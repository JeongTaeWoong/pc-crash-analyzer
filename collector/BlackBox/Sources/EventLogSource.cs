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
    // 공급자까지 제한한다 — ID 1000 은 다른 프로그램도 쓴다(10/29 전체 로그 재검증: ".NET Runtime" 1000 2건이 앱 크래시로 잡혔음)
    private const string AppCond =
        "(Provider[@Name='Application Error'] and EventID=1000) " +
        "or (Provider[@Name='Windows Error Reporting'] and EventID=1001) " +
        "or (Provider[@Name='Application Hang'] and EventID=1002)";
    private const string SysCond =
        "(EventID=41 or EventID=4101) " +
        "or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID>=17 and EventID<=20)) " +
        "or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) " +
        "or (Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and EventID=37)"; // CPU 속도 제한(펌웨어) — 버퍼만, 트리거 아님 (느려짐 C 메커니즘)

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
        return SourceStatus.Ok(Name, errors.Count > 0 ? "부분 구독: " + string.Join(" / ", errors) : "Application 1000/1001/1002 + System 41/4101/WHEA/BugCheck + Kernel-Processor-Power 37 구독 중");
    }

    private void Handle(string logName, EventRecord rec)
    {
        var (payload, _, trigger) = Inspect(logName, rec, DateTimeOffset.Now);
        OnSignal?.Invoke(SignalEvent.Create(Name, payload));
        if (trigger != null) OnTrigger?.Invoke(trigger);
    }

    /// <summary>
    /// 레코드 1건을 버퍼 페이로드·트리거 분류·트리거로 바꾼다. 실시간 구독과 과거 로그 재검증(--verify-eventlog)이 같은 코드를 쓴다 —
    /// 블루스크린·전원 차단·TDR 을 일부러 일으키지 않고도 이 PC 에 남은 실제 기록으로 해석을 검증하기 위해(2026-10-29).
    /// </summary>
    public static (object Payload, TriggerClass Class, EventLogTrigger? Trigger) Inspect(string logName, EventRecord rec, DateTimeOffset receivedAt)
    {
        string msg = "";
        try { msg = rec.FormatDescription() ?? ""; } catch { }
        msg = msg.Replace("\r\n", " ").Replace("\n", " ");
        if (msg.Length > 1000) msg = msg[..1000] + "...";

        // 이벤트별 속성 위치 (2026-10-28 이 PC에서 확인):
        //  1000: [0]앱 [3]모듈 [6]예외코드 [8]PID(16진) [10]앱 경로 [11]모듈 경로 [12]ReportId
        //  1001: [2]WER 유형 [5]P1 앱 [8]P4 모듈
        //  1002: [0]앱 [2]PID(16진) [5]앱 경로 [6]ReportId
        //  41(Kernel-Power): [0]BugcheckCode [5]SleepInProgress [6]PowerButtonTimestamp (10/29 XML 로 이름 확인)
        //  1001 LiveKernelEvent: [5]P1 = 라이브 덤프 코드(예 "141")
        string? pname = null, module = null, modulePath = null, exc = null, reportId = null, werType = null;
        int? pid = null;
        EventDetail? detail = null;
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
                    if (werType?.StartsWith("LiveKernelEvent", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        detail = new EventDetail(LiveKernelCode: pname);
                        pname = $"LiveKernelEvent 0x{pname}"; // 프로세스 이름이 아니다 — 엔진이 앱 크래시로 세지 않게
                    }
                    break;
                case ("Application", 1002):
                    pname = Str(p, 0); pid = ParsePid(Val(p, 2)); modulePath = Str(p, 5); reportId = Str(p, 6);
                    break;
                case ("System", 41) when rec.ProviderName == TriggerClassifier.KernelPowerProvider:
                    long bc = ToLong(Val(p, 0));
                    int sleep = (int)ToLong(Val(p, 5));
                    bool button = ToLong(Val(p, 6)) != 0;
                    detail = new EventDetail(bc, sleep, button);
                    pname = $"BugcheckCode {bc}"; // 엔진 signals::hasNonZeroBugcheckCode 형식
                    break;
            }
        }
        catch { /* 속성 형식이 다른 레코드 — 트리거 분류는 계속한다 */ }

        var payload = new
        {
            log = logName,
            id = rec.Id,
            provider = rec.ProviderName,
            subject = pname,
            faultingModule = module,
            werEventType = werType,
            exceptionCode = exc,
            bugcheckCode = detail?.BugcheckCode,
            sleepInProgress = detail?.SleepInProgress,
            powerButtonHeld = detail?.BugcheckCode != null ? detail.PowerButtonHeld : (bool?)null,
            recordId = rec.RecordId,
            eventTime = rec.TimeCreated,
            message = msg,
        };

        var cls = TriggerClassifier.Classify(logName, rec.Id, rec.ProviderName, werType, detail);
        EventLogTrigger? trigger = cls.IsTrigger
            ? new EventLogTrigger(receivedAt, logName, rec.Id, rec.ProviderName, rec.TimeCreated, msg, pname, pid, module, cls.Kind!,
                werType, exc, modulePath, reportId, cls, rec.RecordId)
            : null;
        return (payload, cls, trigger);
    }

    /// <summary>
    /// 부팅 직후 기록되는 사건(41 비정상 종료·BugCheck 1001·WHEA)은 수집기가 켜지기 전에 써지므로 실시간 구독으로는 절대 못 받는다(10/29 발견).
    /// 시작 시 "이번 부팅 이후"의 System 기록을 한 번 읽어 같은 경로(Handle)로 흘린다. 이미 덤프한 레코드(alreadyDumped)는 건너뛴다 — 같은 부팅에서 수집기를 다시 켜도 중복 덤프 없음.
    /// 디스크 쓰기 없음(읽기만). 반환: 흘려보낸 건수.
    /// </summary>
    public int ReplaySinceBoot(Func<long, bool> alreadyDumped)
    {
        int n = 0;
        try
        {
            var bootAt = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(2);
            long ms = (long)(DateTime.Now - bootAt).TotalMilliseconds;
            const string bootCond =
                "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) " +
                "or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) " +
                "or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID>=17 and EventID<=20))";
            var q = new EventLogQuery("System", PathType.LogName, $"*[System[({bootCond}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            using var reader = new EventLogReader(q);
            for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
            {
                using (rec)
                {
                    if (rec.RecordId is { } id && alreadyDumped(id)) continue;
                    try { Handle("System", rec); n++; }
                    catch { /* 재생 실패로 죽지 않는다 */ }
                }
            }
        }
        catch { /* 로그 읽기 실패 — 실시간 구독은 계속 */ }
        return n;
    }

    /// <summary>
    /// 과거 로그 재검증: 실시간 구독과 같은 XPath 조건으로 최근 days 일(0 = 로그 전체)을 읽어 Inspect 결과를 모은다. 읽기 전용.
    /// </summary>
    public static object VerifyHistory(int days)
    {
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var nonTrigger = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var samples = new Dictionary<string, object>();
        var parseGaps = new List<object>();
        var errors = new List<string>();
        int total = 0;
        DateTime? oldest = null;
        string timeCond = days > 0 ? $" and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(days).TotalMilliseconds}]" : "";
        foreach (var (log, cond) in new[] { ("Application", AppCond), ("System", SysCond) })
        {
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, $"*[System[({cond}){timeCond}]]");
                using var reader = new EventLogReader(q);
                for (EventRecord? rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
                {
                    using (rec)
                    {
                        total++;
                        if (rec.TimeCreated is { } tc && (oldest == null || tc < oldest)) oldest = tc;
                        var (_, cls, trig) = Inspect(log, rec, DateTimeOffset.Now);
                        if (trig != null)
                        {
                            byKind[trig.Kind] = byKind.GetValueOrDefault(trig.Kind) + 1;
                            // 앱 크래시인데 프로세스 이름을 못 뽑았으면 해석 결함 후보
                            if (trig.Kind is "crash-1000" or "hang-1002" && (trig.ProcessName == null || (trig.Kind == "crash-1000" && trig.Pid == null)) && parseGaps.Count < 5)
                                parseGaps.Add(new { trig.Kind, rec.Id, at = rec.TimeCreated, trig.ProcessName, trig.Pid });
                            if (!samples.ContainsKey(trig.Kind))
                                samples[trig.Kind] = new
                                {
                                    at = rec.TimeCreated, trig.EventId, trig.Provider, trig.ProcessName, trig.Pid, trig.FaultingModule,
                                    trig.ExceptionCode, trig.WerEventType, symptom = cls.SymptomType, scope = cls.Scope, baseScore = cls.BaseScore,
                                    message = trig.Message.Length > 160 ? trig.Message[..160] + "..." : trig.Message,
                                };
                        }
                        else
                        {
                            string key = $"{log}/{rec.Id}/{rec.ProviderName}";
                            nonTrigger[key] = nonTrigger.GetValueOrDefault(key) + 1;
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"{log}: {ex.GetType().Name}: {ex.Message}"); }
        }
        return new
        {
            mode = "verify-eventlog",
            days = days > 0 ? days : (int?)null,
            oldestRecord = oldest,
            recordsRead = total,
            triggersByKind = byKind,
            notTriggered = nonTrigger,
            parseGaps,
            samplePerKind = samples,
            errors,
        };
    }

    /// <summary>유의성 점수의 "재발" 요소용: 최근 window 동안 같은 프로그램의 1000(앱 크래시) 건수.</summary>
    public static int CountRecentCrashes(string processName, TimeSpan window)
    {
        try
        {
            long ms = (long)window.TotalMilliseconds;
            var q = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
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

    private static long ToLong(object? v) => v switch
    {
        null => 0,
        long l => l,
        ulong ul => unchecked((long)ul),
        int i => i,
        uint u => u,
        byte b => b,
        string s when s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => Convert.ToInt64(s[2..], 16),
        string s when long.TryParse(s, out var x) => x,
        _ => 0,
    };
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
