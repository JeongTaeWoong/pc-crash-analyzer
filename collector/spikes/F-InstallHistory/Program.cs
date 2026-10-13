// 스파이크 F: 설치 이력 — Uninstall 레지스트리(64/32비트/HKCU) 최근 30일 + Windows Update 이력(WUA COM)
using System.Globalization;
using System.Management;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "F-InstallHistory", ["ranAt"] = DateTime.Now.ToString("s") };
var cutoff = DateTime.Today.AddDays(-30);

// 1) Uninstall 레지스트리 (HKLM 64/32비트 + HKCU)
try
{
    var entries = new List<(string Name, DateTime Date, string? Version, string Hive)>();
    int noDateCount = 0;

    void ScanKey(RegistryKey? root, string hiveLabel)
    {
        if (root == null) return;
        using (root)
        {
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                var raw = k.GetValue("InstallDate") as string;
                if (string.IsNullOrEmpty(raw) || !DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                { noDateCount++; continue; }
                if (date >= cutoff)
                    entries.Add((name, date, k.GetValue("DisplayVersion") as string, hiveLabel));
            }
        }
    }

    ScanKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKLM64");
    ScanKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKLM32");
    ScanKey(Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKCU");

    result["recentInstalls_30d"] = entries.OrderByDescending(e => e.Date).Take(30)
        .Select(e => new { name = e.Name, date = e.Date.ToString("yyyy-MM-dd"), version = e.Version, hive = e.Hive }).ToList();
    result["entriesWithoutInstallDate"] = noDateCount;
}
catch (Exception ex) { result["recentInstalls_30d"] = $"unavailable: {ex.GetType().Name}: {ex.Message}"; }

// 2) Windows Update 이력 (WUA COM, 읽기 전용)
try
{
    var t = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("WUA ProgID 없음");
    dynamic session = Activator.CreateInstance(t)!;
    dynamic searcher = session.CreateUpdateSearcher();
    int total = searcher.GetTotalHistoryCount();
    dynamic history = searcher.QueryHistory(0, Math.Min(total, 100));
    var updates = new List<object>();
    for (int i = 0; i < history.Count; i++)
    {
        dynamic entry = history.Item(i);
        DateTime date = entry.Date;
        if (date.ToLocalTime() < cutoff) continue;
        string title = entry.Title ?? "";
        updates.Add(new { date = date.ToLocalTime().ToString("yyyy-MM-dd"), title = title.Length > 80 ? title[..80] + "..." : title, resultCode = (int)entry.ResultCode });
        if (updates.Count >= 20) break;
    }
    result["windowsUpdates_30d"] = updates;
    result["updateHistoryTotalCount"] = total;
    result["status"] = "ok";
}
catch (Exception ex)
{
    // 폴백: QFE (핫픽스만 나옴)
    try
    {
        using var qfe = new ManagementObjectSearcher("SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering");
        result["windowsUpdates_30d"] = qfe.Get().Cast<ManagementObject>()
            .Select(m => new { id = m["HotFixID"] as string, installedOn = m["InstalledOn"] as string }).ToList();
        result["status"] = "ok";
        result["note"] = $"WUA COM 실패({ex.GetType().Name}) — QFE 폴백 사용";
    }
    catch (Exception ex2)
    {
        result["status"] = "unavailable";
        result["reason"] = $"WUA: {ex.Message} / QFE: {ex2.Message}";
    }
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
