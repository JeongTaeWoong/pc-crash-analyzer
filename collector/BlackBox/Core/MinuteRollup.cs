namespace BlackBox.Core;

/// <summary>리소스 소스의 10초 샘플 요약(ResourceSource.Latest).</summary>
public sealed record ProcUsage(int Pid, string Name, double Cpu, double PrivMB, long Handles, double IoMBps);

public sealed record ResourceSample(
    DateTimeOffset At,
    double CommittedPct, double CommittedMB, double CommitLimitMB, double AvailMB, double PagesPerSec,
    double GpuDedicatedMB, double GpuSharedMB,
    double CpuTotalPct,
    IReadOnlyList<ProcUsage> TopCpu);

/// <summary>GPU 코어 센서 요약(SensorSource.LatestGpu). 값이 없으면 null.</summary>
public sealed record GpuSample(DateTimeOffset At, double? Temp, double? HotSpot, double? Clock, double? Power);

/// <summary>프로세스 목록 요약(ProcessListSource.LatestSummary): Private Bytes 상위 + 전경.</summary>
public sealed record ProcSummary(int Pid, string Name, double PrivMB, long Handles, bool Win);
public sealed record ProcessSummary(DateTimeOffset At, int FgPid, IReadOnlyList<ProcSummary> TopPrivate);

/// <summary>
/// 분 단위 롤업 (메모리에만, 기본 24시간). 5분 링버퍼로는 못 보는 "점점 느려짐"(docs/slowdown-mechanisms.md A·B·C·E)을
/// 위해 1분마다 각 소스의 최신 샘플을 한 줄로 압축해 보관하고, 덤프 시 시계열과 기울기 요약을 내보낸다.
/// 마킹 전에는 디스크에 쓰지 않는다. 시간 콜백(Ingest)은 타이머와 테스트가 공유한다.
/// </summary>
public sealed class MinuteRollup : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<MinuteEntry> _entries = new();
    private readonly Dictionary<string, string> _namePool = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _retention;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MinuteRollup(TimeSpan retention) => _retention = retention;

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>근사 메모리 점유(바이트): 엔트리 고정부 + 프로세스 행.</summary>
    public long ApproxBytes
    {
        get
        {
            lock (_lock)
            {
                long b = 0;
                foreach (var e in _entries) b += 72 + e.Procs.Length * 32;
                return b + _namePool.Count * 64;
            }
        }
    }

    public void Start(Func<ResourceSample?> resource, Func<GpuSample?> gpu, Func<ProcessSummary?> procs, TimeSpan interval)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    try { Ingest(DateTimeOffset.Now, resource(), gpu(), procs()); }
                    catch { /* 롤업 실패로 죽지 않는다 */ }
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>한 분의 샘플을 받아 한 줄로 저장. 소스가 없으면(null) 해당 칸은 NaN/빈 값.</summary>
    public void Ingest(DateTimeOffset at, ResourceSample? res, GpuSample? gpu, ProcessSummary? procs)
    {
        var rows = new Dictionary<int, ProcRow>();
        if (procs != null)
            foreach (var p in procs.TopPrivate)
                rows[p.Pid] = new ProcRow(p.Pid, Intern(p.Name), (float)p.PrivMB, (int)Math.Min(p.Handles, int.MaxValue), float.NaN, p.Pid == procs.FgPid);
        if (res != null)
            foreach (var u in res.TopCpu)
            {
                if (rows.TryGetValue(u.Pid, out var r)) rows[u.Pid] = r with { Cpu = (float)u.Cpu };
                else rows[u.Pid] = new ProcRow(u.Pid, Intern(u.Name), (float)u.PrivMB, (int)Math.Min(u.Handles, int.MaxValue), (float)u.Cpu, false);
            }

        var entry = new MinuteEntry(
            at,
            (float)(res?.CommittedPct ?? double.NaN), (float)(res?.AvailMB ?? double.NaN), (float)(res?.GpuDedicatedMB ?? double.NaN),
            (float)(res?.CpuTotalPct ?? double.NaN), (float)(res?.PagesPerSec ?? double.NaN),
            (float)(gpu?.Temp ?? double.NaN), (float)(gpu?.HotSpot ?? double.NaN), (float)(gpu?.Clock ?? double.NaN), (float)(gpu?.Power ?? double.NaN),
            rows.Values.ToArray());

        lock (_lock)
        {
            _entries.Enqueue(entry);
            var cutoff = at - _retention;
            while (_entries.Count > 0 && _entries.Peek().T < cutoff) _entries.Dequeue();
        }
    }

    private string Intern(string name)
    {
        if (_namePool.TryGetValue(name, out var s)) return s;
        _namePool[name] = name;
        return name;
    }

    /// <summary>
    /// 덤프용 내보내기. Full = 전체 시계열(snapshot.json용), Trends = 기울기 요약(엔진 스냅샷 resource_trends용).
    /// focus = 크래시/전경 프로세스. 기울기 단위: MB/분, %/시간.
    /// </summary>
    public RollupExport Export(DateTimeOffset now, int? focusPid, string? focusName)
    {
        MinuteEntry[] all;
        lock (_lock) all = _entries.ToArray();
        if (all.Length == 0)
            return new RollupExport(new { status = "unavailable", reason = "롤업 샘플 없음(가동 1분 미만)" }, null);

        var last60 = all.Where(e => e.T >= now.AddMinutes(-60)).ToArray();
        var last15 = all.Where(e => e.T >= now.AddMinutes(-15)).ToArray();

        // 시스템 추세
        double? commitSlopePctPerHour = SlopePerHour(last60, e => e.CommittedPct);
        double? availSlopeMBPerMin = SlopePerMin(last60, e => e.AvailMB);
        double? gpuDedSlopeMBPerMin = SlopePerMin(last60, e => e.GpuDedMB);
        double? gpuTempMax60 = Max(last60, e => e.GpuTemp), gpuClockMin60 = Min(last60, e => e.GpuClock), gpuClockMax60 = Max(last60, e => e.GpuClock);

        // 프로세스별 추세(최근 60분에 10회 이상 관측된 것만)
        string? bareFocus = focusName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? focusName[..^4] : focusName;
        var perProc = new Dictionary<int, List<(DateTimeOffset T, ProcRow R)>>();
        foreach (var e in last60)
            foreach (var r in e.Procs)
            {
                if (!perProc.TryGetValue(r.Pid, out var l)) perProc[r.Pid] = l = new();
                l.Add((e.T, r));
            }

        var growers = new List<object>();
        object? focus = null;
        foreach (var (pid, list) in perProc)
        {
            if (list.Count < 10) continue;
            var name = list[^1].R.Name;
            var priv = Fit(list.Select(x => (x.T, (double)x.R.PrivMB)).ToList(), now);
            var handles = Fit(list.Select(x => (x.T, (double)x.R.Handles)).ToList(), now);
            var row = new
            {
                pid, name, samples = list.Count,
                privMBNow = Math.Round(list[^1].R.PrivMB, 1),
                privMBStart = Math.Round(list[0].R.PrivMB, 1),
                privSlopeMBPerMin = priv?.Slope is { } ps ? Math.Round(ps, 2) : (double?)null,
                privR2 = priv?.R2 is { } r2 ? Math.Round(r2, 2) : (double?)null,
                handleSlopePerMin = handles?.Slope is { } hs ? Math.Round(hs, 2) : (double?)null,
                cpuAvg = AvgNonNaN(list.Select(x => (double)x.R.Cpu)),
            };
            bool isFocus = (focusPid.HasValue && pid == focusPid.Value) ||
                           (bareFocus != null && string.Equals(name, bareFocus, StringComparison.OrdinalIgnoreCase));
            if (isFocus) focus = row;
            growers.Add(row);
        }
        var topGrowers = growers
            .OrderByDescending(g => (double?)g.GetType().GetProperty("privSlopeMBPerMin")!.GetValue(g) ?? double.NegativeInfinity)
            .Take(5).ToList();
        // 외부 간섭(E) 판별용: 최근 60분 CPU 평균 상위 5 (초점 프로세스가 아닌 것이 위에 있으면 간섭 후보)
        var topCpu60 = perProc
            .Select(kv => new { pid = kv.Key, name = kv.Value[^1].R.Name, samples = kv.Value.Count, cpuAvg = AvgNonNaN(kv.Value.Select(x => (double)x.R.Cpu)) })
            .Where(x => x.cpuAvg is { } c && c >= 0.5)
            .OrderByDescending(x => x.cpuAvg).Take(5).ToList();

        object? focus15 = null;
        if (focusPid.HasValue && last15.Length >= 3)
        {
            var pts = new List<(DateTimeOffset, double)>();
            foreach (var e in last15) foreach (var r in e.Procs) if (r.Pid == focusPid.Value) pts.Add((e.T, r.PrivMB));
            if (Fit(pts, now) is { } f15) focus15 = new { privSlopeMBPerMin = Math.Round(f15.Slope, 2), r2 = Math.Round(f15.R2, 2), samples = pts.Count };
        }

        var trends = new
        {
            window_minutes = 60,
            samples = last60.Length,
            commit_slope_pct_per_hour = Round(commitSlopePctPerHour),
            commit_pct_now = Round(last60[^1].CommittedPct),
            avail_mb_slope_per_min = Round(availSlopeMBPerMin),
            gpu_dedicated_mb_slope_per_min = Round(gpuDedSlopeMBPerMin),
            gpu_temp_max = Round(gpuTempMax60),
            gpu_clock_min = Round(gpuClockMin60),
            gpu_clock_max = Round(gpuClockMax60),
            focus_process = focus,
            focus_process_last15 = focus15,
            top_memory_growers = topGrowers,
            top_cpu_60 = topCpu60,
        };

        var full = new
        {
            retentionHours = _retention.TotalHours,
            minutes = all.Length,
            approxBytes = ApproxBytes,
            series = all.Select(e => new
            {
                t = e.T,
                commitPct = R(e.CommittedPct), availMB = R(e.AvailMB), gpuDedMB = R(e.GpuDedMB), cpuPct = R(e.CpuTotalPct), pagesPerSec = R(e.PagesPerSec),
                gpuTemp = R(e.GpuTemp), gpuHotSpot = R(e.GpuHotSpot), gpuClock = R(e.GpuClock), gpuPower = R(e.GpuPower),
                procs = e.Procs.Select(p => new { p.Pid, p.Name, privMB = R(p.PrivMB), p.Handles, cpu = R(p.Cpu), fg = p.Fg ? true : (bool?)null }).ToList(),
            }).ToList(),
            trends,
        };
        return new RollupExport(full, trends);
    }

    private static (double Slope, double R2)? Fit(List<(DateTimeOffset T, double V)> pts, DateTimeOffset now)
    {
        var xy = pts.Where(p => !double.IsNaN(p.V)).Select(p => ((p.T - now).TotalMinutes, p.V)).ToList();
        return Trend.Fit(xy);
    }
    private static double? SlopePerMin(MinuteEntry[] es, Func<MinuteEntry, float> f)
        => Trend.Slope(es.Where(e => !float.IsNaN(f(e))).Select(e => ((e.T - es[0].T).TotalMinutes, (double)f(e))).ToList());
    private static double? SlopePerHour(MinuteEntry[] es, Func<MinuteEntry, float> f) => SlopePerMin(es, f) is { } s ? s * 60 : null;
    private static double? Max(MinuteEntry[] es, Func<MinuteEntry, float> f) { var v = es.Select(f).Where(x => !float.IsNaN(x)).ToList(); return v.Count > 0 ? v.Max() : null; }
    private static double? Min(MinuteEntry[] es, Func<MinuteEntry, float> f) { var v = es.Select(f).Where(x => !float.IsNaN(x)).ToList(); return v.Count > 0 ? v.Min() : null; }
    private static double? AvgNonNaN(IEnumerable<double> xs) { var v = xs.Where(x => !double.IsNaN(x)).ToList(); return v.Count > 0 ? Math.Round(v.Average(), 1) : null; }
    private static double? Round(double? v) => v is { } x && !double.IsNaN(x) ? Math.Round(x, 2) : null;
    private static double? R(float v) => float.IsNaN(v) ? null : Math.Round(v, 1);

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch { }
    }

    private readonly record struct ProcRow(int Pid, string Name, float PrivMB, int Handles, float Cpu, bool Fg);

    private sealed record MinuteEntry(
        DateTimeOffset T,
        float CommittedPct, float AvailMB, float GpuDedMB, float CpuTotalPct, float PagesPerSec,
        float GpuTemp, float GpuHotSpot, float GpuClock, float GpuPower,
        ProcRow[] Procs);
}

public sealed record RollupExport(object Full, object? Trends);
