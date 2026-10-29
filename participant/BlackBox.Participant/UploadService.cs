using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BlackBox.Core;
using BlackBox.Marking;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>
/// 덤프 → zip → 암호 봉투 → 디스크 대기열 → HTTPS 전송. 하트비트는 메모리에서 바로 보낸다(실패하면 다음 차례에).
/// "전송 일시중지" 중에는 네트워크를 전혀 쓰지 않는다(대기열에만 쌓인다).
/// </summary>
public sealed class UploadService : IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(8);

    private readonly StudyConfig _study;
    private readonly Func<ParticipantState> _state;
    private readonly RSA _publicKey;
    private readonly ECDsa _signer;
    private readonly IUploadTransport _transport;
    private readonly Func<object> _heartbeatPayload;
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public Outbox Outbox { get; }
    public string AppVersion { get; }
    public OutboxRunResult? LastRun { get; private set; }
    public DateTimeOffset? LastHeartbeatOk { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>대기열 상태가 바뀜(작업 스레드에서 호출).</summary>
    public event Action? Changed;

    public UploadService(StudyConfig study, Func<ParticipantState> state, string outboxRoot, IUploadTransport transport,
        Func<object> heartbeatPayload, string appVersion)
    {
        _study = study;
        _state = state;
        _publicKey = study.CreatePublicKey();
        _signer = state().CreateSigner();
        _transport = transport;
        _heartbeatPayload = heartbeatPayload;
        AppVersion = appVersion;
        Outbox = new Outbox(outboxRoot);
    }

    private EnvelopeHeader Header(string kind, string name) =>
        new(_study.StudyId, _state().ParticipantId, kind, name, DateTimeOffset.Now.ToString("o"), _study.KeyId, "participant/" + AppVersion);

    private string Seal(string kind, string name, byte[] zip) => Envelope.Seal(Header(kind, name), zip, _publicKey, _study.Token, _signer);

    /// <summary>대기열 상한 — 로컬 기록(약 200MB)과 합쳐 동의 화면의 "약 300MB" 안. 30일 지난 미전송 봉투도 지운다.</summary>
    public const long OutboxMaxBytes = 100L * 1024 * 1024;
    public const int OutboxMaxCount = 500;
    public static readonly TimeSpan OutboxMaxAge = TimeSpan.FromDays(30);

    public int TrimOutbox() => Outbox.Trim(OutboxMaxBytes, OutboxMaxCount, OutboxMaxAge, DateTimeOffset.Now);

    /// <summary>덤프 폴더를 봉해 대기열에 넣는다. 평문은 대기열에 들어가지 않는다.</summary>
    public string EnqueueDump(string dumpDir)
    {
        var name = Path.GetFileName(dumpDir);
        var extra = new Dictionary<string, string>
        {
            ["upload.json"] = Json.Serialize(new { app = "participant/" + AppVersion, bundledAt = DateTimeOffset.Now, consentVersion = _state().ConsentVersion }),
        };
        var zip = BundleBuilder.FromDirectory(dumpDir, extra, out bool truncated);
        if (truncated) OpsLog.Write($"[upload] {name}: 전송 상한 초과 — events.json 생략");
        var id = Outbox.Enqueue(Seal("dump", name, zip), "dump", name);
        OpsLog.Write($"[upload] 대기열 추가 {id} ({zip.Length / 1024.0:0.#}KB zip)");
        Kick();
        return id;
    }

    /// <summary>나중에 남긴 메모(report.json)를 덤프와 같은 이름으로 보낸다. 팀 도구가 덤프 폴더에 합친다.</summary>
    public string EnqueueAnnotation(string dumpName, string reportJson)
    {
        var zip = BundleBuilder.FromEntries(new Dictionary<string, string> { ["report.json"] = reportJson });
        var id = Outbox.Enqueue(Seal("annotation", dumpName, zip), "annotation", dumpName);
        OpsLog.Write($"[upload] 메모 대기열 추가 {id}");
        Kick();
        return id;
    }

    public async Task<SendResult> SendHeartbeatAsync(CancellationToken ct)
    {
        var name = $"hb-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        // 상태 문구·운영 로그에 경로·이름이 섞일 수 있어 덤프와 같은 치환을 거친다
        var payload = Sanitizer.SanitizeJson(Json.Serialize(_heartbeatPayload(), indented: true));
        var zip = BundleBuilder.FromEntries(new Dictionary<string, string> { ["heartbeat.json"] = payload });
        var r = await _transport.SendAsync(Seal("heartbeat", name, zip), ct).ConfigureAwait(false);
        if (r.Outcome == SendOutcome.Ok) LastHeartbeatOk = DateTimeOffset.Now;
        OpsLog.Write($"[heartbeat] {r.Outcome} {r.Error}");
        return r;
    }

    /// <summary>참여 중단: 팀에 "내 기록을 지워 주세요" 요청(즉시 전송, 대기열 안 씀).</summary>
    public async Task<SendResult> SendWithdrawAsync(bool deleteServerData, CancellationToken ct)
    {
        var body = Json.Serialize(new { participantId = _state().ParticipantId, requestedAt = DateTimeOffset.Now, deleteServerData });
        var zip = BundleBuilder.FromEntries(new Dictionary<string, string> { ["withdraw.json"] = body });
        return await _transport.SendAsync(Seal("withdraw", $"withdraw-{DateTimeOffset.Now:yyyyMMdd-HHmmss}", zip), ct).ConfigureAwait(false);
    }

    public void Kick()
    {
        try { if (_kick.CurrentCount == 0) _kick.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>전송 반복을 멈추고 끝날 때까지(최대 5초) 기다린다. 이후에는 FlushAsync·SendWithdrawAsync 만 쓴다.</summary>
    public async Task StopLoopAsync()
    {
        _cts.Cancel();
        if (_loop != null) await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
    }

    public void Start(TimeSpan firstHeartbeatDelay)
    {
        _loop ??= Task.Run(() => Loop(firstHeartbeatDelay, _cts.Token));
    }

    private async Task Loop(TimeSpan firstHeartbeatDelay, CancellationToken ct)
    {
        var nextHeartbeat = DateTimeOffset.Now + firstHeartbeatDelay;
        while (!ct.IsCancellationRequested)
        {
            if (!_state().UploadsPaused)
            {
                try
                {
                    LastRun = await Outbox.ProcessAsync(_transport, ct).ConfigureAwait(false);
                    if (LastRun.Sent > 0 || LastRun.Retried > 0 || LastRun.Rejected > 0)
                        OpsLog.Write($"[upload] 보냄 {LastRun.Sent} · 재시도 {LastRun.Retried} · 거부 {LastRun.Rejected} · 남음 {LastRun.Remaining} {LastRun.LastError}");
                    LastError = LastRun.LastError;
                    if (DateTimeOffset.Now >= nextHeartbeat)
                    {
                        var hb = await SendHeartbeatAsync(ct).ConfigureAwait(false);
                        nextHeartbeat = DateTimeOffset.Now + (hb.Outcome == SendOutcome.Ok ? HeartbeatInterval : TimeSpan.FromMinutes(15));
                        if (hb.Outcome != SendOutcome.Ok) LastError = hb.Error;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LastError = $"{ex.GetType().Name}: {ex.Message}";
                    OpsLog.Write($"[upload-error] {LastError}");
                }
            }
            try { Changed?.Invoke(); } catch { }
            // 다음 깨어남: 2분 또는 하트비트 시각 중 이른 쪽(일시중지 중에는 2분)
            var untilHeartbeat = nextHeartbeat - DateTimeOffset.Now;
            var wait = _state().UploadsPaused ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromTicks(Math.Clamp(untilHeartbeat.Ticks, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(2).Ticks));
            try { await _kick.WaitAsync(wait, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>끝내기 전 마지막 한 번 — 방금 생긴 덤프를 보낼 기회(짧은 제한 시간).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        if (_state().UploadsPaused) return;
        using var cts = new CancellationTokenSource(timeout);
        try { await Outbox.ProcessAsync(_transport, cts.Token).ConfigureAwait(false); }
        catch { /* 다음 실행 때 보낸다 */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _publicKey.Dispose();
        _signer.Dispose();
        (_transport as IDisposable)?.Dispose();
    }
}

/// <summary>참여자 PC 의 로컬 기록 관리 — 데이터 폴더 안, 우리가 만든 이름 형식의 폴더만 지운다.</summary>
public static class LocalData
{
    private static readonly Regex DumpDirRx = new(@"^\d{8}-\d{6}-[a-z0-9-]+$");

    public const long SoftCapBytes = 200L * 1024 * 1024;
    public const int SoftCapCount = 300;
    public const long HardCapBytes = 400L * 1024 * 1024;
    public static readonly TimeSpan SentRetention = TimeSpan.FromDays(30);

    public static List<DirectoryInfo> DumpDirs(string dumpRoot) =>
        Directory.Exists(dumpRoot)
            ? new DirectoryInfo(dumpRoot).EnumerateDirectories().Where(d => DumpDirRx.IsMatch(d.Name)).OrderBy(d => d.Name, StringComparer.Ordinal).ToList()
            : new List<DirectoryInfo>();

    /// <summary>
    /// 전송 끝난 30일 지난 기록 삭제 → 개수·용량이 상한을 넘으면 전송 끝난 오래된 것부터 삭제 → 그래도 하드 상한을 넘으면 오래된 것부터(미전송 포함).
    /// 반환: 지운 폴더 수.
    /// </summary>
    public static int Prune(string dumpRoot, Func<string, bool> isSent, DateTimeOffset now)
    {
        int removed = 0;
        var dirs = DumpDirs(dumpRoot).Select(d => (Dir: d, Size: SizeOf(d))).ToList();
        foreach (var (d, _) in dirs.ToList())
        {
            if (isSent(d.Name) && now - d.CreationTime > SentRetention && TryDelete(d)) { removed++; dirs.RemoveAll(x => x.Dir.FullName == d.FullName); }
        }
        long total = dirs.Sum(x => x.Size);
        foreach (var (d, size) in dirs.ToList())
        {
            if (dirs.Count <= SoftCapCount && total <= SoftCapBytes) break;
            if (!isSent(d.Name)) continue;
            if (TryDelete(d)) { removed++; total -= size; dirs.RemoveAll(x => x.Dir.FullName == d.FullName); }
        }
        foreach (var (d, size) in dirs.ToList())
        {
            if (total <= HardCapBytes) break;
            if (TryDelete(d)) { removed++; total -= size; dirs.RemoveAll(x => x.Dir.FullName == d.FullName); }
        }
        return removed;
    }

    /// <summary>참여 중단 시: 데이터 폴더 안의 우리 하위 폴더·파일만 지운다(다른 파일은 건드리지 않는다).</summary>
    public static void DeleteAll(string dataDir)
    {
        // 우리 데이터 폴더라는 표시(participant.json)가 없으면 아무것도 지우지 않는다 — --data-dir 오입력 대비(10/29 검토 지적)
        if (!File.Exists(ParticipantState.PathIn(dataDir))) return;
        foreach (var sub in new[] { "dumps", "outbox", "sent", "failed", "selftest" })
        {
            var p = Path.Combine(dataDir, sub);
            try { if (Directory.Exists(p)) Directory.Delete(p, recursive: true); } catch { }
        }
        foreach (var f in new[] { "participant.json", "participant.json.broken" })
        {
            try { File.Delete(Path.Combine(dataDir, f)); } catch { }
        }
        try { if (Directory.Exists(dataDir) && !Directory.EnumerateFileSystemEntries(dataDir).Any()) Directory.Delete(dataDir); } catch { }
    }

    private static long SizeOf(DirectoryInfo d)
    {
        try { return d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
    }

    private static bool TryDelete(DirectoryInfo d)
    {
        try { d.Delete(recursive: true); return true; } catch { return false; }
    }
}
