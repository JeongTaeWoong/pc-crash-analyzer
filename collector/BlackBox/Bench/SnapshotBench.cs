using System.Diagnostics;
using BlackBox.Core;
using BlackBox.Sources;

namespace BlackBox.Bench;

/// <summary>
/// 두 모듈 스냅샷 전략의 CPU 비용을 각각 60초(10초 틱 6회)씩 측정한다.
/// 측정 전 틱 1회는 워밍업(JIT) — FullSweep은 워밍업이 전체 스윕이므로,
/// 측정 구간(틱 1~6)에 전체 스윕이 정확히 1회(틱 6) 포함된다.
/// </summary>
public static class SnapshotBench
{
    public static void Run()
    {
        Console.WriteLine("[bench] 전략별 60초 CPU 비용 측정 시작 (워밍업 틱 1회 + 측정 틱 6회 × 2전략)");
        var results = new List<object>();

        foreach (var strategy in new[] { SnapshotStrategy.FullSweepEvery60s, SnapshotStrategy.ForegroundPlusRecent })
        {
            var source = new ProcessListSource(strategy);

            // 워밍업 (JIT + FullSweep의 틱0 소비)
            source.SampleOnceForBench();

            var proc = Process.GetCurrentProcess();
            var tickWallMs = new List<long>();
            var payloadBytes = new List<int>();
            proc.Refresh();
            var cpuBefore = proc.TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            for (int i = 0; i < 6; i++)
            {
                var tickSw = Stopwatch.StartNew();
                var payload = source.SampleOnceForBench();
                payloadBytes.Add(Json.Serialize(payload).Length); // 직렬화 비용 포함 (운영과 동일 경로)
                tickSw.Stop();
                tickWallMs.Add(tickSw.ElapsedMilliseconds);
                int remain = (int)(10_000 - tickSw.ElapsedMilliseconds);
                if (remain > 0 && i < 5) Thread.Sleep(remain);
            }

            wall.Stop();
            proc.Refresh();
            double cpuMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            double pctOneCore = cpuMs / wall.ElapsedMilliseconds * 100;
            double pctMachine = pctOneCore / Environment.ProcessorCount;

            var r = new
            {
                strategy = strategy.ToString(),
                wallMs = wall.ElapsedMilliseconds,
                cpuMs = Math.Round(cpuMs, 1),
                pctOfOneCore = Math.Round(pctOneCore, 2),
                pctMachineWide = Math.Round(pctMachine, 3),
                logicalProcessors = Environment.ProcessorCount,
                tickWallMs,
                avgPayloadKB = Math.Round(payloadBytes.Average() / 1024.0, 1),
            };
            results.Add(r);
            Console.WriteLine($"[bench] {Json.Serialize(r)}");
        }

        Console.WriteLine($"[bench-result] {Json.Serialize(results)}");
    }

    /// <summary>센서 소스(LHM, 2초 폴링) 단독 CPU 비용 60초 측정 — 상시 CPU 귀속 분석용.</summary>
    public static void RunSensors()
    {
        Console.WriteLine("[bench] 센서 2초 폴링 60초 CPU 비용 측정 (워밍업 1회 + 30틱)");
        using var source = new SensorSource();
        source.SampleOnceForBench(); // LHM Open + JIT 워밍업

        var proc = Process.GetCurrentProcess();
        var tickWallMs = new List<long>();
        proc.Refresh();
        var cpuBefore = proc.TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        for (int i = 0; i < 30; i++)
        {
            var tickSw = Stopwatch.StartNew();
            var payload = source.SampleOnceForBench();
            _ = Json.Serialize(payload); // 운영과 동일하게 직렬화 포함
            tickSw.Stop();
            tickWallMs.Add(tickSw.ElapsedMilliseconds);
            int remain = (int)(2_000 - tickSw.ElapsedMilliseconds);
            if (remain > 0 && i < 29) Thread.Sleep(remain);
        }

        wall.Stop();
        proc.Refresh();
        double cpuMs = (proc.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        double pctOneCore = cpuMs / wall.ElapsedMilliseconds * 100;
        Console.WriteLine($"[bench-sensors] {Json.Serialize(new
        {
            wallMs = wall.ElapsedMilliseconds,
            cpuMs = Math.Round(cpuMs, 1),
            pctOfOneCore = Math.Round(pctOneCore, 2),
            pctMachineWide = Math.Round(pctOneCore / Environment.ProcessorCount, 3),
            avgTickMs = Math.Round(tickWallMs.Average(), 1),
            maxTickMs = tickWallMs.Max(),
        })}");
    }
}
