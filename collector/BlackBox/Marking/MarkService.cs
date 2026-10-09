using System.Text.Json;
using BlackBox.Core;

namespace BlackBox.Marking;

/// <summary>
/// 마킹 처리: T 시점에 상태 스냅샷을 뜨기 시작하고, T+post까지 수집을 계속한 뒤
/// 버퍼의 [T-pre, T+post] 구간과 스냅샷을 덤프한다.
/// 자동 트리거는 쿨다운(10초) 안에 또 오면 새 덤프 대신 직전 마킹에 병합한다
/// (한 번의 크래시가 1000→1001을 연달아 쏘는 것 대비).
/// </summary>
public sealed class MarkService
{
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromSeconds(10);

    private readonly RingBuffer _buffer;
    private readonly StateSnapshotter _snapshotter;
    private readonly DumpWriter _writer;
    private readonly TimeSpan _pre;
    private readonly TimeSpan _post;
    private readonly IReadOnlyList<SourceStatus> _statuses;
    private readonly object _lock = new();
    private readonly List<Task> _pending = new();
    private DateTimeOffset _lastAutoMark = DateTimeOffset.MinValue;
    private List<EventLogTrigger>? _mergeTarget;

    public MarkService(RingBuffer buffer, StateSnapshotter snapshotter, DumpWriter writer,
        TimeSpan pre, TimeSpan post, IReadOnlyList<SourceStatus> statuses)
    {
        _buffer = buffer;
        _snapshotter = snapshotter;
        _writer = writer;
        _pre = pre;
        _post = post;
        _statuses = statuses;
    }

    public void MarkManual(string via) =>
        StartMark("manual", new { type = "manual", via }, null, null, null);

    public void OnAutoTrigger(EventLogTrigger t)
    {
        List<EventLogTrigger> merged;
        lock (_lock)
        {
            if (DateTimeOffset.Now - _lastAutoMark < AutoCooldown && _mergeTarget != null)
            {
                _mergeTarget.Add(t);
                Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, id = t.EventId, suppressed = true, reason = "쿨다운 내 — 직전 마킹에 병합" })}");
                return;
            }
            _lastAutoMark = DateTimeOffset.Now;
            merged = new List<EventLogTrigger>();
            _mergeTarget = merged;
        }

        Console.WriteLine($"[trigger] {Json.Serialize(new { receivedAt = t.ReceivedAt, kind = t.Kind, log = t.Log, id = t.EventId, processName = t.ProcessName, pid = t.Pid })}");
        StartMark(t.Kind, t, t.Pid, t.ProcessName, merged);
    }

    private void StartMark(string kind, object trigger, int? crashPid, string? crashProcessName, List<EventLogTrigger>? mergedTriggers)
    {
        var markTime = DateTimeOffset.Now;
        Console.WriteLine($"[mark] {markTime:HH:mm:ss.fff} kind={kind} — {_post.TotalSeconds:0}초 후 덤프 예정");

        var task = Task.Run(async () =>
        {
            try
            {
                // 마킹 "시점"의 상태를 담아야 하므로 스냅샷은 지금 시작, post 대기와 병행
                var snapshotTask = Task.Run(_snapshotter.Capture);
                await Task.Delay(_post);

                var from = markTime - _pre;
                var to = markTime + _post;
                var events = _buffer.Snapshot(from, to);
                var snapshot = await snapshotTask;
                snapshot["etwImageLoadTopPids"] = TopImageLoadPids(events);

                if (crashPid.HasValue || crashProcessName != null)
                    snapshot["crashProcessLastModules"] = FindLastModules(events, markTime, crashPid, crashProcessName);

                object triggerObj = mergedTriggers is { Count: > 0 }
                    ? new { primary = trigger, mergedWithinCooldown = mergedTriggers }
                    : trigger;

                var dir = _writer.Write(markTime, kind, triggerObj, from, to, events, snapshot, _buffer.Stats, _statuses);
                Console.WriteLine($"[dump] {Json.Serialize(new { dir, completedAt = DateTimeOffset.Now, kind, events = events.Count })}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dump-error] {ex.GetType().Name}: {ex.Message}");
            }
        });

        lock (_lock) { _pending.Add(task); _pending.RemoveAll(x => x.IsCompleted); }
    }

    /// <summary>
    /// 크래시 프로세스의 마지막 모듈 목록 찾기.
    /// 1차: ETW ImageLoad 누적 (관리자 + 해당 PID의 로드가 버퍼에 있을 때) — 수명이 짧은 프로세스도 실시간으로 잡힘.
    /// 폴백: 주기 프로세스 스냅샷 (비관리자이거나, ETW 가동 전에 시작된 프로세스).
    /// </summary>
    private static object FindLastModules(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int? pid, string? processName)
    {
        if (pid.HasValue && AccumulateEtwImageLoads(events, markTime, pid.Value) is { } etwMatch)
            return etwMatch;
        return FindLastModulesFromSnapshots(events, markTime, pid, processName);
    }

    /// <summary>
    /// 버퍼의 etw-kernel ImageLoad 이벤트에서 해당 PID의 로드 경로를 누적한다.
    /// PID 재사용 대비: 그 PID의 마지막 processStart(마킹 이전) 이후 이벤트만 센다.
    /// ETW 실시간 전달은 1~3초 늦게 도착하므로(수명 100ms급 크래시에서 실측),
    /// ImageLoad는 마킹 +5초까지 유예해 받아들인다 — PID 일치 조건이라 오염 위험 없음.
    /// </summary>
    private static object? AccumulateEtwImageLoads(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int pid)
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
        return new
        {
            matchedBy = "etw-pid",
            source = "etw-imageload-accumulated",
            pid,
            name,
            firstLoadAt = first,
            lastLoadAt = last,
            modules = paths.Take(60).ToList(),
        };
    }

    /// <summary>
    /// 주기 스냅샷 폴백: 최신부터 거슬러 PID 일치 우선, 프로세스명(확장자 제외) 일치는 차선.
    /// 마킹 시점 이후 스냅샷 제외 — 이름 폴백이 크래시 후 새로 뜬 동명 프로세스를 집어오는 오류 방지 (2026-10-08 확인).
    /// </summary>
    private static object FindLastModulesFromSnapshots(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, int? pid, string? processName)
    {
        string? bareName = processName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true
            ? processName[..^4] : processName;
        object? nameMatch = null;

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

                    var entry = new
                    {
                        matchedBy = pidHit ? "pid" : "name",
                        snapshotAt = e.Timestamp,
                        pid = mPid,
                        name = mName,
                        modules = m.GetProperty("mods").EnumerateArray().Select(x => x.GetString()).ToList(),
                    };
                    if (pidHit) return entry;       // 최신 스냅샷의 PID 일치 — 바로 확정
                    nameMatch ??= entry;            // 이름 일치는 폴백 후보로만
                }
            }
            catch { }
        }

        return nameMatch ?? (object)new
        {
            status = "unavailable",
            reason = "버퍼의 모듈 스냅샷에서 크래시 프로세스를 찾지 못함 (프로세스 수명이 스냅샷 주기보다 짧았거나 열거 대상에서 제외됨)",
            pid,
            processName,
        };
    }

    /// <summary>
    /// 덤프 구간의 ETW ImageLoad를 PID별로 집계해 상위 10개를 낸다. 같은 구간의 processStart와
    /// 매칭해(시작 시각·부모 PID·명령줄) "무엇이 로드 폭주를 유발했는지"를 덤프만으로 짚게 한다.
    /// 2026-10-08 유휴 실측 중 ~320건/초 버스트의 원인을 사후 추적할 수 없었던 데서 추가.
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
