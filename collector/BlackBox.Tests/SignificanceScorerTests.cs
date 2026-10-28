using BlackBox.Core;
using Xunit;

namespace BlackBox.Tests;

public class SignificanceScorerTests
{
    private static SignificanceInput Base(int baseScore = 30) =>
        new(baseScore, null, null, null, null, 0, null, false);

    [Fact]
    public void User_facing_third_party_native_long_lived_crash_is_major()
    {
        var r = SignificanceScorer.Compute(Base() with
        {
            UserFacing = true,
            FaultingModuleName = "aswhook.dll",
            FaultingModulePath = @"C:\Program Files\Avast Software\Avast\aswhook.dll",
            ProcessLifetimeSeconds = 1200,
            ExceptionCode = "c0000005",
        });
        Assert.Equal(100, r.Score); // 30+30+20+15+10 = 105 → clamp
        Assert.Equal("major", r.Tier);
        Assert.Contains("third-party-module", r.Tags);
        Assert.Contains("user-facing", r.Tags);
    }

    [Fact]
    public void Short_lived_dotnet_exception_in_system_module_is_hidden()
    {
        var r = SignificanceScorer.Compute(Base() with
        {
            UserFacing = false,
            FaultingModuleName = "KERNELBASE.dll",
            FaultingModulePath = @"C:\WINDOWS\System32\KERNELBASE.dll",
            ProcessLifetimeSeconds = 0.3,
            ExceptionCode = "e0434352",
        });
        Assert.Equal(10, r.Score); // 30 − 20
        Assert.Equal("hidden", r.Tier);
        Assert.Contains("short-lived", r.Tags);
    }

    [Fact]
    public void Test_artifact_is_capped_to_hidden_even_with_strong_factors()
    {
        var r = SignificanceScorer.Compute(Base() with
        {
            UserFacing = true,
            FaultingModulePath = @"E:\x\FaultPlugin.dll",
            ProcessLifetimeSeconds = 100,
            ExceptionCode = "c0000005",
            IsTestArtifact = true,
        });
        Assert.True(r.Score <= 10);
        Assert.Equal("hidden", r.Tier);
        Assert.Contains("test-artifact", r.Tags);
    }

    [Fact]
    public void Kernel_grade_base_alone_is_major()
    {
        var r = SignificanceScorer.Compute(Base(90));
        Assert.Equal("major", r.Tier);
    }

    [Fact]
    public void Recurrence_adds_points_and_tag()
    {
        var r = SignificanceScorer.Compute(Base() with { RecurrenceCount7d = 3 });
        Assert.Equal(45, r.Score);
        Assert.Contains("recurred-3x-7d", r.Tags);
        Assert.Equal("minor", r.Tier);
    }

    [Fact]
    public void Unknown_module_name_gets_half_credit_runtime_module_gets_none()
    {
        var unknown = SignificanceScorer.Compute(Base() with { FaultingModuleName = "nexonlink-listener.dll" });
        var runtime = SignificanceScorer.Compute(Base() with { FaultingModuleName = "ntdll.dll" });
        Assert.Equal(40, unknown.Score);
        Assert.Equal(30, runtime.Score);
    }

    [Theory]
    [InlineData(60, "major")]
    [InlineData(59, "minor")]
    [InlineData(30, "minor")]
    [InlineData(29, "hidden")]
    public void Tier_boundaries(int baseScore, string tier)
    {
        Assert.Equal(tier, SignificanceScorer.Compute(Base(baseScore)).Tier);
    }

    [Fact]
    public void Breakpoint_exception_lowers_score()
    {
        var r = SignificanceScorer.Compute(Base(40) with { ExceptionCode = "80000003" });
        Assert.Equal(30, r.Score);
    }

    [Fact]
    public void Dotnet_runtime_module_under_program_files_is_not_third_party()
    {
        var r = SignificanceScorer.Compute(Base() with
        {
            FaultingModuleName = "coreclr.dll",
            FaultingModulePath = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.14\coreclr.dll",
            ExceptionCode = "c0000005",
        });
        Assert.DoesNotContain("third-party-module", r.Tags);
        Assert.Equal(40, r.Score); // 30 + nativeFault 10
    }
}
