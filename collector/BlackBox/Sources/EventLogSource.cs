using System.Diagnostics.Eventing.Reader;
using BlackBox.Core;

namespace BlackBox.Sources;

/// <summary>
/// 이벤트 로그 실시간 구독.
/// Application 1000(앱 크래시) / 1001(WER 보고) / 1002(응답 없음),
/// System 41(Kernel-Power) / 4101(TDR) / WHEA 17~20 / BugCheck 1001(BSOD 기록).
/// 수신한 레코드는 전부 버퍼에 넣고, TriggerClassifier가 트리거로 분류한 것만 OnTrigger로 올린다
/// (예: 1001 중 RADAR_PRE_LEAK 같은 비크래시 보고는 버퍼에만 남는다 — 2026-10-28 재분류 반영).
/// </summary>
public sealed class EventLogSource : ISignalSource
{
    private const string AppCond = "EventID=1000 or EventID=1001 or EventID=1002";
    private const string SysCond =
        "(EventID=41 or EventID=4101) " +
        "or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID>=17 and EventID<=20)) " +
        "or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)";

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
        return SourceStatus.Ok(Name, errors.Count > 0 ? "부분 구독: " + string.Join(" / ", errors) : "Application 1000/1001/1002 + System 41/4101/WHEA/BugCheck 구독 중");
    }

    private void Handle(string logName, EventRecord rec)
    {
        var receivedAt = DateTimeOffset.Now;
        string msg = "";
        try { msg = rec.FormatDescription() ?? ""; } catch { }
        msg = msg.Replace("\r\n", " ").Replace("\n", " ");
        if (msg.Length > 1000) msg = msg[..1000] + "...";

        // 이벤트별 속성 위치 (2026-10-28 이 PC에서 확인):
        //  1000: [0]앱 [3]모듈 [6]예외코드 [8]PID(16진) [10]앱 경로 [11]모듈 경로 [12]ReportId
        //  1001: [2]WER 유형 [5]P1 앱 [8]P4 모듈
        //  1002: [0]앱 [2]PID(16진) [5]앱 경로 [6]ReportId
        string? pname = null, module = null, modulePath = null, exc = null, reportId = null, werType = null;
        int? pid = null;
        try
        {
            var p = rec.Properties;
            switch (logName, rec.Id)
            {
                case ("Application", 1000):
                    pname = Str(p, 0); module = Str(p, 3); exc = Str(p, 6);
                    pid = ParsePid(Val(p, 8)); modulePath = Str(p, 11); reportId = Str(p, 12);
                    break;
                case ("Application", 1001):
                    werType = Str(p, 2); pname = Str(p, 5); module = Str(p, 8);
                    break;
                case ("Application", 1002):
                    pname = Str(p, 0); pid = ParsePid(Val(p, 2)); modulePath = Str(p, 5); reportId = Str(p, 6);
                    break;
            }
        }
        catch { /* 속성 형식이 다른 레코드 — 트리거 분류는 계속한다 */ }

        OnSignal?.Invoke(SignalEvent.Create(Name, new
        {
            log = logName,
            id = rec.Id,
            provider = rec.ProviderName,
            subject = pname,
            faultingModule = module,
            werEventType = werType,
            exceptionCode = exc,
            message = msg,
        }));

        var cls = TriggerClassifier.Classify(logName, rec.Id, rec.ProviderName, werType);
        if (!cls.IsTrigger) return;

        OnTrigger?.Invoke(new EventLogTrigger(
            receivedAt, logName, rec.Id, rec.ProviderName, rec.TimeCreated, msg, pname, pid, module, cls.Kind!,
            werType, exc, modulePath, reportId, cls));
    }

    /// <summary>유의성 점수의 "재발" 요소용: 최근 window 동안 같은 프로그램의 1000(앱 크래시) 건수.</summary>
    public static int CountRecentCrashes(string processName, TimeSpan window)
    {
        try
        {
            long ms = (long)window.TotalMilliseconds;
            var q = new EventLogQuery("Application", PathType.LogName,
                $"*[System[EventID=1000 and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using var reader = new EventLogReader(q);
            int n = 0;
            for (EventRecord? ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
            {
                using (ev)
                {
                    if (string.Equals(Str(ev.Properties, 0), processName, StringComparison.OrdinalIgnoreCase)) n++;
                }
            }
            return n;
        }
        catch { return 0; }
    }

    private static object? Val(IList<EventProperty> p, int i) => i < p.Count ? p[i].Value : null;
    private static string? Str(IList<EventProperty> p, int i) => Val(p, i) as string is { Length: > 0 } s ? s : null;

    private static int? ParsePid(object? value)
    {
        // 이벤트 1000/1002의 PID는 "0x 접두사 없는 16진수 문자열" (예: "5650" = 22096). 십진으로 읽으면 안 된다.
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
