using System.Text.Json;
using BlackBox.Core;
using BlackBox.Marking;
using Xunit;

namespace BlackBox.Tests;

public class ProcessExitsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 6, 47, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset Mark = T0.AddSeconds(60);

    private static SignalEvent Start(double sec, int pid, string name) =>
        new(T0.AddSeconds(sec), "etw-kernel", Json.Serialize(new { ev = "processStart", pid, ppid = 4, name, cmd = name }));

    private static SignalEvent Stop(double sec, int pid, string name, int exit) =>
        new(T0.AddSeconds(sec), "etw-kernel", Json.Serialize(new { ev = "processStop", pid, name, exit }));

    private static JsonElement Run(IReadOnlyList<SignalEvent> events, string? target = null) =>
        JsonDocument.Parse(Json.Serialize(ProcessExits.Summarize(events, Mark, target))).RootElement;

    [Fact]
    public void A_program_that_ended_with_an_error_code_and_no_crash_event_is_listed()
    {
        // 11/15 실험실 office1: blender.exe 가 켜진 지 5초 만에 종료 코드 11 로 끝났다. Windows 크래시 이벤트는 없었다.
        var root = Run(new[] { Start(38, 5924, "blender.exe"), Stop(43, 5924, "blender.exe", 11), Stop(50, 700, "conhost.exe", 0) }, "blender.exe");

        Assert.Equal(2, root.GetProperty("window_stops").GetInt32());
        Assert.Equal(1, root.GetProperty("nonzero_stops").GetInt32());
        var rows = root.GetProperty("rows");
        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("blender.exe", rows[0].GetProperty("name").GetString());
        Assert.Equal(11u, rows[0].GetProperty("exit_code").GetUInt32());
        Assert.Equal("0x0000000B", rows[0].GetProperty("exit_hex").GetString());
        Assert.Equal(5.0, rows[0].GetProperty("lived_sec").GetDouble());
        Assert.Equal(17.0, rows[0].GetProperty("sec_before_mark").GetDouble());
        Assert.True(rows[0].GetProperty("is_target").GetBoolean());
    }

    [Fact]
    public void Exits_are_grouped_by_name_and_code_and_ordinary_zero_exits_are_only_counted()
    {
        var root = Run(new[]
        {
            Stop(1, 10, "backgroundTaskHost.exe", 1), Stop(12, 11, "backgroundTaskHost.exe", 1), Stop(30, 12, "backgroundTaskHost.exe", 1),
            Stop(20, 13, "cmd.exe", 0), Stop(21, 14, "cmd.exe", 2),
        });

        Assert.Equal(5, root.GetProperty("window_stops").GetInt32());
        Assert.Equal(4, root.GetProperty("nonzero_stops").GetInt32());
        var rows = root.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("backgroundTaskHost.exe", rows[0].GetProperty("name").GetString());   // 마킹에 가장 가까운 것이 먼저
        Assert.Equal(3, rows[0].GetProperty("count").GetInt32());
        Assert.Equal("cmd.exe", rows[1].GetProperty("name").GetString());
        Assert.Equal(2u, rows[1].GetProperty("exit_code").GetUInt32());
        Assert.False(rows[0].TryGetProperty("is_target", out _));
        Assert.False(rows[0].TryGetProperty("lived_sec", out _));                          // 시작이 창 안에 없으면 필드를 뺀다
    }

    [Fact]
    public void A_Windows_exception_code_is_written_as_unsigned_hex()
    {
        // ExitStatus 는 부호 있는 32비트로 기록된다: 0xC0000005(접근 위반) = -1073741819
        var rows = Run(new[] { Stop(10, 20, "FaultLab.exe", -1073741819) }).GetProperty("rows");

        Assert.Equal("0xC0000005", rows[0].GetProperty("exit_hex").GetString());
        Assert.Equal(3221225477u, rows[0].GetProperty("exit_code").GetUInt32());
    }

    [Fact]
    public void The_marked_program_is_listed_even_when_it_ended_with_zero()
    {
        // 사용자가 "blender 가 꺼졌다"고 마킹했는데 0 으로 끝났다는 것도 사실이다. 대상 이름은 .exe 가 있든 없든 맞춘다.
        var rows = Run(new[] { Stop(10, 30, "Blender.exe", 0), Stop(11, 31, "cmd.exe", 0) }, "blender").GetProperty("rows");

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal("Blender.exe", rows[0].GetProperty("name").GetString());
        Assert.Equal(0u, rows[0].GetProperty("exit_code").GetUInt32());
        Assert.True(rows[0].GetProperty("is_target").GetBoolean());
    }

    [Fact]
    public void A_reused_pid_takes_the_start_of_its_own_process()
    {
        var rows = Run(new[] { Start(0, 100, "a.exe"), Stop(5, 100, "a.exe", 0), Start(20, 100, "b.exe"), Stop(23, 100, "b.exe", 3) }).GetProperty("rows");

        Assert.Equal("b.exe", rows[0].GetProperty("name").GetString());
        Assert.Equal(3.0, rows[0].GetProperty("lived_sec").GetDouble());
    }

    [Fact]
    public void More_groups_than_the_limit_are_cut_and_the_cut_is_recorded()
    {
        var events = Enumerable.Range(0, ProcessExits.MaxRows + 5).Select(i => Stop(i, 1000 + i, $"tool{i}.exe", 1)).ToList();

        var root = Run(events);

        Assert.Equal(ProcessExits.MaxRows, root.GetProperty("rows").GetArrayLength());
        Assert.Equal(5, root.GetProperty("rows_omitted").GetInt32());
    }

    [Fact]
    public void Without_any_process_stop_record_there_is_no_summary()
    {
        Assert.Null(ProcessExits.Summarize(new[] { Start(1, 5, "a.exe"), new SignalEvent(T0, "sensors", "{}") }, Mark));
        Assert.Null(ProcessExits.Summarize(Array.Empty<SignalEvent>(), Mark));
    }

    [Fact]
    public void The_engine_snapshot_carries_the_exits_of_the_dump_window()
    {
        var events = new List<SignalEvent> { Start(38, 5924, "blender.exe"), Stop(43, 5924, "blender.exe", 11) };
        var snapshot = EngineSnapshotBuilder.Build(Mark, "manual", null, events, new EngineFacts(), null, null, null,
            new ManualMark("app_crash", "blender.exe"));
        var root = JsonDocument.Parse(Json.Serialize(snapshot)).RootElement;

        var rows = root.GetProperty("process_exits").GetProperty("rows");
        Assert.Equal("blender.exe", rows[0].GetProperty("name").GetString());
        Assert.True(rows[0].GetProperty("is_target").GetBoolean());

        // 종료 기록이 없는 덤프에는 필드가 없다
        var empty = EngineSnapshotBuilder.Build(Mark, "manual", null, new List<SignalEvent>(), new EngineFacts(), null, null);
        Assert.False(JsonDocument.Parse(Json.Serialize(empty)).RootElement.TryGetProperty("process_exits", out _));
    }
}
