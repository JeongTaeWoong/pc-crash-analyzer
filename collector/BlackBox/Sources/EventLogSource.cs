using System.Diagnostics.Eventing.Reader;
using BlackBox.Core;

namespace BlackBox.Sources;

/// <summary>
/// 스파이크 A에서 검증된 이벤트 로그 실시간 구독.
/// Application 1000(앱 크래시)/1001(WER), System 41(Kernel-Power)/4101(TDR)/WHEA 17~20.
/// 수신 이벤트는 전부 자동 마킹 트리거이기도 하다 (쿼리가 그 이벤트만 구독하므로).
/// </summary>
public sealed class EventLogSource : ISignalSource
{
    private const string AppCond = "EventID=1000 or EventID=1001";
    private const string SysCond = "(EventID=41 or EventID=4101) or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID>=17 and EventID<=20))";

    private readonly List<EventLogWatcher> _watchers = new();

    public string Name => "eventlog";

    public event Action<SignalEvent>? OnSignal;

    /// <summary>자동 마킹용 구조화 트리거 (버퍼용 OnSignal과 별개).</summary>
    public event Action<EventLogTrigger>? OnTrigger;

    public SourceStatus Start()
    {
        var errors = new List<string>();
        foreach (var (log, cond) in new[] { ("Application", AppCond), ("System", SysCond) })
        {
            try
            {
                var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, $"*[System[{cond}]]"));
                string logName = log;
                watcher.EventRecordWritten += (_, e) =>
                {
                    if (e.EventRecord is not { } rec) return;
                    using (rec)
                    {
                        try { Handle(logName, rec); }
                        catch { /* 수집 실패로 죽지 않는다 */ }
                    }
                };
                watcher.Enabled = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                errors.Add($"{log}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (_watchers.Count == 0)
            return SourceStatus.Unavailable(Name, string.Join(" / ", errors));
        return SourceStatus.Ok(Name, errors.Count > 0 ? "부분 구독: " + string.Join(" / ", errors) : "Application+System 구독 중");
    }

    private void Handle(string logName, EventRecord rec)
    {
        var receivedAt = DateTimeOffset.Now;
        string msg = "";
        try { msg = rec.FormatDescription() ?? ""; } catch { }
        msg = msg.Replace("\r\n", " ").Replace("\n", " ");
        if (msg.Length > 1000) msg = msg[..1000] + "...";

        // 이벤트 1000(Application Error)의 표준 속성: [0]=앱 이름, [3]=모듈 이름, [8]=프로세스 ID(16진수 문자열)
        string? pname = null;
        string? faultingModule = null;
        int? pid = null;
        if (rec.Id == 1000)
        {
            try
            {
                var props = rec.Properties;
                if (props.Count > 0) pname = props[0].Value as string;
                if (props.Count > 3) faultingModule = props[3].Value as string;
                if (props.Count > 8) pid = ParsePid(props[8].Value);
            }
            catch { }
        }

        OnSignal?.Invoke(SignalEvent.Create(Name, new
        {
            log = logName,
            id = rec.Id,
            provider = rec.ProviderName,
            subject = pname,
            faultingModule,
            message = msg,
        }));

        string? kind = KindOf(logName, rec.Id, rec.ProviderName);
        if (kind == null) return;

        OnTrigger?.Invoke(new EventLogTrigger(
            receivedAt, logName, rec.Id, rec.ProviderName, rec.TimeCreated, msg, pname, pid, faultingModule, kind));
    }

    private static string? KindOf(string log, int id, string? provider) => (log, id) switch
    {
        ("Application", 1000) => "crash-1000",
        ("Application", 1001) => "crash-1001",
        ("System", 41) => "power-41",
        ("System", 4101) => "display-4101",
        _ when log == "System" && id is >= 17 and <= 20 && provider == "Microsoft-Windows-WHEA-Logger" => "whea",
        _ => null,
    };

    private static int? ParsePid(object? value)
    {
        // 이벤트 1000의 P9(프로세스 ID)는 "0x 접두사 없는 16진수 문자열"이다 (예: "5650" = 22096).
        // 메시지 렌더링에서만 0x가 붙는다. 십진수로 읽으면 엉뚱한 PID가 된다 — 2026-10-08 테스트에서 확인.
        try
        {
            return value switch
            {
                int i => i,
                uint u => (int)u,
                string s when s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase) => Convert.ToInt32(s.Trim()[2..], 16),
                string s when s.Trim().Length > 0 => Convert.ToInt32(s.Trim(), 16),
                _ => null,
            };
        }
        catch { return null; }
    }

    public void Stop()
    {
        foreach (var w in _watchers)
            try { w.Enabled = false; } catch { }
    }

    public void Dispose()
    {
        Stop();
        foreach (var w in _watchers)
            try { w.Dispose(); } catch { }
        _watchers.Clear();
    }
}
