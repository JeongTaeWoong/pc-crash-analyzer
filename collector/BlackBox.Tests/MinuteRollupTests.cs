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
