// 스파이크 D: 입력 장치 UpperFilters + fltmc 미니필터 목록 + 커널 드라이버 서비스 열거 (전부 읽기만)
using System.Diagnostics;
using System.Management;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Win32;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "D-FilterDrivers", ["ranAt"] = DateTime.Now.ToString("s") };

// 1) 키보드/마우스 클래스 UpperFilters
var upperFilters = new Dictionary<string, object?>();
foreach (var (name, guid) in new[] { ("keyboard", "{4D36E96B-E325-11CE-BFC1-08002BE10318}"), ("mouse", "{4D36E96F-E325-11CE-BFC1-08002BE10318}") })
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{guid}");
        upperFilters[name] = key?.GetValue("UpperFilters") as string[] ?? (object?)"(값 없음)";
    }
    catch (Exception ex) { upperFilters[name] = $"unavailable: {ex.GetType().Name}"; }
}
result["upperFilters"] = upperFilters;

// 2) fltmc filters (미니필터) — 비관리자면 보통 액세스 거부
try
{
    var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "fltmc.exe"), "filters")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string stdout = p.StandardOutput.ReadToEnd();
    string stderr = p.StandardError.ReadToEnd();
    p.WaitForExit(5000);
    if (p.ExitCode == 0)
    {
        // 헤더 줄 수가 가변적(빈 줄 포함 가능)이라 Skip 대신 내용으로 거른다: 구분선(---)과 제목 줄 제외
        var names = stdout.Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-") && !l.StartsWith("필터") && !l.StartsWith("Filter"))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToList();
        result["fltmc"] = new { status = "ok", count = names.Count, filters = names };
    }
    else
    {
        result["fltmc"] = new { status = "unavailable", exitCode = p.ExitCode, reason = (stdout + stderr).Trim().Replace("\r\n", " ") };
    }
}
catch (Exception ex) { result["fltmc"] = new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }; }

// 3) 커널 드라이버 타입 서비스 (Win32_SystemDriver) — 표준 경로 밖 드라이버만 따로
try
{
    using var searcher = new ManagementObjectSearcher("SELECT Name, State, StartMode, PathName FROM Win32_SystemDriver");
    var drivers = searcher.Get().Cast<ManagementObject>()
        .Select(d => new
        {
            Name = d["Name"] as string ?? "",
            State = d["State"] as string ?? "",
            PathName = (d["PathName"] as string ?? "").ToLowerInvariant()
        }).ToList();
    var running = drivers.Where(d => d.State == "Running").ToList();
    var nonStandardPath = running
        .Where(d => d.PathName.Length > 0 && !d.PathName.Contains(@"\system32\drivers\") && !d.PathName.Contains(@"\system32\driverstore\"))
        .Select(d => new { d.Name, path = d.PathName }).Take(20).ToList();
    result["kernelDrivers"] = new { total = drivers.Count, running = running.Count, nonStandardPathRunning = nonStandardPath };
    result["status"] = "ok";
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
