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
    public void Gpu_driver_dll_under_driverstore_is_a_driver_clue_not_system()
    {
        var path = @"C:\WINDOWS\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_123\nvwgf2umx.dll";
        Assert.DoesNotContain('\n', path); // 2026-10-29: 인라인 패치 때 백슬래시가 깨져 줄바꿈이 들어갔던 사고 재발 방지
        Assert.Equal(7, path.Split('\\').Length);
        var r = SignificanceScorer.Compute(Base() with
        {
            UserFacing = true,
            FaultingModuleName = "nvwgf2umx.dll",
            FaultingModulePath = path,
            ExceptionCode = "c0000005",
        });
        Assert.Contains("gpu-driver-module", r.Tags);
        Assert.DoesNotContain("third-party-module", r.Tags);
        Assert.Equal(90, r.Score); // 30 + userFacing 30 + gpuDriver 20 + nativeFault 10
        Assert.Equal("major", r.Tier);
    }

    [Fact]
    public void Unknown_dll_under_driverstore_is_tagged_driver_store()
    {
        var path = @"C:\WINDOWS\System32\DriverStore\FileRepository\realtek.inf_amd64_1\RTKVHD64.dll";
        Assert.DoesNotContain('\r', path);
        Assert.Equal("RTKVHD64.dll", Path.GetFileName(path));
        var r = SignificanceScorer.Compute(Base() with
        {
            FaultingModuleName = "RTKVHD64.dll",
            FaultingModulePath = path,
        });
        Assert.Contains("driver-store-module", r.Tags);
        Assert.Equal(50, r.Score);
    }

    [Fact]
    public void Plain_system32_module_gets_no_module_credit()
    {
        var r = SignificanceScorer.Compute(Base() with
        {
            FaultingModuleName = "d3d11.dll",
            FaultingModulePath = @"C:\WINDOWS\System32\d3d11.dll",
        });
        Assert.Equal(30, r.Score);
        Assert.Empty(r.Tags);
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
