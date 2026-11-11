using System.Text.Json;
using BlackBox.Core;
using Xunit;

namespace BlackBox.Tests;

public class MinuteRollupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(9));

    private static ResourceSample Res(DateTimeOffset at, double commitPct, params ProcUsage[] top) =>
        new(at, commitPct, 80000, 107000, 50000, 10, 3000, 400, 12, top);

    private static ProcessSummary Procs(DateTimeOffset at, int fg, params ProcSummary[] rows) => new(at, fg, rows);

    [Fact]
    public void Retention_drops_entries_older_than_window()
    {
        var r = new MinuteRollup(TimeSpan.FromMinutes(30));
        for (int i = 0; i < 100; i++)
            r.Ingest(T0.AddMinutes(i), Res(T0.AddMinutes(i), 50), null, null);
        Assert.InRange(r.Count, 30, 31);
    }

    [Fact]
    public void Focus_process_memory_slope_is_reported_in_mb_per_minute()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 60; i++)
        {
            var at = T0.AddMinutes(i);
            // unity: 1분에 12MB씩 증가 / chrome: 평탄
            r.Ingest(at, Res(at, 60 + i * 0.1),
                new GpuSample(at, 60 + i * 0.2, null, 1800 - i, 120),
                Procs(at, 4242,
                    new ProcSummary(4242, "Unity", 4000 + 12 * i, 2000 + 5 * i, true),
                    new ProcSummary(777, "chrome", 1500, 1900, true)));
        }

        var now = T0.AddMinutes(59);
        var export = r.Export(now, 4242, "Unity.exe");
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(export.Trends)).RootElement;

        var focus = trends.GetProperty("focus_process");
        Assert.Equal(4242, focus.GetProperty("pid").GetInt32());
        Assert.Equal(12.0, focus.GetProperty("privSlopeMBPerMin").GetDouble(), 1);
        Assert.Equal(5.0, focus.GetProperty("handleSlopePerMin").GetDouble(), 1);
        Assert.Equal(1.0, focus.GetProperty("privR2").GetDouble(), 2);

        // 시스템: 커밋 % 기울기 = 0.1%/분 = 6%/시간
        Assert.Equal(6.0, trends.GetProperty("commit_slope_pct_per_hour").GetDouble(), 1);
        // GPU: 최고 온도·최저 클럭
        Assert.Equal(60 + 59 * 0.2, trends.GetProperty("gpu_temp_max").GetDouble(), 1);
        Assert.Equal(1800 - 59, trends.GetProperty("gpu_clock_min").GetDouble(), 1);

        // 상위 증가 프로세스 1위는 unity
        var growers = trends.GetProperty("top_memory_growers");
        Assert.Equal("Unity", growers[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Focus_by_name_matches_when_pid_unknown()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 20; i++)
        {
            var at = T0.AddMinutes(i);
            r.Ingest(at, Res(at, 50), null, Procs(at, -1, new ProcSummary(99, "FaultHost", 100 + i, 50, false)));
        }
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(19), null, "FaultHost.exe").Trends)).RootElement;
        Assert.Equal(99, trends.GetProperty("focus_process").GetProperty("pid").GetInt32());
    }

    [Fact]
    public void Empty_rollup_exports_unavailable_not_exception()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        var export = r.Export(T0, null, null);
        Assert.Null(export.Trends);
        var full = JsonDocument.Parse(JsonSerializer.Serialize(export.Full)).RootElement;
        Assert.Equal("unavailable", full.GetProperty("status").GetString());
    }

    [Fact]
    public void Stale_window_after_sleep_exports_unavailable_trends_not_exception()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 30; i++) r.Ingest(T0.AddMinutes(i), Res(T0.AddMinutes(i), 50), null, null);
        // 2시간 절전 뒤 첫 롤업 틱 전에 마킹
        var export = r.Export(T0.AddMinutes(30 + 120), 1, "x.exe");
        Assert.Null(export.Trends);
        var full = JsonDocument.Parse(JsonSerializer.Serialize(export.Full)).RootElement;
        Assert.Equal(30, full.GetProperty("minutes").GetInt32());
        Assert.Equal("unavailable", full.GetProperty("trends").GetProperty("status").GetString());
    }

    [Fact]
    public void Repeated_identical_sample_from_stalled_source_is_treated_as_missing()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        var stale = Res(T0, 50); // 소스가 멈춰 같은 At 의 샘플이 계속 들어오는 상황
        for (int i = 0; i < 20; i++) r.Ingest(T0.AddMinutes(i), stale, null, null);
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(19), null, null).Trends)).RootElement;
        Assert.Equal(1, trends.GetProperty("cpu_samples").GetInt32()); // 첫 1분만 유효
        Assert.Equal(JsonValueKind.Null, trends.GetProperty("commit_slope_pct_per_hour").ValueKind);
    }

    [Fact]
    public void Focus_prefers_pid_match_over_same_name_process()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 30; i++)
        {
            var at = T0.AddMinutes(i);
            r.Ingest(at, Res(at, 50), null, Procs(at, -1,
                new ProcSummary(4242, "Unity", 4000 + 12 * i, 100, true),   // 진짜(증가)
                new ProcSummary(5100, "Unity", 800, 100, false)));           // 동명 다른 인스턴스(평탄)
        }
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(29), 4242, "Unity.exe").Trends)).RootElement;
        Assert.Equal(4242, trends.GetProperty("focus_process").GetProperty("pid").GetInt32());
        Assert.Equal(12.0, trends.GetProperty("focus_process").GetProperty("privSlopeMBPerMin").GetDouble(), 1);
    }

    [Fact]
    public void Cpu_average_counts_minutes_outside_top_list_as_zero()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 60; i++)
        {
            var at = T0.AddMinutes(i);
            // big: 항상 메모리 상위, CPU 상위에는 2분만(15%) 등장
            var top = i < 2 ? new[] { new ProcUsage(9, "big", 15, 3000, 100, 0) } : Array.Empty<ProcUsage>();
            r.Ingest(at, Res(at, 50, top), null, Procs(at, -1, new ProcSummary(9, "big", 3000, 100, false)));
        }
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(59), null, null).Trends)).RootElement;
        Assert.Equal(0, trends.GetProperty("top_cpu_60").GetArrayLength()); // 평균 0.5% → 상위 목록에 못 든다
        var grower = trends.GetProperty("top_memory_growers")[0];
        Assert.Equal(0.5, grower.GetProperty("cpuAvg").GetDouble(), 1);
        Assert.Equal(60, grower.GetProperty("cpuSamples").GetInt32());
    }

    [Fact]
    public void Reused_pid_with_different_name_is_a_separate_series()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 20; i++) { var at = T0.AddMinutes(i); r.Ingest(at, Res(at, 50), null, Procs(at, -1, new ProcSummary(1234, "A", 3000, 10, false))); }
        for (int i = 20; i < 50; i++) { var at = T0.AddMinutes(i); r.Ingest(at, Res(at, 50), null, Procs(at, -1, new ProcSummary(1234, "B", 200, 10, false))); }
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(49), null, null).Trends)).RootElement;
        foreach (var g in trends.GetProperty("top_memory_growers").EnumerateArray())
        {
            // A(20) 와 B(30) 가 따로 집계되어 어느 쪽도 큰 음의 기울기를 갖지 않는다
            Assert.InRange(g.GetProperty("privSlopeMBPerMin").GetDouble(), -0.01, 0.01);
        }
    }

    [Fact]
    public void Gpu_memory_peak_percentage_uses_reported_total()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        for (int i = 0; i < 20; i++)
        {
            var at = T0.AddMinutes(i);
            r.Ingest(at, Res(at, 50), new GpuSample(at, 60, 70, 2600, 200, 3000 + 600 * i, 16376, "NVIDIA GeForce RTX 4070 Ti SUPER"), null);
        }
        var trends = JsonDocument.Parse(JsonSerializer.Serialize(r.Export(T0.AddMinutes(19), null, null).Trends)).RootElement;
        Assert.Equal(16376, trends.GetProperty("gpu_mem_total_mb").GetDouble(), 0);
        Assert.Equal(3000 + 600 * 19, trends.GetProperty("gpu_mem_used_mb_max").GetDouble(), 0);
        Assert.Equal((3000 + 600 * 19) / 16376.0 * 100, trends.GetProperty("gpu_mem_pct_max").GetDouble(), 1);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti SUPER", trends.GetProperty("gpu_name").GetString());
    }

    [Theory]
    [InlineData(17171480576.0, "Data", 16376.0)]   // 0.9.7-pre749 실측: Data 타입인데 바이트
    [InlineData(16.0, "Data", 16384.0)]            // 예전 LHM: Data = GB
    public void Lhm_gpu_memory_value_is_normalized_to_mb(double raw, string type, double expectedMb)
    {
        var t = Enum.Parse<LibreHardwareMonitor.Hardware.SensorType>(type);
        Assert.Equal(expectedMb, BlackBox.Sources.SensorSource.ToMB(t, raw), 0);
    }

    [Fact]
    public void Memory_footprint_for_24h_stays_under_two_mb()
    {
        var r = new MinuteRollup(TimeSpan.FromHours(24));
        var rows = Enumerable.Range(0, 12).Select(k => new ProcSummary(1000 + k, $"proc{k}", 100 + k, 500, false)).ToArray();
        var top = Enumerable.Range(0, 10).Select(k => new ProcUsage(2000 + k, $"busy{k}", 5, 50, 100, 0)).ToArray();
        for (int i = 0; i < 1440; i++)
        {
            var at = T0.AddMinutes(i);
            r.Ingest(at, Res(at, 50, top), new GpuSample(at, 50, 60, 1500, 100), Procs(at, 1000, rows));
        }
        Assert.Equal(1440, r.Count);
        Assert.InRange(r.ApproxBytes, 100_000, 2_000_000);
    }
}
