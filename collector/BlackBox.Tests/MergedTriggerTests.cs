using System.Text.Json;
using BlackBox.Core;
using BlackBox.Marking;
using Xunit;

namespace BlackBox.Tests;

public class MergedTriggerTests
{
    private static EventLogTrigger Trigger(int id, string provider, string? wer, string kind, string? process, string? module = null, string? code = null) =>
        new(DateTimeOffset.Now, "Application", id, provider, null, "test", process, null, module, kind, wer, code, null, null,
            TriggerClassifier.Classify("Application", id, provider, wer));

    private static JsonElement Build(EventLogTrigger primary, List<EventLogTrigger>? merged)
    {
        var snapshot = EngineSnapshotBuilder.Build(DateTimeOffset.Now, primary.Kind, primary, new List<SignalEvent>(), new EngineFacts(),
            null, null, null, null, merged);
        return JsonDocument.Parse(Json.Serialize(snapshot)).RootElement;
    }

    [Fact]
    public void A_crash_merged_into_an_earlier_system_event_is_listed_in_the_snapshot()
    {
        // 11/14 실험실: LiveKernelEvent(GPU 복제본 자체) 2초 뒤에 notepad++ 크래시 — 덤프는 kernel-live 하나
        var live = Trigger(1001, "Windows Error Reporting", "LiveKernelEvent", "kernel-live", "LiveKernelEvent 0x193");
        var crash = Trigger(1000, "Application Error", null, "crash-1000", "notepad++.exe", "notepad++.exe", "c000041d");

        var root = Build(live, new List<EventLogTrigger> { crash });

        // 증상은 먼저 온 사건의 것 그대로다 — 병합된 사건은 따로 적힌다
        Assert.NotEqual("app_crash", root.GetProperty("symptom").GetProperty("type").GetString());
        var merged = root.GetProperty("merged_triggers");
        Assert.Equal(1, merged.GetArrayLength());
        Assert.Equal("crash-1000", merged[0].GetProperty("kind").GetString());
        Assert.Equal("notepad++.exe", merged[0].GetProperty("process").GetString());
        Assert.Equal("notepad++.exe", merged[0].GetProperty("faulting_module").GetString());
        Assert.Equal("c000041d", merged[0].GetProperty("exception_code").GetString());
        Assert.Equal("app_crash", merged[0].GetProperty("symptom_type").GetString());
    }

    [Fact]
    public void Without_merged_triggers_the_field_is_absent()
    {
        var crash = Trigger(1000, "Application Error", null, "crash-1000", "notepad++.exe", "notepad++.exe", "c000041d");
        Assert.False(Build(crash, null).TryGetProperty("merged_triggers", out _));
        Assert.False(Build(crash, new List<EventLogTrigger>()).TryGetProperty("merged_triggers", out _));
    }
}
