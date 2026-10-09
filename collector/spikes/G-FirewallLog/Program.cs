// 스파이크 G: 방화벽 로그(pfirewall.log) 존재·로깅 활성화 여부 — 읽기만, 절대 켜지 않는다
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "G-FirewallLog", ["ranAt"] = DateTime.Now.ToString("s") };

try
{
    var profiles = new Dictionary<string, object?>();
    bool anyLogging = false;
    var logPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string defaultPath = Path.Combine(Environment.SystemDirectory, @"LogFiles\Firewall\pfirewall.log");
    logPaths.Add(defaultPath);

    foreach (var p in new[] { "Domain", "Standard", "Public" })
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{p}Profile\Logging");
        if (key == null)
        {
            profiles[p] = new { logging = "값 없음 = 기본값(비활성)", note = "설정 필요" };
            continue;
        }
        int dropped = key.GetValue("LogDroppedPackets") as int? ?? 0;
        int success = key.GetValue("LogSuccessfulConnections") as int? ?? 0;
        string? path = key.GetValue("LogFilePath") as string;
        if (path != null) logPaths.Add(Environment.ExpandEnvironmentVariables(path));
        bool enabled = dropped == 1 || success == 1;
        anyLogging |= enabled;
        profiles[p] = new { logDroppedPackets = dropped, logSuccessfulConnections = success, logFilePath = path, enabled };
    }
    result["profiles"] = profiles;

    var files = new List<object>();
    foreach (var path in logPaths)
    {
        bool exists = File.Exists(path);
        string readable = "n/a";
        long size = -1;
        if (exists)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                size = fs.Length;
                readable = "ok";
            }
            catch (Exception ex) { readable = $"unavailable: {ex.GetType().Name}"; }
        }
        files.Add(new { path, exists, readable, sizeBytes = size });
    }
    result["logFiles"] = files;
    result["loggingEnabledAnyProfile"] = anyLogging;
    result["status"] = "ok";
    if (!anyLogging) result["note"] = "설정 필요: 모든 프로필에서 방화벽 로깅 비활성 (원칙대로 켜지 않음)";
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
