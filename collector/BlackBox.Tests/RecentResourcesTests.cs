using System.Text.Json;
using BlackBox.Core;
using Xunit;

namespace BlackBox.Tests;

public class RecentResourcesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 13, 0, 0, TimeSpan.FromHours(9));

    private static SignalEvent Res(int sec, double commitPct, double availMB, double pages, double? cpuTotal, string? cpuAt, object[] top, object? disk = null) =>
        new(T0.AddSeconds(sec), "resources", Json.Serialize(new
        {
            memory = new { committedMB = 1000, commitLimitMB = 5500, committedPct = commitPct, availMB, pagesPerSec = pages },
            disk,
            cpuTotalPct = cpuTotal,
            cpuAt,
            topCpu = top,
        }));

    private static object Top(int pid, string name, double cpu, double priv, double io) => new { Pid = pid, Name = name, Cpu = cpu, PrivMB = priv, Handles = 10, io };

    private static SignalEvent Procs(int sec, params (int Pid, string Name, double Priv)[] rows) =>
        new(T0.AddSeconds(sec), "processes", Json.Serialize(new { processes = rows.Select(r => new { pid = r.Pid, name = r.Name, wsMB = 1.0, privMB = r.Priv }).ToArray() }));

    private static JsonElement Run(params SignalEvent[] events) =>
        JsonDocument.Parse(Json.Serialize(RecentResources.Summarize(events))).RootElement;

    [Fact]
    public void No_resource_events_gives_null()
    {
        Assert.Null(RecentResources.Summarize(new[] { new SignalEvent(T0, "sensors", "{}") }));
    }

    [Fact]
    public void Repeated_cpu_readings_count_once()
    {
        // 수집기는 CPU 를 30초마다 재고 10초 샘플 3개에 같은 값을 싣는다 — 측정 2번으로 세어야 한다
        var a = new[] { Top(10, "hog", 99, 8, 0), Top(11, "other", 1, 50, 0) };
        var b = new[] { Top(10, "hog", 91, 8, 0), Top(11, "other", 3, 50, 0) };
        var r = Run(
            Res(0, 50, 1000, 0, 100, "t1", a), Res(10, 50, 1000, 10, 100, "t1", a), Res(20, 50, 1000, 10, 100, "t1", a),
            Res(30, 50, 1000, 10, 96, "t2", b), Res(40, 50, 1000, 10, 96, "t2", b));
        Assert.Equal(2, r.GetProperty("cpu_measurements").GetInt32());
        Assert.Equal(5, r.GetProperty("samples").GetInt32());
        Assert.Equal(40, r.GetProperty("window_seconds").GetInt32());
        var hog = r.GetProperty("top_cpu")[0];
        Assert.Equal("hog", hog.GetProperty("name").GetString());
        Assert.Equal(95, hog.GetProperty("cpu_avg").GetDouble());
        Assert.Equal(99, hog.GetProperty("cpu_max").GetDouble());
        Assert.Equal(98, r.GetProperty("cpu_total_pct_avg").GetDouble());
    }

    [Fact]
    public void Older_dumps_without_cpuAt_fall_back_to_identical_lists()
    {
        var a = new[] { Top(10, "hog", 99, 8, 0) };
        var b = new[] { Top(10, "hog", 50, 8, 0) };
        var r = Run(Res(0, 50, 1000, 0, 100, null, a), Res(10, 50, 1000, 0, 100, null, a), Res(20, 50, 1000, 0, 60, null, b));
        Assert.Equal(2, r.GetProperty("cpu_measurements").GetInt32());
    }

    [Fact]
    public void A_process_missing_from_a_reading_counts_as_zero_in_the_average()
    {
        // 범인이 측정 4번 중 2번만 상위에 있었다면 평균은 절반
        var idle = new[] { Top(11, "other", 2, 50, 0) };
        var busy = new[] { Top(10, "hog", 100, 8, 0), Top(11, "other", 1, 50, 0) };
        var r = Run(Res(0, 50, 1000, 0, 5, "t1", idle), Res(30, 50, 1000, 0, 5, "t2", idle), Res(60, 50, 1000, 0, 100, "t3", busy), Res(90, 50, 1000, 0, 100, "t4", busy));
        var hog = r.GetProperty("top_cpu")[0];
        Assert.Equal("hog", hog.GetProperty("name").GetString());
        Assert.Equal(50, hog.GetProperty("cpu_avg").GetDouble());
        Assert.Equal(2, hog.GetProperty("measurements").GetInt32());
        // 잡힌 동안의 평균은 묽어지지 않는다 (2026-11-14)
        Assert.Equal(100, hog.GetProperty("cpu_avg_active").GetDouble());
        var other = r.GetProperty("top_cpu")[1];
        Assert.Equal(1.5, other.GetProperty("cpu_avg_active").GetDouble());   // (2 + 2 + 1 + 1) / 4 — 네 번 모두 잡혔으니 창 평균과 같다
        Assert.Equal(1.5, other.GetProperty("cpu_avg").GetDouble());
    }

    [Fact]
    public void Disk_saturation_is_counted_per_sample_and_io_has_an_active_average()
    {
        // 11/14 office2 disk-hog: 마킹 직전 1분만 포화 — 창 평균은 낮고, 90% 이상 표본 수가 그 사실을 보여 준다
        object D(double busy, double lat) => new { disks = new[] { new { name = "0 C:", busyPct = busy, queue = 1.0, latencyMs = lat, iops = 100.0, mbps = 5.0 } } };
        var quiet = new[] { Top(11, "other", 2, 50, 0) };
        var writing = new[] { Top(10, "writer", 3, 10, 12.0), Top(11, "other", 2, 50, 0) };
        var r = Run(
            Res(0, 50, 1000, 0, 5, "t1", quiet, D(1, 0.3)), Res(10, 50, 1000, 0, 5, "t1", quiet, D(2, 0.3)), Res(20, 50, 1000, 0, 5, "t1", quiet, D(1, 0.3)),
            Res(30, 50, 1000, 0, 5, "t2", quiet, D(1, 0.3)), Res(40, 50, 1000, 0, 5, "t2", quiet, D(89.9, 0.3)), Res(50, 50, 1000, 0, 5, "t2", quiet, D(1, 0.3)),
            Res(60, 50, 1000, 0, 5, "t3", writing, D(99, 300)), Res(70, 50, 1000, 0, 5, "t3", writing, D(100, 20)), Res(80, 50, 1000, 0, 5, "t3", writing, D(90, 10)));
        var d = r.GetProperty("disks")[0];
        Assert.Equal(3, d.GetProperty("busy_ge90_samples").GetInt32());
        Assert.True(d.GetProperty("busy_pct_avg").GetDouble() < 50);
        var io = r.GetProperty("top_io")[0];
        Assert.Equal(4, io.GetProperty("io_mbps_avg").GetDouble());          // 12 / 측정 3회
        Assert.Equal(12, io.GetProperty("io_mbps_avg_active").GetDouble());  // 잡힌 1회의 평균
        // 마킹 무렵: 마지막 표본 6개의 디스크, 마지막 측정의 프로세스 값
        Assert.Equal(63.5, d.GetProperty("busy_pct_recent").GetDouble());     // (1 + 89.9 + 1 + 99 + 100 + 90) / 6 = 63.48
        Assert.Equal(5, d.GetProperty("mbps_recent").GetDouble());
        Assert.Equal(12, io.GetProperty("io_mbps_last").GetDouble());
    }

    [Fact]
    public void A_process_absent_from_the_last_reading_has_no_last_value()
    {
        var early = new[] { Top(10, "gone", 80, 8, 5.0), Top(11, "other", 2, 50, 0) };
        var late = new[] { Top(11, "other", 3, 50, 0) };
        var r = Run(Res(0, 50, 1000, 0, 90, "t1", early), Res(30, 50, 1000, 0, 5, "t2", late));
        var gone = r.GetProperty("top_cpu")[0];
        Assert.Equal("gone", gone.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, gone.GetProperty("cpu_last").ValueKind);
        Assert.Equal(3, r.GetProperty("top_cpu")[1].GetProperty("cpu_last").GetDouble());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("top_io")[0].GetProperty("io_mbps_last").ValueKind);
    }

    [Fact]
    public void Commit_is_the_last_raw_sample_not_a_minute_average()
    {
        // 11/12 design1: 마킹 시점 실제 96.4%, 분 단위 롤업은 33.6% 였다
        var top = new[] { Top(11, "other", 2, 50, 0) };
        var r = Run(Res(0, 33.6, 4646, 0, 5, "t1", top), Res(10, 96.4, 477, 0, 5, "t1", top), Res(20, 96.3, 1276, 0, 5, "t1", top));
        Assert.Equal(96.3, r.GetProperty("commit_pct_last").GetDouble());
        Assert.Equal(96.4, r.GetProperty("commit_pct_max").GetDouble());
        Assert.Equal(477, r.GetProperty("avail_mb_min").GetDouble());
        Assert.Equal(5500, r.GetProperty("commit_limit_mb").GetDouble());
    }

    [Fact]
    public void Memory_holders_come_from_the_process_list_with_growth_inside_the_window()
    {
        // 메모리만 쥐고 CPU 는 안 쓰는 프로세스는 topCpu 에 없다 — processes 이벤트에서 찾는다
        var r = Run(
            Procs(0, (1, "chrome", 400), (2, "small", 10)),
            Procs(60, (1, "chrome", 410), (2, "small", 10), (3, "leaker", 100)),
            Procs(120, (1, "chrome", 405), (2, "small", 10), (3, "leaker", 7100)));
        var top = r.GetProperty("top_memory");
        Assert.Equal("leaker", top[0].GetProperty("name").GetString());
        Assert.Equal(7100, top[0].GetProperty("priv_mb_last").GetDouble());
        Assert.Equal(7000, top[0].GetProperty("priv_mb_delta").GetDouble());
        Assert.Equal(60, top[0].GetProperty("seconds").GetInt32());
        Assert.True(top[0].GetProperty("started_in_window").GetBoolean());   // 창의 첫 목록에는 없었다
        Assert.False(top[1].GetProperty("started_in_window").GetBoolean());  // chrome 은 처음부터 있었다
        var growth = r.GetProperty("top_memory_growth");
        Assert.Equal(1, growth.GetArrayLength());           // chrome +5MB 는 평소 흔들림이라 빠진다
        Assert.Equal("leaker", growth[0].GetProperty("name").GetString());
    }

    [Fact]
    public void A_process_that_ended_before_the_last_list_is_not_reported_as_a_holder()
    {
        var r = Run(Procs(0, (3, "gone", 5000), (1, "chrome", 400)), Procs(60, (1, "chrome", 400)));
        Assert.Equal("chrome", r.GetProperty("top_memory")[0].GetProperty("name").GetString());
        Assert.Equal(1, r.GetProperty("top_memory").GetArrayLength());
    }

    [Fact]
    public void Io_and_disk_rows()
    {
        var top = new[] { Top(10, "writer", 15, 10, 1121.13), Top(11, "other", 2, 50, 0.2) };
        var busy = new { disks = new[] { new { name = "0 C:", busyPct = 98.0, queue = 4.5, latencyMs = 120.0, iops = 480.0, mbps = 30.0 }, new { name = "1 D:", busyPct = 0.0, queue = 0.0, latencyMs = 0.0, iops = 0.0, mbps = 0.0 } } };
        var warming = new { status = "warming" };
        var r = Run(Res(0, 50, 1000, 0, 20, "t1", top, warming), Res(10, 50, 1000, 100000, 20, "t1", top, busy), Res(20, 50, 1000, 130000, 20, "t1", top, busy));
        Assert.Equal("writer", r.GetProperty("top_io")[0].GetProperty("name").GetString());
        Assert.Equal(1121.13, r.GetProperty("top_io")[0].GetProperty("io_mbps_max").GetDouble());
        Assert.Equal(1, r.GetProperty("top_io").GetArrayLength());   // 1MB/s 미만은 빠진다
        var d = r.GetProperty("disks");
        Assert.Equal("0 C:", d[0].GetProperty("name").GetString()); // 가장 바쁜 디스크가 먼저
        Assert.Equal(2, d[0].GetProperty("samples").GetInt32());    // 첫 샘플(준비 중)은 세지 않는다
        Assert.Equal(98, d[0].GetProperty("busy_pct_avg").GetDouble());
        Assert.Equal(130000, r.GetProperty("pages_per_sec_max").GetDouble());
    }

    [Fact]
    public void Dumps_made_before_the_disk_signal_have_no_disks_field()
    {
        var r = Run(Res(0, 50, 1000, 0, 20, "t1", new[] { Top(11, "other", 2, 50, 0) }));
        Assert.False(r.TryGetProperty("disks", out _));
    }

    [Fact]
    public void Malformed_events_are_skipped()
    {
        var r = Run(new SignalEvent(T0, "resources", "not json"), Res(10, 50, 1000, 0, 20, "t1", new[] { Top(11, "other", 2, 50, 0) }));
        Assert.Equal(1, r.GetProperty("samples").GetInt32());
    }
}
