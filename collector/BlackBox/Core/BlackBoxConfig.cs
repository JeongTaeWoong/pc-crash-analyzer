using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox.Core;

/// <summary>
/// 선택적 설정 파일 blackbox.config.json (실행 폴더 또는 exe 폴더). 없으면 기본값.
/// - testArtifacts: 우리 테스트 바이너리 — 트리거는 하되 유의성 점수를 10 이하로 눌러 UI 기본 숨김
/// - denylist: 아예 트리거하지 않을 프로세스 (기본 비어 있음)
/// </summary>
public sealed class BlackBoxConfig
{
    [JsonPropertyName("testArtifacts")]
    public List<string> TestArtifacts { get; set; } = new() { "CrashDummy.exe", "FaultLab.exe", "FaultHost.exe" };

    [JsonPropertyName("denylist")]
    public List<string> Denylist { get; set; } = new();

    public bool IsTestArtifact(string? processName) =>
        processName != null && TestArtifacts.Any(t => t.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public bool IsDenied(string? processName) =>
        processName != null && Denylist.Any(t => t.Equals(processName, StringComparison.OrdinalIgnoreCase));

    public static BlackBoxConfig Load()
    {
        foreach (var dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var path = Path.Combine(dir, "blackbox.config.json");
            if (!File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<BlackBoxConfig>(File.ReadAllText(path)) ?? new BlackBoxConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[config] {path} 읽기 실패 — 기본값 사용: {ex.Message}");
                return new BlackBoxConfig();
            }
        }
        return new BlackBoxConfig();
    }
}
