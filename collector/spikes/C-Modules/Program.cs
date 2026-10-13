// 스파이크 C: 지정 PID(기본: notepad.exe를 직접 띄움)의 로드된 모듈 전체 열거.
// 시스템 경로(System32/SysWOW64/WinSxS) 밖 모듈만 따로 표시 (주입 DLL 탐지용 신호).
// 직접 띄운 notepad는 스파이크 종료 시 닫는다 (우리가 만든 프로세스만 — 타 프로세스 종료 금지 원칙과 무관).
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "C-Modules", ["ranAt"] = DateTime.Now.ToString("s") };
string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string[] systemRoots = { Path.Combine(windir, "System32"), Path.Combine(windir, "SysWOW64"), Path.Combine(windir, "WinSxS") };

Process? target = null;
bool spawned = false;
try
{
    if (args.Length > 0 && int.TryParse(args[0], out int pid))
    {
        target = Process.GetProcessById(pid);
    }
    else
    {
        target = Process.Start("notepad.exe");
        spawned = true;
        try { target.WaitForInputIdle(3000); } catch { }
        Thread.Sleep(1500); // 지연 로드 DLL 대기
    }

    result["processName"] = target.ProcessName;
    result["pid"] = target.Id;
    result["spawnedForTest"] = spawned;

    var modules = target.Modules.Cast<ProcessModule>()
        .Select(m => m.FileName)
        .Where(p => !string.IsNullOrEmpty(p))
        .ToList();

    bool IsSystemPath(string p) => systemRoots.Any(r => p.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    string Sanitize(string p) => p.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

    var nonSystem = modules.Where(p => !IsSystemPath(p)).Select(Sanitize).ToList();

    result["moduleCount"] = modules.Count;
    result["nonSystemCount"] = nonSystem.Count;
    result["nonSystemModules"] = nonSystem.Take(30).ToList();
    result["status"] = "ok";
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}
finally
{
    if (spawned && target != null)
    {
        try { target.Kill(); target.WaitForExit(2000); } catch { }
    }
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
