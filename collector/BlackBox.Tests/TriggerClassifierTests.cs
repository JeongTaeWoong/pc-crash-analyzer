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
}
