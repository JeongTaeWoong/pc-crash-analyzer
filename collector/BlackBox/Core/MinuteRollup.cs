namespace BlackBox.Core;

/// <summary>리소스 소스의 10초 샘플 요약(ResourceSource.Latest).</summary>
public sealed record ProcUsage(int Pid, string Name, double Cpu, double PrivMB, long Handles, double IoMBps);

public sealed record ResourceSample(
    DateTimeOffset At,
    double CommittedPct, double CommittedMB, double CommitLimitMB, double AvailMB, double PagesPerSec,
    double GpuDedicatedMB, double GpuSharedMB,
    double CpuTotalPct,
    IReadOnlyList<ProcUsage> TopCpu);

/// <summary>주 GPU 센서 요약(SensorSource.LatestGpu). 값이 없으면 null. 메모리는 MB.</summary>
public sealed record GpuSample(DateTimeOffset At, double? Temp, double? HotSpot, double? Clock, double? Power,
    double? MemUsedMB = null, double? MemTotalMB = null, string? Name = null);

/// <summary>프로세스 목록 요약(ProcessListSource.LatestSummary): Private Bytes 상위 + 전경.</summary>
public sealed record ProcSummary(int Pid, string Name, double PrivMB, long Handles, bool Win);
public sealed record ProcessSummary(DateTimeOffset At, int FgPid, IReadOnlyList<ProcSummary> TopPrivate);

/// <summary>
/// 분 단위 롤업 (메모리에만, 기본 24시간). 5분 링버퍼로는 못 보는 "점점 느려짐"(docs/slowdown-mechanisms.md A·B·C·E)을
/// 위해 1분마다 각 소스의 최신 샘플을 한 줄로 압축해 보관하고, 덤프 시 시계열과 기울기 요약을 내보낸다.
/// 마킹 전에는 디스크에 쓰지 않는다. 시간 콜백(Ingest)은 타이머와 테스트가 공유한다.
/// 2026-10-29 리뷰 반영: 소스가 멈춰 같은 샘플이 반복되면 빈 칸(NaN)으로, 프로세스 시계열은 (pid, 이름) 키로(PID 재사용 분리),
/// 초점은 PID 일치 우선, CPU 평균은 "CPU 상위에 안 든 분 = 0"으로 센다.
/// </summary>
public sealed class MinuteRollup : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<MinuteEntry> _entries = new();
    private readonly Dictionary<string, string> _namePool = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _retention;
    private DateTimeOffset _lastResAt = DateTimeOffset.MinValue, _lastGpuAt = DateTimeOffset.MinValue, _lastProcAt = DateTimeOffset.MinValue;
    private double? _gpuMemTotalMB;
    private string? _gpuName;
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

    /// <summary>
    /// 한 분의 샘플을 받아 한 줄로 저장. 소스가 없으면(null) 해당 칸은 NaN/빈 값.
    /// 직전 Ingest 때와 같은 시각(At)의 샘플이 다시 오면 소스가 멈춘 것이므로 그 칸도 비운다 — 평탄한 가짜 추세를 만들지 않기 위해.
    /// </summary>
    public void Ingest(DateTimeOffset at, ResourceSample? res, GpuSample? gpu, ProcessSummary? procs)
    {
        if (res != null && res.At == _lastResAt) res = null; else if (res != null) _lastResAt = res.At;
        if (gpu != null && gpu.At == _lastGpuAt) gpu = null; else if (gpu != null) _lastGpuAt = gpu.At;
        if (procs != null && procs.At == _lastProcAt) procs = null; else if (procs != null) _lastProcAt = procs.At;

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
            (float)(gpu?.MemUsedMB ?? double.NaN),
            res != null, // 이 분에 CPU 상위 목록이 있었는가 — 없으면 프로세스 CPU 를 0으로 셀 수 없다
            rows.Values.ToArray());

        lock (_lock)
        {
            if (gpu?.MemTotalMB is { } total) _gpuMemTotalMB = total;
            if (gpu?.Name is { } gname) _gpuName = gname;
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
    /// focus = 크래시/전경 프로세스(PID 일치 우선, 없으면 이름). 기울기 단위: MB/분, %/시간. 절대 throw 하지 않는다.
    /// </summary>
    public RollupExport Export(DateTimeOffset now, int? focusPid, string? focusName)
    {
        try { return ExportCore(now, focusPid, focusName); }
        catch (Exception ex)
        {
            return new RollupExport(new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }, null);
        }
    }

    private RollupExport ExportCore(DateTimeOffset now, int? focusPid, string? focusName)
    {
        MinuteEntry[] all;
        lock (_lock) all = _entries.ToArray();
        if (all.Length == 0)
            return new RollupExport(new { status = "unavailable", reason = "롤업 샘플 없음(가동 1분 미만)" }, null);

        var last60 = all.Where(e => e.T >= now.AddMinutes(-60) && e.T <= now).ToArray();
        var last15 = all.Where(e => e.T >= now.AddMinutes(-15) && e.T <= now).ToArray();
        if (last60.Length == 0)
        {
            // 절전·시계 점프 뒤 첫 틱 전에 마킹된 경우 — 24시간 시계열은 주되 추세는 없음
            return new RollupExport(new
            {
                retentionHours = _retention.TotalHours, minutes = all.Length, approxBytes = ApproxBytes,
                series = Series(all),
                trends = new { status = "unavailable", reason = "최근 60분 롤업 샘플 없음(절전 복귀 직후 등)" },
            }, null);
        }

        // 시스템 추세
        double? commitSlopePctPerHour = SlopePerHour(last60, e => e.CommittedPct);
        double? availSlopeMBPerMin = SlopePerMin(last60, e => e.AvailMB);
        double? gpuDedSlopeMBPerMin = SlopePerMin(last60, e => e.GpuDedMB);
        double? gpuTempMax60 = Max(last60, e => e.GpuTemp), gpuClockMin60 = Min(last60, e => e.GpuClock), gpuClockMax60 = Max(last60, e => e.GpuClock);
        double? gpuMemMax60 = Max(last60, e => e.GpuMemUsedMB);
        double? gpuMemTotal; string? gpuName;
        lock (_lock) { gpuMemTotal = _gpuMemTotalMB; gpuName = _gpuName; }
        double? gpuMemPctMax = gpuMemMax60 is { } gm && gpuMemTotal is { } gt && gt > 0 ? gm / gt * 100 : null;
        int cpuMinutes = last60.Count(e => e.HasCpu);

        // 프로세스별 시계열 — (pid, 이름) 키: PID 재사용으로 다른 프로그램이 한 시계열에 섞이지 않게
        string? bareFocus = focusName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true ? focusName[..^4] : focusName;
        var perProc = new Dictionary<(int Pid, string Name), List<(DateTimeOffset T, ProcRow R, bool HasCpu)>>();
        foreach (var e in last60)
            foreach (var r in e.Procs)
            {
                var key = (r.Pid, r.Name);
                if (!perProc.TryGetValue(key, out var l)) perProc[key] = l = new();
                l.Add((e.T, r, e.HasCpu));
            }

        var growers = new List<(double? Slope, object Row)>();
        var cpuRows = new List<(double CpuAvg, object Row)>();
        object? focusByPid = null, focusByName = null;
        foreach (var ((pid, name), list) in perProc)
        {
            if (list.Count < 10) continue;
            var priv = Fit(list.Select(x => (x.T, (double)x.R.PrivMB)).ToList(), now);
            var handles = Fit(list.Select(x => (x.T, (double)x.R.Handles)).ToList(), now);
            // CPU 평균: CPU 목록이 있던 분 중 상위에 안 든 분은 0 으로 센다(상위 10 밖 = 그 10번째보다 낮음)
            double? cpuAvg = CpuAverage(list);
            var row = new
            {
                pid, name, samples = list.Count,
                privMBNow = Math.Round(list[^1].R.PrivMB, 1),
                privMBStart = Math.Round(list[0].R.PrivMB, 1),
                privSlopeMBPerMin = priv?.Slope is { } ps ? Math.Round(ps, 2) : (double?)null,
                privR2 = priv?.R2 is { } r2 ? Math.Round(r2, 2) : (double?)null,
                handleSlopePerMin = handles?.Slope is { } hs ? Math.Round(hs, 2) : (double?)null,
                cpuAvg,
                cpuSamples = list.Count(x => x.HasCpu),
            };
            if (focusPid.HasValue && pid == focusPid.Value) focusByPid = row;
            else if (bareFocus != null && string.Equals(name, bareFocus, StringComparison.OrdinalIgnoreCase)) focusByName ??= row;
            growers.Add((priv?.Slope, row));
            if (cpuAvg is { } c && c >= 1.0) cpuRows.Add((c, row)); // 60분 평균 1% 미만은 간섭 후보로 의미 없음
        }
        object? focus = focusByPid ?? (focusPid.HasValue ? null : focusByName) ?? focusByName;
        var topGrowers = growers.OrderByDescending(g => g.Slope ?? double.NegativeInfinity).Take(5).Select(g => g.Row).ToList();
        var topCpu60 = cpuRows.OrderByDescending(c => c.CpuAvg).Take(5).Select(c => c.Row).ToList();

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
            cpu_samples = cpuMinutes,
            commit_slope_pct_per_hour = Round(commitSlopePctPerHour),
            commit_pct_now = Round(last60[^1].CommittedPct),
            avail_mb_slope_per_min = Round(availSlopeMBPerMin),
            gpu_dedicated_mb_slope_per_min = Round(gpuDedSlopeMBPerMin),
            gpu_temp_max = Round(gpuTempMax60),
            gpu_clock_min = Round(gpuClockMin60),
            gpu_clock_max = Round(gpuClockMax60),
            gpu_name = gpuName,
            gpu_mem_total_mb = Round(gpuMemTotal),
            gpu_mem_used_mb_max = Round(gpuMemMax60),
            gpu_mem_pct_max = Round(gpuMemPctMax),
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
            series = Series(all),
            trends,
        };
        return new RollupExport(full, trends);
    }

    private static object Series(MinuteEntry[] all) => all.Select(e => new
    {
        t = e.T,
        commitPct = R(e.CommittedPct), availMB = R(e.AvailMB), gpuDedMB = R(e.GpuDedMB), cpuPct = R(e.CpuTotalPct), pagesPerSec = R(e.PagesPerSec),
        gpuTemp = R(e.GpuTemp), gpuHotSpot = R(e.GpuHotSpot), gpuClock = R(e.GpuClock), gpuPower = R(e.GpuPower), gpuMemMB = R(e.GpuMemUsedMB),
        procs = e.Procs.Select(p => new { p.Pid, p.Name, privMB = R(p.PrivMB), p.Handles, cpu = R(p.Cpu), fg = p.Fg ? true : (bool?)null }).ToList(),
    }).ToList();

    /// <summary>CPU 목록이 있던 분만 분모로, 상위에 안 든 분은 0. CPU 목록이 한 번도 없으면 null.</summary>
    private static double? CpuAverage(List<(DateTimeOffset T, ProcRow R, bool HasCpu)> list)
    {
        int n = 0; double sum = 0;
        foreach (var (_, r, hasCpu) in list)
        {
            if (!hasCpu) continue;
            n++;
            if (!float.IsNaN(r.Cpu)) sum += r.Cpu;
        }
        return n > 0 ? Math.Round(sum / n, 1) : null;
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
        float GpuMemUsedMB,
        bool HasCpu,
        ProcRow[] Procs);
}

public sealed record RollupExport(object Full, object? Trends);
