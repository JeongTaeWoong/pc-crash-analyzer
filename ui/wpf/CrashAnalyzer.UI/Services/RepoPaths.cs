using System.IO;

namespace CrashAnalyzer.UI.Services;

/// <summary>저장소 루트와 엔진/수집기/덤프 경로 해석. 환경 변수 CE_ENGINE, CE_DUMPS로 재지정 가능.</summary>
public static class RepoPaths
{
    /// <summary>exe 위치/현재 폴더에서 위로 올라가며 engine+collector 폴더가 함께 있는 곳을 찾는다.</summary>
    public static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "engine")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "collector")))
                    return dir.FullName;
            }
        }
        return null;
    }

    public static string ResolveEnginePath(string repoRoot)
    {
        var env = Environment.GetEnvironmentVariable("CE_ENGINE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var p = Path.Combine(repoRoot, "engine", "build", "Release", "crash_engine.exe");
        if (File.Exists(p)) return p;
        throw new FileNotFoundException(
            "crash_engine.exe를 찾지 못했습니다. engine/README.md의 빌드 절차를 먼저 실행하거나 CE_ENGINE 환경 변수로 경로를 지정하세요.", p);
    }

    /// <summary>덤프 탐색 루트. 기본은 저장소의 collector 폴더 전체.</summary>
    public static string ResolveDumpsRoot(string repoRoot)
    {
        var env = Environment.GetEnvironmentVariable("CE_DUMPS");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return Path.Combine(repoRoot, "collector");
    }

    public static string BlackBoxProjectDir(string repoRoot) => Path.Combine(repoRoot, "collector", "BlackBox");

    public static string? BlackBoxExePath(string repoRoot)
    {
        var p = Path.Combine(BlackBoxProjectDir(repoRoot), "bin", "Release", "net8.0-windows", "BlackBox.exe");
        return File.Exists(p) ? p : null;
    }

    public static string SessionDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pc_crash_analyzer");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
