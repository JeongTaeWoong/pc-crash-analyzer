using System.Text.Json;
using BlackBox.Core;
using BlackBox.Sources;

namespace BlackBox.Marking;

/// <summary>덤프 1건 완료 알림. 참여자판이 전송 대기열에 넣고 알림을 띄운다 — 수집기 자신은 업로드하지 않는다(CLAUDE.md 1).</summary>
public sealed record DumpRecord(string Dir, string Kind, DateTimeOffset MarkTime, bool Auto, string? ProcessName,
    int? Score, string? Tier, string? Symptom, string? Target);

/// <summary>
/// 마킹 처리: T 시점에 상태 스냅샷을 뜨기 시작하고, T+post까지 수집을 계속한 뒤
/// 버퍼의 [T-pre, T+post] 구간과 스냅샷을 덤프한다.
/// 자동 트리거는 (1) denylist, (2) WER 후속 보고 중복 제거(1001↔1000, hang-1001↔1002, 180초),
/// (3) 쿨다운(10초) 병합을 거친다. 덤프 시 유의성 점수를 계산해 meta·엔진 스냅샷에 넣는다 — 낮은 점수도 덤프는 남긴다(2026-10-28 결정: 숨기기).
/// </summary>
public sealed class MarkService
{
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(180);
    /// <summary>
    /// 같은 대상(프로그램 이름, 없으면 종류)의 자동 덤프는 1시간에 6건까지. 크래시 반복(10초마다 재시작·크래시)이
    /// 하루 수천 개 덤프로 디스크·전송을 채우는 것을 막는다(10/29 검토 지적). 넘친 것은 트리거 줄에 suppressed 로 남는다.
    /// </summary>
    public const int AutoPerTargetPerHour = 6;

    private readonly RingBuffer _buffer;
    private readonly StateSnapshotter _snapshotter;
    private readonly DumpWriter _writer;
    private readonly TimeSpan _pre;
    private readonly TimeSpan _post;
    private readonly IReadOnlyList<SourceStatus> _statuses;
    private readonly BlackBoxConfig _config;
    private readonly MinuteRollup? _rollup;
    private readonly object _lock = new();
    private readonly List<Task> _pending = new();
    private readonly Dictionary<string, (string Kind, DateTimeOffset At)> _recentByProcess = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _autoByTarget = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastAutoMark = DateTimeOffset.MinValue;
    private List<EventLogTrigger>? _mergeTarget;

    /// <summary>덤프를 디스크에 다 쓴 직후(작업 스레드에서) 호출된다. 처리기 예외는 삼킨다.</summary>
    public event Action<DumpRecord>? DumpWritten;

    public MarkService(RingBuffer buffer, StateSnapshotter snapshotter, DumpWriter writer,
        TimeSpan pre, TimeSpan post, IReadOnlyList<SourceStatus> statuses, BlackBoxConfig config, MinuteRollup? rollup = null)
    {
        _rollup = rollup;
        _buffer = buffer;
        _snapshotter = snapshotter;
        _writer = writer;
        _pre = pre;
        _post = post;
        _statuses = statuses;
        _config = config;
    }

    public void MarkManual(string via) =>
        StartMark("manual", new { type = "manual", via }, null, null, null, null);

    /// <summary>
    /// 증상·대상을 지정한 수동 마킹 (콘솔 `m slow blender`, 이후 UI 증상 대화상자가 같은 경로를 쓴다).
    /// 대상이 있으면 롤업 초점·마지막 모듈 목록을 그 프로그램 이름으로 잡는다 — 전경(콘솔 창)이 초점이 되는 문제(10/29) 해결.
    /// </summary>
    public void MarkManual(string via, ManualMark manual, int? fgPidHint = null)
    {
        var m = NormalizeManual(manual);
        StartMark("manual", new { type = "manual", via, symptom = m.Symptom, target = m.Target, memo = m.Memo }, null, null, m.Target, null, m, fgPidHint);
    }

    /// <summary>
    /// 증상 대화상자용 마킹(참여자판): 마킹 시각·전경 PID 는 지금 고정하고, 증상·대상·메모는 대화상자 답을 덤프 직전까지 기다린다.
    /// 버퍼 구간은 T+post 에 먼저 잘라 두므로 답이 늦어도 T-pre 이벤트가 밀려나지 않는다. maxWait(마킹 기준) 안에 답이 없으면 증상 없이 덤프한다.
    /// </summary>
    public DateTimeOffset MarkManualDeferred(string via, int? fgPidHint, Task<ManualMark?> details, TimeSpan maxWait) =>
        StartMark("manual", new { type = "manual", via }, null, null, null, null, null, fgPidHint, details, maxWait, via);

    /// <summary>대상 이름에 .exe 를 붙이고, 알려진 증상만 남기고, 메모는 제어 문자를 지워 300자로 자른다.</summary>
    public static ManualMark NormalizeManual(ManualMark m)
    {
        string? target = m.Target?.Trim() is { Length: > 0 } t
            ? (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : t + ".exe")
            : null;
        string? symptom = m.Symptom?.Trim() is { Length: > 0 } sy && ManualMark.KnownSymptoms.Contains(sy, StringComparer.OrdinalIgnoreCase)
            ? sy.ToLowerInvariant()
            : null;
        string? memo = null;
        if (m.Memo != null)
        {
            var chars = m.Memo.Where(c => !char.IsControl(c) || c == ' ').ToArray();
            var s = new string(chars).Trim();
            if (s.Length > 300) s = s[..300];
            memo = s.Length > 0 ? s : null;
        }
        return new ManualMark(symptom, target, memo);
    }

    public void OnAutoTrigger(EventLogTrigger t)
    {
        if (_config.IsDenied(t.ProcessName))
        {
            Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, processName = t.ProcessName, suppressed = true, reason = "denylist" })}");
            return;
        }

        List<EventLogTrigger> merged;
        lock (_lock)
        {
            var now = DateTimeOffset.Now;

            // WER 후속 보고(1001)는 같은 프로그램의 1000/1002가 직전에 있었으면 같은 사건 — 별도 덤프를 만들지 않는다
            if (t.ProcessName != null && _recentByProcess.TryGetValue(t.ProcessName, out var r) && now - r.At < DedupeWindow)
            {
                bool dup = (t.Kind == "crash-1001" && r.Kind == "crash-1000") || (t.Kind == "hang-1001" && r.Kind == "hang-1002");
                if (dup)
                {
                    Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, id = t.EventId, werEventType = t.WerEventType, processName = t.ProcessName, suppressed = true, reason = $"같은 프로그램의 {r.Kind}와 중복(WER 후속 보고)" })}");
                    return;
                }
            }
            if (t.Kind is "crash-1000" or "hang-1002" && t.ProcessName != null)
                _recentByProcess[t.ProcessName] = (t.Kind, now);

            var targetKey = t.ProcessName ?? t.Kind;
            if (!_autoByTarget.TryGetValue(targetKey, out var times)) _autoByTarget[targetKey] = times = new List<DateTimeOffset>();
            times.RemoveAll(x => now - x > TimeSpan.FromHours(1));
            if (times.Count >= AutoPerTargetPerHour)
            {
                Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, processName = t.ProcessName, suppressed = true, reason = $"같은 대상 1시간 {AutoPerTargetPerHour}건 초과(반복 크래시) — 덤프 생략" })}");
                return;
            }
            times.Add(now);

            if (now - _lastAutoMark < AutoCooldown && _mergeTarget != null)
            {
                _mergeTarget.Add(t);
                Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, id = t.EventId, suppressed = true, reason = "쿨다운 내 — 직전 마킹에 병합" })}");
                return;
            }
            _lastAutoMark = now;
            merged = new List<EventLogTrigger>();
            _mergeTarget = merged;
        }

        Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, log = t.Log, id = t.EventId, werEventType = t.WerEventType, processName = t.ProcessName, pid = t.Pid, exceptionCode = t.ExceptionCode, note = t.Class.Note })}");
        StartMark(t.Kind, t, t, t.Pid, t.ProcessName, merged);
    }

    private DateTimeOffset StartMark(string kind, object trigger, EventLogTrigger? autoTrigger, int? crashPid, string? crashProcessName, List<EventLogTrigger>? mergedTriggers,
        ManualMark? manual = null, int? fgPidHint = null, Task<ManualMark?>? lateDetails = null, TimeSpan? lateWait = null, string? via = null)
    {
        var markTime = DateTimeOffset.Now;
        Console.WriteLine($"[mark] {markTime:HH:mm:ss.fff} kind={kind} — {_post.TotalSeconds:0}초 후 덤프 예정");

        var task = Task.Run(async () =>
        {
            try
            {
                // 마킹 "시점"의 상태를 담아야 하므로 스냅샷은 지금 시작, post 대기와 병행
                var snapshotTask = Task.Run(() => _snapshotter.Capture(fgPidHint));
                await Task.Delay(_post);

                var from = markTime - _pre;
                var to = markTime + _post;
                // 대화상자 답을 기다리기 전에 구간을 잘라 둔다 — 버퍼 보존(pre+post+30초)을 넘겨 T-pre 가 밀려나지 않게
                var events = _buffer.Snapshot(from, to);

                if (lateDetails != null)
                {
                    var remaining = markTime + (lateWait ?? _post) - DateTimeOffset.Now;
                    if (!lateDetails.IsCompleted && remaining > TimeSpan.Zero)
                        await Task.WhenAny(lateDetails, Task.Delay(remaining));
                    ManualMark? answer = lateDetails.IsCompletedSuccessfully ? lateDetails.Result : null;
                    manual = answer is null ? null : NormalizeManual(answer);
                    crashProcessName = manual?.Target;
                    trigger = new { type = "manual", via, symptom = manual?.Symptom, target = manual?.Target, memo = manual?.Memo, answered = answer != null };
                }

                var snapResult = await snapshotTask;
                var snapshot = snapResult.Json;
                snapshot["etwImageLoadTopPids"] = TopImageLoadPids(events);
                if (manual != null)
                    snapshot["userReport"] = new { symptom = manual.Symptom, target = manual.Target, memo = manual.Memo };

                List<string>? crashModulePaths = null;
                if (crashPid.HasValue || crashProcessName != null)
                {
                    var found = FindLastModules(events, markTime, crashPid, crashProcessName);
                    snapshot["crashProcessLastModules"] = found.Dump;
                    crashModulePaths = found.Paths;
                }

                // 24시간 분 단위 롤업(느려짐 추세) — 크래시 프로세스를 초점으로, 없으면(수동 마킹) 마킹 시점 전경 프로세스
                object? resourceTrends = null;
                if (_rollup != null)
                {
                    try
                    {
                        // 초점: 크래시 PID > 지정 대상 이름 > 마킹 시점 전경 PID
                        int? focusPid = crashPid;
                        if (focusPid == null && crashProcessName == null && snapResult.ForegroundPid > 0) focusPid = snapResult.ForegroundPid;
                        var export = _rollup.Export(markTime, focusPid, crashProcessName);
                        snapshot["rollup24h"] = export.Full;
                        resourceTrends = export.Trends;
                    }
                    catch (Exception ex)
                    {
                        // 롤업 요약 실패가 덤프 전체를 잃게 하면 안 된다 (CLAUDE.md 4)
                        snapshot["rollup24h"] = new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" };
                    }
                }

                SignificanceResult? significance = null;
                if (autoTrigger != null)
                {
                    significance = ComputeSignificance(autoTrigger, events, markTime);
                    snapshot["significance"] = new { significance.Score, significance.Tier, significance.Tags, significance.Factors };
                }

                object triggerObj = mergedTriggers is { Count: > 0 }
                    ? new { primary = trigger, mergedWithinCooldown = mergedTriggers }
                    : trigger;

                var engineSnapshot = EngineSnapshotBuilder.Build(markTime, kind, autoTrigger, events, snapResult.Facts, crashModulePaths, significance, resourceTrends, manual);
                var dir = _writer.Write(markTime, kind, triggerObj, from, to, events, snapshot, engineSnapshot, significance, _buffer.Stats, _statuses);
                Console.WriteLine($"[dump] {Json.Serialize(new { dir, completedAt = DateTimeOffset.Now, kind, events = events.Count, significance = significance?.Score, tier = significance?.Tier })}");
                try
                {
                    DumpWritten?.Invoke(new DumpRecord(dir, kind, markTime, autoTrigger != null, autoTrigger?.ProcessName ?? manual?.Target,
                        significance?.Score, significance?.Tier, manual?.Symptom, manual?.Target));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[dump-notify-error] {ex.GetType().Name}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dump-error] {ex.GetType().Name}: {ex.Message}");
            }
        });

        lock (_lock) { _pending.Add(task); _pending.RemoveAll(x => x.IsCompleted); }
        return markTime;
    }

    /// <summary>
    /// 유의성 점수 입력을 버퍼에서 모은다: 사용자 대면(창 보유/전경), 수명(ETW processStart 또는 스냅샷 최초 관측),
    /// 7일 내 재발(이벤트 로그 조회), 오류 모듈 경로·예외 코드(트리거), 테스트 아티팩트(설정).
    /// </summary>
    private SignificanceResult ComputeSignificance(EventLogTrigger t, IReadOnlyList<SignalEvent> events, DateTimeOffset markTime)
    {
        bool? userFacing = null;
        double? lifetime = null;
        var crashAt = t.EventTime ?? t.ReceivedAt;

        if (t.Pid is { } pid)
        {
            DateTimeOffset? firstSeen = null;
            DateTimeOffset? etwStart = null;
            foreach (var e in events)
            {
                if (e.Timestamp > markTime) continue;
                try
                {
                    if (e.Source == "processes")
                    {
                        using var doc = JsonDocument.Parse(e.PayloadJson);
                        var root = doc.RootElement;
                        int fg = root.TryGetProperty("fgPid", out var f) ? f.GetInt32() : -1;
                        if (!root.TryGetProperty("processes", out var arr)) continue;
                        foreach (var p in arr.EnumerateArray())
                        {
                            if (p.GetProperty("pid").GetInt32() != pid) continue;
                            firstSeen ??= e.Timestamp;
                            bool win = p.TryGetProperty("win", out var w) && w.GetBoolean();
                            if (win || fg == pid) userFacing = true;
                            else userFacing ??= false;
                        }
                    }
                    else if (e.Source == "etw-kernel")
                    {
                        using var doc = JsonDocument.Parse(e.PayloadJson);
                        var r = doc.RootElement;
                        if (r.GetProperty("ev").GetString() == "processStart" && r.GetProperty("pid").GetInt32() == pid)
                            etwStart = e.Timestamp;
                    }
                }
                catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
            }

            if (etwStart is { } s) lifetime = (crashAt - s).TotalSeconds;
            else if (firstSeen is { } fs && (crashAt - fs).TotalSeconds > 30) lifetime = (crashAt - fs).TotalSeconds; // 하한만 알 때는 30초 초과만 인정
        }

        int recurrence = 0;
        if (t.Kind == "crash-1000" && t.ProcessName != null)
            recurrence = Math.Max(0, EventLogSource.CountRecentCrashes(t.ProcessName, TimeSpan.FromDays(7)) - 1);

        return SignificanceScorer.Compute(new SignificanceInput(
            t.Class.BaseScore, userFacing, t.FaultingModule, t.FaultingModulePath, lifetime, recurrence,
            t.ExceptionCode, _config.IsTestArtifact(t.ProcessName)));
    }

    /// <summary>
    /// 크래시 프로세스의 마지막 모듈 목록 찾기.
    /// 1차: ETW ImageLoad 누적 (관리자 + 해당 PID의 로드가 버퍼에 있을 때) — 수명이 짧은 프로세스도 실시간으로 잡힘.
    /// 폴백: 주기 프로세스 스냅샷 (비관리자이거나, ETW 가동 전에 시작된 프로세스).
    /// </summary>
    private static (object Dump, List<string>? Paths) FindLastModules(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int? pid, string? processName)
    {
        if (pid.HasValue && AccumulateEtwImageLoads(events, markTime, pid.Value) is { } etwMatch)
            return etwMatch;
        return FindLastModulesFromSnapshots(events, markTime, pid, processName);
    }

    /// <summary>
    /// 버퍼의 etw-kernel ImageLoad 이벤트에서 해당 PID의 로드 경로를 누적한다.
    /// PID 재사용 대비: 그 PID의 마지막 processStart(마킹 이전) 이후 이벤트만 센다.
    /// ETW 실시간 전달은 1~3초 늦게 도착하므로(수명 100ms급 크래시에서 실측), ImageLoad는 마킹 +5초까지 유예해 받아들인다.
    /// </summary>
    private static (object Dump, List<string> Paths)? AccumulateEtwImageLoads(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int pid)
    {
        var graceCap = markTime + TimeSpan.FromSeconds(5);
        DateTimeOffset? lastStart = null;
        foreach (var e in events)
        {
            if (e.Source != "etw-kernel" || e.Timestamp > markTime) continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                if (doc.RootElement.GetProperty("ev").GetString() == "processStart" &&
                    doc.RootElement.GetProperty("pid").GetInt32() == pid)
                    lastStart = e.Timestamp;
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        var paths = new List<string>();
        string? name = null;
        DateTimeOffset? first = null, last = null;
        foreach (var e in events)
        {
            if (e.Source != "etw-kernel" || e.Timestamp > graceCap) continue;
            if (lastStart.HasValue && e.Timestamp < lastStart.Value) continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                var root = doc.RootElement;
                if (root.GetProperty("ev").GetString() != "imageLoad" || root.GetProperty("pid").GetInt32() != pid) continue;
                name ??= root.TryGetProperty("proc", out var p) ? p.GetString() : null;
                var path = root.GetProperty("path").GetString();
                if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path);
                first ??= e.Timestamp;
                last = e.Timestamp;
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        if (paths.Count == 0) return null;
        var capped = paths.Take(60).ToList();
        return (new
        {
            matchedBy = "etw-pid",
            source = "etw-imageload-accumulated",
            pid,
            name,
            firstLoadAt = first,
            lastLoadAt = last,
            modules = capped,
        }, capped);
    }

    /// <summary>
    /// 주기 스냅샷 폴백: 최신부터 거슬러 PID 일치 우선, 프로세스명(확장자 제외) 일치는 차선.
    /// 마킹 시점 이후 스냅샷 제외 — 이름 폴백이 크래시 후 새로 뜬 동명 프로세스를 집어오는 오류 방지.
    /// </summary>
    private static (object Dump, List<string>? Paths) FindLastModulesFromSnapshots(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int? pid, string? processName)
    {
        string? bareName = processName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
            ? processName[..^4] : processName;
        (object Dump, List<string>? Paths)? nameMatch = null;

        for (int i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.Source != "processes" || e.Timestamp > markTime) continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                if (!doc.RootElement.TryGetProperty("modules", out var modules)) continue;
                foreach (var m in modules.EnumerateArray())
                {
                    int mPid = m.GetProperty("pid").GetInt32();
                    string mName = m.GetProperty("name").GetString() ?? "";
                    bool pidHit = pid.HasValue && mPid == pid.Value;
                    bool nameHit = bareName != null && string.Equals(mName, bareName, StringComparison.OrdinalIgnoreCase);
                    if (!pidHit && !nameHit) continue;

                    var mods = m.GetProperty("mods").EnumerateArray()
                        .Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToList();
                    (object Dump, List<string>? Paths) entry = (new
                    {
                        matchedBy = pidHit ? "pid" : "name",
                        snapshotAt = e.Timestamp,
                        pid = mPid,
                        name = mName,
                        modules = mods,
                    }, mods);
                    if (pidHit) return entry;
                    nameMatch ??= entry;
                }
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        return nameMatch ?? ((object)new
        {
            status = "unavailable",
            reason = "버퍼의 모듈 스냅샷에서 크래시 프로세스를 찾지 못함 (프로세스 수명이 스냅샷 주기보다 짧았거나 열거 대상에서 제외됨)",
            pid,
            processName,
        }, null);
    }

    /// <summary>
    /// 덤프 구간의 ETW ImageLoad를 PID별로 집계해 상위 10개를 낸다. 같은 구간의 processStart와
    /// 매칭해(시작 시각·부모 PID·명령줄) "무엇이 로드 폭주를 유발했는지"를 덤프만으로 짚게 한다.
    /// </summary>
    private static object TopImageLoadPids(IReadOnlyList<SignalEvent> events)
    {
        var agg = new Dictionary<int, PidAgg>();
        var starts = new Dictionary<int, (DateTimeOffset At, int Ppid, string? Cmd)>();
        int total = 0;

        foreach (var e in events)
        {
            if (e.Source != "etw-kernel") continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                var root = doc.RootElement;
                string? ev = root.GetProperty("ev").GetString();
                int pid = root.GetProperty("pid").GetInt32();
                if (ev == "imageLoad")
                {
                    total++;
                    if (!agg.TryGetValue(pid, out var a)) agg[pid] = a = new PidAgg();
                    a.Count++;
                    a.Name ??= root.TryGetProperty("proc", out var pn) ? pn.GetString() : null;
                    if (root.TryGetProperty("path", out var pp) && pp.GetString() is { } path) a.Mods.Add(path);
                    a.First ??= e.Timestamp;
                    a.Last = e.Timestamp;
                }
                else if (ev == "processStart")
                {
                    starts[pid] = (e.Timestamp,
                        root.TryGetProperty("ppid", out var pp2) ? pp2.GetInt32() : -1,
                        root.TryGetProperty("cmd", out var cm) ? cm.GetString() : null);
                }
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        if (total == 0)
            return new { status = "unavailable", reason = "덤프 구간에 ETW ImageLoad 없음 (비관리자이거나 로드 활동 없음)" };

        var top10 = agg.OrderByDescending(kv => kv.Value.Count).Take(10).Select(kv =>
        {
            bool started = starts.TryGetValue(kv.Key, out var st);
            string? cmd = st.Cmd;
            if (cmd is { Length: > 120 }) cmd = cmd[..120] + "...";
            return new
            {
                pid = kv.Key,
                name = kv.Value.Name,
                imageLoads = kv.Value.Count,
                distinctModules = kv.Value.Mods.Count,
                firstAt = kv.Value.First,
                lastAt = kv.Value.Last,
                startedInWindow = started,
                parentPid = started ? st.Ppid : (int?)null,
                startCmd = started ? cmd : null,
            };
        }).ToList();

        return new { totalImageLoadsInWindow = total, distinctPids = agg.Count, top10 };
    }

    private sealed class PidAgg
    {
        public string? Name;
        public int Count;
        public readonly HashSet<string> Mods = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? First;
        public DateTimeOffset? Last;
    }

    /// <summary>종료 시 진행 중인 덤프를 기다린다.</summary>
    public void Drain(TimeSpan timeout)
    {
        Task[] pending;
        lock (_lock) pending = _pending.ToArray();
        if (pending.Length > 0)
            Task.WaitAll(pending, timeout);
    }
}
