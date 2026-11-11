using BlackBox.Core;
using Xunit;

namespace BlackBox.Tests;

// 2026-10-28 30일 재분류에서 확인된 유형들로 1단계 트리거 분류를 고정한다.
public class TriggerClassifierTests
{
    [Theory]
    [InlineData("RADAR_PRE_LEAK_64")]
    [InlineData("RADAR_PRE_LEAK_32")]
    [InlineData("StoreAgentScanForUpdatesFailure0")]
    [InlineData("crashpad_log")]
    [InlineData("FaultTolerantHeap")]
    public void Wer_non_crash_reports_are_not_triggers(string werType)
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", werType);
        Assert.False(c.IsTrigger);
        Assert.Null(c.Kind);
    }

    [Theory]
    [InlineData("APPCRASH")]
    [InlineData("CLR20r3")]
    [InlineData("BEX64")]
    [InlineData("MoAppCrash")]
    public void Wer_crash_reports_are_crash_1001(string werType)
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", werType);
        Assert.Equal("crash-1001", c.Kind);
        Assert.Equal("app_crash", c.SymptomType);
        Assert.Equal(30, c.BaseScore);
    }

    [Fact]
    public void LiveKernelEvent_is_system_grade()
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", "LiveKernelEvent");
        Assert.Equal("kernel-live", c.Kind);
        Assert.Equal("system_wide", c.Scope);
        Assert.Equal(90, c.BaseScore);
    }

    [Fact]
    public void AppHang_report_is_hang_1001()
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", "AppHangB1");
        Assert.Equal("hang-1001", c.Kind);
        Assert.Equal("freeze", c.SymptomType);
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("slow", "slow", null)]
    [InlineData("blender", null, "blender")]
    [InlineData("slow blender", "slow", "blender")]
    [InlineData("FREEZE Unity.exe", "freeze", "Unity.exe")]
    public void Manual_mark_console_arguments_are_parsed(string args, string? symptom, string? target)
    {
        var m = BlackBox.Marking.ManualMark.Parse(args);
        Assert.Equal(symptom, m.Symptom);
        Assert.Equal(target, m.Target);
    }

    [Fact]
    public void Processor_power_37_is_buffered_but_not_a_trigger()
    {
        var c = TriggerClassifier.Classify("System", 37, "Microsoft-Windows-Kernel-Processor-Power", null);
        Assert.False(c.IsTrigger);
    }

    [Fact]
    public void Application_hang_1002_is_freeze()
    {
        var c = TriggerClassifier.Classify("Application", 1002, "Application Hang", null);
        Assert.Equal("hang-1002", c.Kind);
        Assert.Equal("freeze", c.SymptomType);
        Assert.Equal(60, c.BaseScore);
    }

    [Fact]
    public void Application_error_1000_is_crash()
    {
        var c = TriggerClassifier.Classify("Application", 1000, "Application Error", null);
        Assert.Equal("crash-1000", c.Kind);
        Assert.Equal("single_app", c.Scope);
    }

    [Fact]
    public void BugCheck_1001_only_from_system_error_reporting_provider()
    {
        Assert.Equal("bsod-1001", TriggerClassifier.Classify("System", 1001, TriggerClassifier.BugCheckProvider, null).Kind);
        Assert.False(TriggerClassifier.Classify("System", 1001, "Some-Other-Provider", null).IsTrigger);
    }

    [Fact]
    public void Whea_only_from_whea_provider()
    {
        Assert.Equal("whea", TriggerClassifier.Classify("System", 18, TriggerClassifier.WheaProvider, null).Kind);
        Assert.False(TriggerClassifier.Classify("System", 18, "Other", null).IsTrigger);
    }

    [Fact]
    public void Unknown_wer_type_triggers_conservatively_with_low_score()
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", "SomeNewTypeXYZ");
        Assert.Equal("crash-1001", c.Kind);
        Assert.Equal(20, c.BaseScore);
    }

    [Fact]
    public void Kernel_power_41_and_tdr_4101()
    {
        Assert.Equal("power-41", TriggerClassifier.Classify("System", 41, "Microsoft-Windows-Kernel-Power", null).Kind);
        Assert.Equal("display-4101", TriggerClassifier.Classify("System", 4101, "Display", null).Kind);
    }

    // 2026-10-29 전체 로그 재검증에서 나온 실제 패턴들
    [Fact]
    public void Event_1000_from_other_provider_is_not_a_crash()
    {
        var c = TriggerClassifier.Classify("Application", 1000, ".NET Runtime", null);
        Assert.False(c.IsTrigger);
    }

    [Fact]
    public void Kernel_power_41_split_by_bugcheck_sleep_and_power_button()
    {
        const string kp = "Microsoft-Windows-Kernel-Power";
        var bsod = TriggerClassifier.Classify("System", 41, kp, null, new EventDetail(BugcheckCode: 0x116));
        Assert.Equal("bsod-41", bsod.Kind);
        Assert.Equal(90, bsod.BaseScore);

        var sleep = TriggerClassifier.Classify("System", 41, kp, null, new EventDetail(0, 6, false)); // 이 PC 110건 패턴
        Assert.Equal("power-41", sleep.Kind);
        Assert.Equal(40, sleep.BaseScore);

        var button = TriggerClassifier.Classify("System", 41, kp, null, new EventDetail(0, 0, true));
        Assert.Equal(60, button.BaseScore);

        var sudden = TriggerClassifier.Classify("System", 41, kp, null, new EventDetail(0, 0, false)); // 0/0/0
        Assert.Equal(90, sudden.BaseScore);
    }

    [Theory]
    [InlineData("141", "flicker", 80)]   // VIDEO_ENGINE_TIMEOUT_DETECTED — 4101 과 같은 시각에 관측
    [InlineData("117", "flicker", 80)]   // VIDEO_TDR_TIMEOUT_DETECTED
    [InlineData("144", "freeze", 60)]    // BUGCODE_USB3_DRIVER
    [InlineData("1b8", "freeze", 90)]    // 그 밖의 라이브 덤프
    public void Live_kernel_event_code_decides_symptom(string code, string symptom, int baseScore)
    {
        var c = TriggerClassifier.Classify("Application", 1001, "Windows Error Reporting", "LiveKernelEvent", new EventDetail(LiveKernelCode: code));
        Assert.Equal("kernel-live", c.Kind);
        Assert.Equal(symptom, c.SymptomType);
        Assert.Equal(baseScore, c.BaseScore);
    }
}
