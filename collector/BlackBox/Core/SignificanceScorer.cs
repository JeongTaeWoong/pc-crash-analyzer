namespace BlackBox.Core;

/// <summary>유의성 점수 입력. 모르는 값은 null로 두면 해당 요소는 0점(판정 보류)이다.</summary>
public sealed record SignificanceInput(
    int BaseScore,
    bool? UserFacing,                 // 창이 있거나 크래시 직전 전경 프로세스였는가
    string? FaultingModuleName,       // 예: ntdll.dll
    string? FaultingModulePath,       // 예: C:\WINDOWS\SYSTEM32\ntdll.dll (1000 [11])
    double? ProcessLifetimeSeconds,   // 시작→크래시
    int RecurrenceCount7d,            // 7일 내 같은 프로그램의 다른 크래시 수
    string? ExceptionCode,            // 예: c0000005
    bool IsTestArtifact);

public sealed record SignificanceResult(int Score, string Tier, IReadOnlyList<string> Tags, IReadOnlyDictionary<string, int> Factors);

/// <summary>
/// 유의성 점수(0~100) — 분석이 아니라 "정렬·숨김 힌트"다. 엔진 판단을 대신하지 않는다.
/// 등급: major(≥60) / minor(30~59) / hidden(&lt;30). 2026-10-28 결정: 낮은 등급도 덤프는 남기고 UI에서 기본 숨김.
/// </summary>
public static class SignificanceScorer
{
    public const int MajorThreshold = 60;
    public const int MinorThreshold = 30;

    // 오류 모듈이 이것들이면 "런타임/OS 내부"라 충돌 단서로 약함
    private static readonly HashSet<string> RuntimeModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll.dll", "kernelbase.dll", "kernel32.dll", "user32.dll", "gdi32.dll", "msvcrt.dll", "ucrtbase.dll",
        "clr.dll", "coreclr.dll", "clrjit.dll", "mscorlib.dll", "mscorwks.dll", "combase.dll", "rpcrt4.dll",
    };

    // GPU 드라이버 사용자 모드 DLL (NVIDIA / AMD / Intel). Windows 폴더(DriverStore) 아래 있어도 "시스템"이 아니라 "드라이버" 단서다 (2026-10-28 발견, docs/slowdown-mechanisms.md S5).
    private static readonly HashSet<string> GpuDriverModules = new(StringComparer.OrdinalIgnoreCase)
    {
        // NVIDIA
        "nvwgf2umx.dll", "nvwgf2um.dll", "nvoglv64.dll", "nvoglv32.dll", "nvd3dumx.dll", "nvd3dum.dll", "nvldumdx.dll", "nvldumd.dll",
        "nvcuda.dll", "nvcuda64.dll", "nvapi64.dll", "nvapi.dll", "nvgpucomp64.dll", "nvlddmkm.sys",
        // AMD
        "atidxx64.dll", "atidxx32.dll", "atiumd64.dll", "atiumdag.dll", "amdxc64.dll", "amdxc32.dll", "aticfx64.dll", "aticfx32.dll",
        "amdvlk64.dll", "atio6axx.dll", "atioglxx.dll", "amdkmdag.sys", "atikmdag.sys",
        // Intel
        "igd10iumd64.dll", "igd10iumd32.dll", "igd12umd64.dll", "igd12umd32.dll", "igdumdim64.dll", "igdumdim32.dll",
        "igc64.dll", "igc32.dll", "ig9icd64.dll", "ig9icd32.dll", "igdkmd64.sys",
    };

    private static readonly HashSet<string> NativeFaultCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "c0000005", // access violation
        "c0000409", // stack buffer overrun / fast fail
        "c000041d", // unhandled exception in callback
        "c0000374", // heap corruption
        "c0000008", // invalid handle
        "c00000fd", // stack overflow
    };

    public static SignificanceResult Compute(SignificanceInput i)
    {
        var factors = new Dictionary<string, int>();
        var tags = new List<string>();
        int score = i.BaseScore;
        factors["base"] = i.BaseScore;

        if (i.UserFacing == true) { score += 30; factors["userFacing"] = 30; tags.Add("user-facing"); }

        // 오류 모듈: 경로가 Windows 디렉터리 밖이면 서드파티(충돌 가설의 핵심), 런타임 목록이면 0, 알 수 없으면 절반
        bool runtimeByName = !string.IsNullOrEmpty(i.FaultingModuleName) && RuntimeModules.Contains(i.FaultingModuleName);
        if (runtimeByName)
        {
            // coreclr.dll 등은 Program Files\dotnet 아래라도 런타임 — .NET 호스트에서 네이티브 DLL이 죽으면 CLR이 가로채 오류 모듈이 coreclr로 찍힌다(10/28 실측)
        }
        else if (!string.IsNullOrEmpty(i.FaultingModuleName) && GpuDriverModules.Contains(i.FaultingModuleName))
        {
            // 경로가 \Windows\System32\DriverStore\ 아래라도 드라이버 단서 — 엔진 gpu_driver 범주로 이어진다 (M4/TDR 메커니즘)
            score += 20; factors["gpuDriverModule"] = 20; tags.Add("gpu-driver-module");
        }
        else if (!string.IsNullOrEmpty(i.FaultingModulePath))
        {
            bool driverStore = i.FaultingModulePath.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase);
            bool systemPath = i.FaultingModulePath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)
                              || i.FaultingModulePath.Contains(@"\dotnet\", StringComparison.OrdinalIgnoreCase);
            if (driverStore) { score += 20; factors["driverStoreModule"] = 20; tags.Add("driver-store-module"); }
            else if (!systemPath) { score += 20; factors["thirdPartyModule"] = 20; tags.Add("third-party-module"); }
        }
        else if (!string.IsNullOrEmpty(i.FaultingModuleName))
        {
            score += 10; factors["unknownModule"] = 10;
        }

        if (i.RecurrenceCount7d >= 2) { score += 15; factors["recurrence"] = 15; tags.Add($"recurred-{i.RecurrenceCount7d}x-7d"); }

        if (i.ProcessLifetimeSeconds is { } life)
        {
            if (life > 30) { score += 15; factors["lifetime"] = 15; }
            else if (life < 5) { score -= 20; factors["lifetime"] = -20; tags.Add("short-lived"); }
        }

        if (!string.IsNullOrEmpty(i.ExceptionCode))
        {
            if (NativeFaultCodes.Contains(i.ExceptionCode)) { score += 10; factors["nativeFault"] = 10; }
            else if (i.ExceptionCode.Equals("80000003", StringComparison.OrdinalIgnoreCase)) { score -= 10; factors["breakpoint"] = -10; }
            // e0434352(.NET 관리 예외) 등은 0 — 앱 자체 버그일 가능성이 커 충돌 단서로 약함
        }

        if (i.IsTestArtifact) { score = Math.Min(score, 10); factors["testArtifact"] = -100; tags.Add("test-artifact"); }

        score = Math.Clamp(score, 0, 100);
        string tier = score >= MajorThreshold ? "major" : score >= MinorThreshold ? "minor" : "hidden";
        return new SignificanceResult(score, tier, tags, factors);
    }
}
