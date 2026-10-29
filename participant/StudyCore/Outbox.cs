using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StudyCore;

public enum SendOutcome { Ok, Retry, Permanent }

/// <param name="NetworkDown">연결 자체가 안 됨 — 이번 차례의 나머지 항목도 보내지 않는다.</param>
public sealed record SendResult(SendOutcome Outcome, string? Stored, string? Error, bool NetworkDown = false);

public interface IUploadTransport
{
    Task<SendResult> SendAsync(string envelopeJson, CancellationToken ct);
}

/// <summary>
/// HTTPS POST 전송. Google Apps Script 웹 앱은 POST 처리 후 302 로 결과 주소를 돌려주므로 리디렉션을 따른다(.NET 은 302 를 GET 으로 바꿔 따른다).
/// 시스템 프록시 설정을 그대로 쓴다. 수신기 응답 {"ok":true,"stored":…} / {"ok":false,"error":코드}.
/// </summary>
public sealed class HttpUploadTransport : IUploadTransport, IDisposable
{
    /// <summary>수신기가 명시적으로 거부한 경우 — 다시 보내도 같은 결과라 대기열에서 뺀다. bad_token 은 수신기 설정으로 고칠 수 있어 재시도.</summary>
    private static readonly HashSet<string> PermanentErrors = new() { "too_large", "bad_request", "bad_version", "bad_json", "empty" };

    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public HttpUploadTransport(Uri endpoint, string userAgent, TimeSpan? timeout = null)
    {
        _endpoint = endpoint;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

    public async Task<SendResult> SendAsync(string envelopeJson, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var content = new StringContent(envelopeJson, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SendResult(SendOutcome.Retry, null, $"network: {ex.GetType().Name}: {ex.Message}", NetworkDown: true);
        }

        using (resp)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { return new SendResult(SendOutcome.Retry, null, $"응답 읽기 실패: {ex.Message}"); }
            return Interpret((int)resp.StatusCode, body);
        }
    }

    /// <summary>응답 해석(시험 가능하게 분리).</summary>
    public static SendResult Interpret(int status, string body)
    {
        if (status == 413) return new SendResult(SendOutcome.Permanent, null, "http 413");
        if (status < 200 || status >= 300)
            return new SendResult(SendOutcome.Retry, null, $"http {status}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return new SendResult(SendOutcome.Ok, r.TryGetProperty("stored", out var s) ? s.GetString() : null, null);
            var err = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "unknown" : "unknown";
            return new SendResult(PermanentErrors.Contains(err) ? SendOutcome.Permanent : SendOutcome.Retry, null, err);
        }
        catch
        {
            // Google 오류 페이지(HTML) 등 — 잠깐의 문제일 수 있어 재시도
            var head = body.Length > 80 ? body[..80] : body;
            return new SendResult(SendOutcome.Retry, null, $"JSON 아닌 응답: {head.Replace('\n', ' ')}");
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record OutboxItem(string Id, string Path, string Kind, string Name, int Attempts, DateTimeOffset NextAttemptAt, string? LastError);

public sealed record OutboxRunResult(int Sent, int Retried, int Rejected, int Remaining, string? LastError);

/// <summary>
/// 디스크 전송 대기열(참여자 PC). 마킹 뒤에 만든 암호 봉투만 담는다 — 평문은 들어가지 않는다.
/// outbox/&lt;id&gt;.pcae.json + &lt;id&gt;.state.json(시도 횟수·다음 시도 시각) / sent/&lt;id&gt;.json(영수증) / failed/(수신기가 거부한 것).
/// 재시도 간격 1분→5분→15분→1시간→3시간→6시간. 시간당 전송 상한으로 폭주를 막는다.
/// </summary>
public sealed class Outbox
{
    private static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
    };

    private readonly string _outDir, _sentDir, _failedDir;
    private readonly int _maxPerHour;
    private readonly List<DateTimeOffset> _recentSends = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Outbox(string root, int maxPerHour = 30)
    {
        _outDir = Path.Combine(root, "outbox");
        _sentDir = Path.Combine(root, "sent");
        _failedDir = Path.Combine(root, "failed");
        _maxPerHour = maxPerHour;
        Directory.CreateDirectory(_outDir);
    }

    /// <summary>봉투를 대기열에 넣는다. id = 시각_kind_name (정렬 = 만든 순서).</summary>
    public string Enqueue(string envelopeJson, string kind, string name, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var id = $"{at.UtcDateTime:yyyyMMddHHmmssfff}_{kind}_{name}";
        AtomicFile.WriteAllText(Path.Combine(_outDir, id + ".pcae.json"), envelopeJson);
        return id;
    }

    public IReadOnlyList<OutboxItem> Pending()
    {
        var list = new List<OutboxItem>();
        if (!Directory.Exists(_outDir)) return list;
        foreach (var f in Directory.EnumerateFiles(_outDir, "*.pcae.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var id = Path.GetFileName(f)[..^".pcae.json".Length];
            var parts = id.Split('_', 3);
            var (attempts, next, err) = ReadState(id);
            list.Add(new OutboxItem(id, f, parts.Length > 1 ? parts[1] : "?", parts.Length > 2 ? parts[2] : id, attempts, next, err));
        }
        return list;
    }

    public int PendingCount => Directory.Exists(_outDir) ? Directory.EnumerateFiles(_outDir, "*.pcae.json").Count() : 0;
    public int SentCount => Directory.Exists(_sentDir) ? Directory.EnumerateFiles(_sentDir, "*.json").Count() : 0;
    public int FailedCount => Directory.Exists(_failedDir) ? Directory.EnumerateFiles(_failedDir, "*.pcae.json").Count() : 0;

    /// <summary>이 이름(덤프 폴더 이름)의 kind 봉투가 전송 완료됐는가.</summary>
    public bool IsSent(string kind, string name) =>
        Directory.Exists(_sentDir) && Directory.EnumerateFiles(_sentDir, $"*_{kind}_{name}.json").Any();

    /// <summary>이 이름의 kind 봉투가 아직 대기 중인가.</summary>
    public bool IsPending(string kind, string name) =>
        Directory.Exists(_outDir) && Directory.EnumerateFiles(_outDir, $"*_{kind}_{name}.pcae.json").Any();

    public bool IsFailed(string kind, string name) =>
        Directory.Exists(_failedDir) && Directory.EnumerateFiles(_failedDir, $"*_{kind}_{name}.pcae.json").Any();

    /// <summary>
    /// 기한이 된 항목을 순서대로 보낸다. 연결이 안 되면(NetworkDown) 남은 항목은 다음 차례로 미룬다.
    /// 동시에 두 번 돌지 않는다.
    /// </summary>
    public async Task<OutboxRunResult> ProcessAsync(IUploadTransport transport, CancellationToken ct, int maxThisRun = 10, Func<DateTimeOffset>? clock = null)
    {
        clock ??= () => DateTimeOffset.Now;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int sent = 0, retried = 0, rejected = 0;
            string? lastError = null;
            foreach (var item in Pending())
            {
                if (ct.IsCancellationRequested || sent + retried + rejected >= maxThisRun) break;
                var now = clock();
                if (item.NextAttemptAt > now) continue;
                _recentSends.RemoveAll(t => now - t > TimeSpan.FromHours(1));
                if (_recentSends.Count >= _maxPerHour) { lastError = "시간당 전송 상한"; break; }

                string body;
                try { body = await File.ReadAllTextAsync(item.Path, ct).ConfigureAwait(false); }
                catch (Exception ex) { lastError = ex.Message; continue; }

                _recentSends.Add(now);
                var r = await transport.SendAsync(body, ct).ConfigureAwait(false);
                switch (r.Outcome)
                {
                    case SendOutcome.Ok:
                        Directory.CreateDirectory(_sentDir);
                        AtomicFile.WriteAllText(Path.Combine(_sentDir, item.Id + ".json"),
                            JsonSerializer.Serialize(new { kind = item.Kind, name = item.Name, sentAt = clock(), stored = r.Stored, attempts = item.Attempts + 1 }));
                        TryDelete(item.Path);
                        TryDelete(StatePath(item.Id));
                        sent++;
                        break;
                    case SendOutcome.Permanent:
                        Directory.CreateDirectory(_failedDir);
                        File.Move(item.Path, Path.Combine(_failedDir, Path.GetFileName(item.Path)), overwrite: true);
                        AtomicFile.WriteAllText(Path.Combine(_failedDir, item.Id + ".reason.txt"), r.Error ?? "rejected");
                        TryDelete(StatePath(item.Id));
                        rejected++;
                        lastError = r.Error;
                        break;
                    default:
                        int attempts = item.Attempts + 1;
                        var wait = Backoff[Math.Min(attempts - 1, Backoff.Length - 1)];
                        WriteState(item.Id, attempts, clock() + wait, r.Error);
                        retried++;
                        lastError = r.Error;
                        if (r.NetworkDown) return new OutboxRunResult(sent, retried, rejected, PendingCount, lastError);
                        break;
                }
            }
            return new OutboxRunResult(sent, retried, rejected, PendingCount, lastError);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 대기열 상한(10/29 검토 지적: 크래시 반복·전송 일시중지·수신기 중단 시 무한히 쌓임).
    /// 오래된 것부터 지운다: maxAge 지난 대기 항목 → 개수·바이트 상한 초과분. failed 는 maxAge·100개, 영수증은 90일.
    /// 쓰다 끊긴 임시 파일(1시간 지남)도 지운다. 반환: 지운 대기 항목 수.
    /// </summary>
    public int Trim(long maxBytes, int maxCount, TimeSpan maxAge, DateTimeOffset now)
    {
        int removed = 0;
        _gate.Wait();
        try
        {
            var items = Directory.Exists(_outDir)
                ? new DirectoryInfo(_outDir).EnumerateFiles("*.pcae.json").OrderBy(f => f.Name, StringComparer.Ordinal).ToList()
                : new List<FileInfo>();
            long total = items.Sum(f => f.Length);
            foreach (var f in items.ToList())
            {
                bool tooOld = now - f.LastWriteTime > maxAge;
                bool over = items.Count > maxCount || total > maxBytes;
                if (!tooOld && !over) break;
                total -= f.Length;
                items.Remove(f);
                TryDelete(f.FullName);
                TryDelete(StatePath(f.Name[..^".pcae.json".Length]));
                removed++;
            }
            if (Directory.Exists(_failedDir))
            {
                var failed = new DirectoryInfo(_failedDir).EnumerateFiles().OrderByDescending(f => f.LastWriteTime).ToList();
                foreach (var f in failed.Skip(200).Concat(failed.Where(f => now - f.LastWriteTime > maxAge)))
                    TryDelete(f.FullName);
            }
            if (Directory.Exists(_sentDir))
                foreach (var f in new DirectoryInfo(_sentDir).EnumerateFiles("*.json").Where(f => now - f.LastWriteTime > TimeSpan.FromDays(90)))
                    TryDelete(f.FullName);
            foreach (var dir in new[] { _outDir, _sentDir, _failedDir })
                if (Directory.Exists(dir))
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.tmp").Where(f => now - f.LastWriteTime > TimeSpan.FromHours(1)))
                        TryDelete(f.FullName);
        }
        finally
        {
            _gate.Release();
        }
        return removed;
    }

    private string StatePath(string id) => Path.Combine(_outDir, id + ".state.json");

    private (int Attempts, DateTimeOffset Next, string? Error) ReadState(string id)
    {
        try
        {
            var p = StatePath(id);
            if (!File.Exists(p)) return (0, DateTimeOffset.MinValue, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            return (r.GetProperty("attempts").GetInt32(), r.GetProperty("nextAttemptAt").GetDateTimeOffset(),
                r.TryGetProperty("lastError", out var e) ? e.GetString() : null);
        }
        catch { return (0, DateTimeOffset.MinValue, null); }
    }

    private void WriteState(string id, int attempts, DateTimeOffset next, string? error) =>
        AtomicFile.WriteAllText(StatePath(id), JsonSerializer.Serialize(new { attempts, nextAttemptAt = next, lastError = error }));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 다음 차례에 영수증이 있으면 중복 전송될 뿐 */ }
    }
}
