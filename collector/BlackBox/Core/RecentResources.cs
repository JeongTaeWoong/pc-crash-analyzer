using System.Text.Json;

namespace BlackBox.Core;

/// <summary>
/// 덤프 창(마킹 전 5분 ~ 후 30초)에 든 resources·processes 이벤트를 엔진 입력용으로 간추린다 (engine-snapshot 의 resource_recent).
///
/// 왜 필요한가 (2026-11-12 첫 사례 세션): 60분 추세(resource_trends, MinuteRollup)는 프로세스당 1분 표본 10개 이상일 때만
/// 행을 만든다. 방금 켠 프로그램이 2분 동안 CPU·메모리·디스크를 차지해 느려진 경우, 범인은 원시 이벤트에만 있고
/// 엔진이 읽는 요약에는 없었다(13개 사례 전부 top_cpu_60·top_memory_growers 가 빈 배열). commit_pct_now 도 분 단위
/// 롤업의 마지막 값이라 최대 1분 늦었다(실제 96.4%, 요약 33.6%).
///
/// 이미 수집한 값을 정렬·평균해 옮겨 적을 뿐이다 — 어느 프로세스가 원인인지 판단하지 않는다(추론은 엔진의 몫).
/// 순수 함수: 버퍼 이벤트만 입력으로 받는다. 값이 없는 항목은 필드를 생략한다(0 을 넣지 않는다).
/// </summary>
public static class RecentResources
{
    private sealed class Proc
    {
        public int Pid; public string Name = "";
        public readonly List<double> Cpu = new();
        public readonly List<double> Io = new();
        public double PrivLast;
        public int LastMeasurement;   // 마지막으로 상위 목록에 잡힌 측정의 순번(1부터)
    }

    private sealed record MemRow(int Pid, string Name, double Priv, double First, double Delta, int Seconds, int Samples, bool StartedInWindow);

    private sealed class Disk
    {
        public readonly List<double> Busy = new(), Queue = new(), Latency = new(), Iops = new(), Mbps = new();
    }

    public static object? Summarize(IReadOnlyList<SignalEvent> events)
    {
        var memT = new List<DateTimeOffset>();
        var commit = new List<double>(); var avail = new List<double>(); var pages = new List<double>();
        double? commitLimit = null;
        var cpuTotals = new List<double>();
        int cpuMeasurements = 0;
        string? lastCpuKey = null;
        var procs = new Dictionary<(int, string), Proc>();
        var disks = new Dictionary<string, Disk>(StringComparer.Ordinal);

        // processes: (pid, 이름)별 처음·마지막 Private Bytes
        var firstPriv = new Dictionary<(int, string), (DateTimeOffset T, double Priv)>();
        List<(int Pid, string Name, double Priv)>? lastList = null;
        DateTimeOffset lastListT = default;
        DateTimeOffset? firstListT = null;
        var sampleCount = new Dictionary<(int, string), int>();

        foreach (var e in events.OrderBy(x => x.Timestamp))
        {
            if (e.Source != "resources" && e.Source != "processes") continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                var root = doc.RootElement;
                if (e.Source == "resources")
                {
                    if (root.TryGetProperty("memory", out var m) && m.ValueKind == JsonValueKind.Object)
                    {
                        memT.Add(e.Timestamp);
                        if (Num(m, "committedPct") is { } cp) commit.Add(cp);
                        if (Num(m, "availMB") is { } av) avail.Add(av);
                        if (Num(m, "pagesPerSec") is { } pg) pages.Add(pg);
                        if (Num(m, "commitLimitMB") is { } cl) commitLimit = cl;
                    }

                    if (root.TryGetProperty("disk", out var dk) && dk.ValueKind == JsonValueKind.Object
                        && dk.TryGetProperty("disks", out var darr) && darr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in darr.EnumerateArray())
                        {
                            if (!d.TryGetProperty("name", out var dn) || dn.GetString() is not { Length: > 0 } name) continue;
                            if (!disks.TryGetValue(name, out var row)) disks[name] = row = new Disk();
                            if (Num(d, "busyPct") is { } b) row.Busy.Add(b);
                            if (Num(d, "queue") is { } q) row.Queue.Add(q);
                            if (Num(d, "latencyMs") is { } l) row.Latency.Add(l);
                            if (Num(d, "iops") is { } io) row.Iops.Add(io);
                            if (Num(d, "mbps") is { } mb) row.Mbps.Add(mb);
                        }
                    }

                    // CPU·프로세스 상위는 30초마다 재고 사이 샘플에는 같은 값을 다시 싣는다 — 한 번 잰 것은 한 번만 센다.
                    // 열쇠: cpuAt(잰 시각, v0.9.1~). 그 전 덤프는 topCpu 원문이 같으면 같은 측정으로 본다.
                    if (!root.TryGetProperty("topCpu", out var top) || top.ValueKind != JsonValueKind.Array || top.GetArrayLength() == 0) continue;
                    string key = root.TryGetProperty("cpuAt", out var ca) && ca.ValueKind == JsonValueKind.String
                        ? ca.GetString()!
                        : top.GetRawText();
                    if (key == lastCpuKey) continue;
                    lastCpuKey = key;
                    cpuMeasurements++;
                    if (Num(root, "cpuTotalPct") is { } tot) cpuTotals.Add(tot);
                    foreach (var p in top.EnumerateArray())
                    {
                        if (Num(p, "Pid") is not { } pidD || !p.TryGetProperty("Name", out var nm) || nm.GetString() is not { Length: > 0 } pname) continue;
                        var k = ((int)pidD, pname);
                        if (!procs.TryGetValue(k, out var pr)) procs[k] = pr = new Proc { Pid = (int)pidD, Name = pname };
                        pr.Cpu.Add(Num(p, "Cpu") ?? 0);
                        pr.Io.Add(Num(p, "io") ?? 0);
                        pr.LastMeasurement = cpuMeasurements;
                        if (Num(p, "PrivMB") is { } pv) pr.PrivLast = pv;
                    }
                }
                else if (root.TryGetProperty("processes", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<(int, string, double)>();
                    foreach (var p in arr.EnumerateArray())
                    {
                        if (Num(p, "pid") is not { } pidD || !p.TryGetProperty("name", out var nm) || nm.GetString() is not { Length: > 0 } pname) continue;
                        if (Num(p, "privMB") is not { } priv) continue; // 접근 거부 등으로 값이 없는 프로세스
                        var k = ((int)pidD, pname);
                        if (!firstPriv.ContainsKey(k)) firstPriv[k] = (e.Timestamp, priv);
                        sampleCount[k] = sampleCount.GetValueOrDefault(k) + 1;
                        list.Add(((int)pidD, pname, priv));
                    }
                    if (list.Count > 0) { lastList = list; lastListT = e.Timestamp; firstListT ??= e.Timestamp; }
                }
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        if (memT.Count == 0 && lastList == null) return null;

        var r = new Dictionary<string, object?>();
        if (memT.Count > 0)
        {
            r["window_seconds"] = (int)Math.Round((memT[^1] - memT[0]).TotalSeconds);
            r["samples"] = memT.Count;
        }
        if (commit.Count > 0) { r["commit_pct_last"] = R1(commit[^1]); r["commit_pct_min"] = R1(commit.Min()); r["commit_pct_max"] = R1(commit.Max()); }
        if (commitLimit is { } lim) r["commit_limit_mb"] = Math.Round(lim);
        if (avail.Count > 0) { r["avail_mb_last"] = Math.Round(avail[^1]); r["avail_mb_min"] = Math.Round(avail.Min()); }
        if (pages.Count > 0) { r["pages_per_sec_avg"] = R1(pages.Average()); r["pages_per_sec_max"] = R1(pages.Max()); }

        r["cpu_measurements"] = cpuMeasurements;
        if (cpuTotals.Count > 0) { r["cpu_total_pct_avg"] = R1(cpuTotals.Average()); r["cpu_total_pct_max"] = R1(cpuTotals.Max()); }
        if (cpuMeasurements > 0)
        {
            // 평균(cpu_avg): 상위 목록에 안 든 측정은 0 으로 센다(상위 10 밖 = 그 10번째보다 낮음) — MinuteRollup.CpuAverage 와 같은 약속.
            // 잡힌 동안의 평균(cpu_avg_active, 2026-11-14): 그 프로세스가 상위 목록에 있던 측정만으로 낸 평균.
            //   마킹 100초 전에 켜져 전 코어를 쓴 프로그램이 5분 창 평균으로는 "28.5%, 측정 2회"로 묽게 보였다(11/13 시범: 요약만 받은
            //   모델이 근거 부족으로 읽음). 둘 다 싣는다 — 어느 쪽이 맞는 눈금인지는 읽는 쪽(엔진·모델)이 정한다.
            // 마지막 측정의 값(cpu_last·io_mbps_last, 2026-11-14): 창의 마지막 측정(마킹 무렵)에 상위 목록에 있었으면 그 값, 없었으면 null.
            //   "마킹하던 순간에도 돌고 있었나"를 요약만으로 알 수 있게 한다.
            r["top_cpu"] = procs.Values
                .Select(p => new { p, avg = p.Cpu.Sum() / cpuMeasurements, max = p.Cpu.Max() })
                .Where(x => x.max >= 1.0)
                .OrderByDescending(x => x.avg).ThenByDescending(x => x.max).Take(5)
                .Select(x => (object)new
                {
                    pid = x.p.Pid, name = x.p.Name, cpu_avg = R1(x.avg), cpu_avg_active = R1(x.p.Cpu.Average()), cpu_max = R1(x.max),
                    cpu_last = x.p.LastMeasurement == cpuMeasurements ? R1(x.p.Cpu[^1]) : (double?)null,
                    measurements = x.p.Cpu.Count, priv_mb_last = R1(x.p.PrivLast),
                })
                .ToList();
            r["top_io"] = procs.Values
                .Select(p => new { p, avg = p.Io.Sum() / cpuMeasurements, max = p.Io.Max() })
                .Where(x => x.max >= 1.0)
                .OrderByDescending(x => x.avg).ThenByDescending(x => x.max).Take(3)
                .Select(x => (object)new
                {
                    pid = x.p.Pid, name = x.p.Name, io_mbps_avg = R2(x.avg), io_mbps_avg_active = R2(x.p.Io.Average()), io_mbps_max = R2(x.max),
                    io_mbps_last = x.p.LastMeasurement == cpuMeasurements ? R2(x.p.Io[^1]) : (double?)null,
                    measurements = x.p.Io.Count,
                })
                .ToList();
        }

        if (lastList != null)
        {
            var rows = lastList.Select(x =>
            {
                var (t0, p0) = firstPriv[(x.Pid, x.Name)];
                // started_in_window: 창의 첫 목록에는 없던 프로세스 — 창 안에서 새로 켜졌다(처음 값이 0 에서 시작한 셈)
                return new MemRow(x.Pid, x.Name, x.Priv, p0, x.Priv - p0, (int)Math.Round((lastListT - t0).TotalSeconds), sampleCount[(x.Pid, x.Name)], t0 > firstListT);
            }).ToList();
            static object Row(MemRow x) => new { pid = x.Pid, name = x.Name, priv_mb_last = R1(x.Priv), priv_mb_first = R1(x.First), priv_mb_delta = R1(x.Delta), seconds = x.Seconds, samples = x.Samples, started_in_window = x.StartedInWindow };
            r["top_memory"] = rows.OrderByDescending(x => x.Priv).Take(5).Select(Row).ToList();
            // 창 안에서 50MB 이상 늘어난 것만 — 그보다 작은 변화는 평소 흔들림
            r["top_memory_growth"] = rows.Where(x => x.Delta >= 50).OrderByDescending(x => x.Delta).Take(5).Select(Row).ToList();
        }

        if (disks.Count > 0)
        {
            r["disks"] = disks.Where(kv => kv.Value.Busy.Count > 0)
                .OrderByDescending(kv => kv.Value.Busy.Average())
                .Select(kv => (object)new
                {
                    name = kv.Key,
                    samples = kv.Value.Busy.Count,
                    busy_pct_avg = R1(kv.Value.Busy.Average()),
                    busy_pct_max = R1(kv.Value.Busy.Max()),
                    // 90% 이상 바빴던 표본 수(10초 표본, 2026-11-14). 창 평균은 마킹 직전 1분의 포화를 5분으로 나눠 44% 로 만든다
                    // (11/14 office2 disk-hog: 표본 24개 중 9개가 93~100%, 평균 44.1%).
                    busy_ge90_samples = kv.Value.Busy.Count(b => b >= 90.0),
                    // 창의 마지막 표본 6개(약 1분, 마킹 무렵)의 평균 — "마킹하던 순간의 디스크"(2026-11-14).
                    // 같은 사례에서 창 평균은 44%·11MB/s 였지만 마지막 1분은 포화였다: 요약만 읽은 모델이 "전송량이 적으니 디스크가 느린 것"으로 읽었다.
                    busy_pct_recent = R1(Recent(kv.Value.Busy)),
                    latency_ms_recent = R2(Recent(kv.Value.Latency)),
                    mbps_recent = R2(Recent(kv.Value.Mbps)),
                    queue_avg = R2(Avg(kv.Value.Queue)),
                    queue_max = R2(Max(kv.Value.Queue)),
                    latency_ms_avg = R2(Avg(kv.Value.Latency)),
                    latency_ms_max = R2(Max(kv.Value.Latency)),
                    iops_avg = R1(Avg(kv.Value.Iops)),
                    mbps_avg = R2(Avg(kv.Value.Mbps)),
                })
                .ToList();
        }
        return r;
    }

    private static double? Num(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double Avg(List<double> l) => l.Count > 0 ? l.Average() : 0;
    private static double Max(List<double> l) => l.Count > 0 ? l.Max() : 0;
    private static double Recent(List<double> l) => l.Count > 0 ? l.Skip(Math.Max(0, l.Count - 6)).Average() : 0;
    private static double R1(double v) => Math.Round(v, 1);
    private static double R2(double v) => Math.Round(v, 2);
}
