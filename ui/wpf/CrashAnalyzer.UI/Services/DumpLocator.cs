using System.IO;

namespace CrashAnalyzer.UI.Services;

public static class DumpLocator
{
    /// <summary>탐색 루트 아래에서 가장 최근의 수집기 덤프(engine-snapshot.json)를 찾는다.</summary>
    public static string? FindLatestEngineSnapshot(string searchRoot)
    {
        if (!Directory.Exists(searchRoot)) return null;
        try
        {
            return Directory.EnumerateFiles(searchRoot, "engine-snapshot.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException) { return null; }
    }
}
