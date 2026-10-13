// 스파이크 H: 안정성 모니터(Win32_ReliabilityRecords)로 최근 30일 크래시/행 기록 조회
using System.Management;
using System.Text.Encodings.Web;
using System.Text.Json;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "H-Reliability", ["ranAt"] = DateTime.Now.ToString("s") };
string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var cutoff = DateTime.Now.AddDays(-30);

try
{
    using var searcher = new ManagementObjectSearcher(@"root\cimv2",
        "SELECT SourceName, EventIdentifier, ProductName, TimeGenerated, Message FROM Win32_ReliabilityRecords");

    var records = new List<(string Source, uint EventId, string? Product, DateTime Time, string Message)>();
    foreach (ManagementObject mo in searcher.Get())
    {
        using (mo)
        {
            var time = ManagementDateTimeConverter.ToDateTime(mo["TimeGenerated"] as string ?? "");
            if (time < cutoff) continue;
            string msg = (mo["Message"] as string ?? "").Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace("\r\n", " ").Replace("\n", " ");
            records.Add((mo["SourceName"] as string ?? "", (uint)(mo["EventIdentifier"] ?? 0u), mo["ProductName"] as string, time, msg));
        }
    }

    result["total_30d"] = records.Count;
    result["bySource"] = records.GroupBy(r => r.Source).ToDictionary(g => g.Key, g => g.Count());
    result["recentSamples"] = records.OrderByDescending(r => r.Time).Take(3)
        .Select(r => new
        {
            source = r.Source,
            eventId = r.EventId,
            product = r.Product,
            time = r.Time.ToString("s"),
            message = r.Message.Length > 200 ? r.Message[..200] + "..." : r.Message
        }).ToList();
    result["status"] = "ok";
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
