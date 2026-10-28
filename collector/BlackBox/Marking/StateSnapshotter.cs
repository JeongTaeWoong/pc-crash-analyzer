using System.Diagnostics;
using System.Globalization;
using System.Management;
using BlackBox.Sources;
using Microsoft.Win32;

namespace BlackBox.Marking;

/// <summary>
/// 마킹 시점의 상태 스냅샷: 전체 프로세스 + 프로세스별 비시스템 DLL, 필터 드라이버, 센서 현재값, 최근 설치.
/// 전부 읽기 전용. 실패 항목은 unavailable로 기록하고 계속 진행한다.
/// </summary>
public sealed class StateSnapshotter
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string[] SystemRoots =
    {
        Path.Combine(WinDir, "System32"),
        Path.Combine(WinDir, "SysWOW64"),
        Path.Combine(WinDir, "WinSxS"),
    };

    private readonly SensorSource _sensorSource;

    public StateSnapshotter(SensorSource sensorSource) => _sensorSource = sensorSource;

    public SnapshotResult Capture()
    {
        var sw = Stopwatch.StartNew();
        var facts = new EngineFacts();
        int fgPid = ProcessListSource.GetForegroundPid();
        var snapshot = new Dictionary<string, object?>
        {
            ["capturedAt"] = DateTimeOffset.Now,
            ["fgPidAtMark"] = fgPid,
            ["processes"] = Guard(CaptureProcessModules),
            ["filterDrivers"] = Guard(() => CaptureFilterDrivers(facts)),
            ["sensors"] = _sensorSource.Latest ?? new { status = "unavailable", reason = "센서 샘플 없음" },
            ["recentInstalls30d"] = Guard(() => CaptureRecentInstalls(facts)),
            ["securityProducts"] = Guard(() => CaptureSecurityProducts(facts)),
            ["appCrashLogs"] = Guard(CaptureAppCrashLogs),
        };
        snapshot["captureDurationMs"] = sw.ElapsedMilliseconds;
        return new SnapshotResult(snapshot, facts, fgPid);
    }

    /// <summary>
    /// 앱 자체 크래시 로그의 "위치·크기·시각"만 기록한다 (docs/slowdown-mechanisms.md S6). 내용은 읽지 않는다 —
    /// Blender 애드온·Maya 스크립트처럼 DLL 이 아닌 플러그인은 모듈 목록에 안 뜨므로, 사용자가 UI 안내를 따라 직접 열어 볼 수 있게 경로를 남긴다.
    /// 10/29 확인: Unity Crashes 폴더의 crash.dmp 와 Application 1000 이 1:1 대응. 최근 30일, 폴더당 최대 10건.
    /// </summary>
    private static object CaptureAppCrashLogs()
    {
        var cutoff = DateTime.Now.AddDays(-30);
        string temp = Path.GetTempPath();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sources = new (string Name, string Dir, string Pattern, bool Recurse)[]
        {
            ("blender", temp, "*.crash.txt", false),                                              // Blender 매뉴얼: 임시 폴더의 <blend>.crash.txt
            ("unity", Path.Combine(temp, "Unity", "Editor", "Crashes"), "*", false),              // Unity 매뉴얼: %TMP%\Unity\Editor\Crashes
            ("wer-archive", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), "*", false),
            ("wer-queue", Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"), "*", false),
            ("maya", temp, "*.dmp", false),                                                       // Maya 크래시 리포트(.dmp)는 %TEMP% 에 생성(커뮤니티 확인)
        };

        var result = new List<object>();
        foreach (var (name, dir, pattern, _) in sources)
        {
            try
            {
                if (!Directory.Exists(dir)) { result.Add(new { app = name, status = "absent", dir }); continue; }
                var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(pattern)
                    .Where(e => e.LastWriteTime >= cutoff)
                    .OrderByDescending(e => e.LastWriteTime)
                    .Take(10)
                    .Select(e => new
                    {
                        path = e.FullName,
                        modified = new DateTimeOffset(e.LastWriteTime),
                        sizeBytes = e is FileInfo f ? f.Length : (long?)null,
                        isDir = e is DirectoryInfo,
                    }).ToList();
                result.Add(new { app = name, dir, recent30d = entries.Count, entries });
            }
            catch (Exception ex) { result.Add(new { app = name, status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }); }
        }
        return result;
    }


    /// <summary>SecurityCenter2에 등록된 실시간 보안 제품 (스파이크에서 비관리자 검증됨).</summary>
    private static object CaptureSecurityProducts(EngineFacts facts)
    {
        using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                if (mo["displayName"] is string name && !string.IsNullOrWhiteSpace(name) && !facts.SecurityProducts.Contains(name))
                    facts.SecurityProducts.Add(name);
            }
        }
        return new { antivirus = facts.SecurityProducts };
    }

    private static object Guard(Func<object> capture)
    {
        try { return capture(); }
        catch (Exception ex) { return new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }; }
    }

    private static object CaptureProcessModules()
    {
        int total = 0, accessDenied = 0;
        var withNonSystem = new List<object>();
        foreach (var p in Process.GetProcesses())
        {
            total++;
            try
            {
                var nonSystem = new List<string>();
                foreach (ProcessModule m in p.Modules)
                {
                    var file = m.FileName;
                    if (!string.IsNullOrEmpty(file) && !SystemRoots.Any(r => file.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                        nonSystem.Add(file);
                }
                if (nonSystem.Count > 0)
                    withNonSystem.Add(new { pid = p.Id, name = p.ProcessName, count = nonSystem.Count, modules = nonSystem.Take(40).ToList() });
            }
            catch { accessDenied++; }
            finally { p.Dispose(); }
        }
        return new { total, accessDenied, withNonSystemModules = withNonSystem };
    }

    private static object CaptureFilterDrivers(EngineFacts facts)
    {
        var result = new Dictionary<string, object?>();

        // 키보드/마우스 UpperFilters
        var upper = new Dictionary<string, object?>();
        foreach (var (name, guid) in new[]
                 {
                     ("keyboard", "{4D36E96B-E325-11CE-BFC1-08002BE10318}"),
                     ("mouse", "{4D36E96F-E325-11CE-BFC1-08002BE10318}"),
                 })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{guid}");
                var filters = key?.GetValue("UpperFilters") as string[];
                upper[name] = filters ?? (object?)"(값 없음)";
                if (filters != null)
                    (name == "keyboard" ? facts.KeyboardUpper : facts.MouseUpper).AddRange(filters);
            }
            catch (Exception ex) { upper[name] = $"unavailable: {ex.GetType().Name}"; }
        }
        result["upperFilters"] = upper;

        // fltmc 미니필터 — 비관리자는 보통 거부되고, 그 사실을 기록한다
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
                // 헤더 줄 수가 가변적이라 내용으로 거른다: 구분선(---)과 제목 줄 제외
                var filters = stdout.Split('\n').Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("-") && !l.StartsWith("필터") && !l.StartsWith("Filter"))
                    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToList();
                facts.Minifilters.AddRange(filters);
                result["minifilters"] = new { status = "ok", filters };
            }
            else
            {
                result["minifilters"] = new { status = "unavailable", exitCode = p.ExitCode, reason = (stdout + stderr).Trim().Replace("\r\n", " ") };
            }
        }
        catch (Exception ex) { result["minifilters"] = new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }; }

        // 커널 드라이버 (Win32_SystemDriver)
        using var searcher = new ManagementObjectSearcher("SELECT Name, State, StartMode, PathName FROM Win32_SystemDriver");
        var drivers = searcher.Get().Cast<ManagementObject>()
            .Select(d => new
            {
                Name = d["Name"] as string ?? "",
                State = d["State"] as string ?? "",
                PathName = (d["PathName"] as string ?? "").ToLowerInvariant(),
            }).ToList();
        var running = drivers.Where(d => d.State == "Running").ToList();
        var nonStandard = running
            .Where(d => d.PathName.Length > 0
                        && !d.PathName.Contains(@"\system32\drivers\")
                        && !d.PathName.Contains(@"\system32\driverstore\"))
            .Select(d => new { name = d.Name, path = d.PathName }).Take(50).ToList();
        foreach (var d in nonStandard)
            facts.NonStandardDrivers.Add((d.name, d.path));
        result["kernelDrivers"] = new { total = drivers.Count, running = running.Count, nonStandardPathRunning = nonStandard };
        return result;
    }

    private static object CaptureRecentInstalls(EngineFacts facts)
    {
        var cutoff = DateTime.Today.AddDays(-30);
        var entries = new List<(string Name, DateTime Date, string? Version, string Hive)>();
        int noDate = 0;

        void ScanKey(RegistryKey? root, string hive)
        {
            if (root == null) return;
            using (root)
            {
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                    var raw = k.GetValue("InstallDate") as string;
                    if (string.IsNullOrEmpty(raw) ||
                        !DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    { noDate++; continue; }
                    if (date >= cutoff)
                        entries.Add((name, date, k.GetValue("DisplayVersion") as string, hive));
                }
            }
        }

        ScanKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKLM64");
        ScanKey(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKLM32");
        ScanKey(Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"), "HKCU");

        foreach (var e in entries.OrderByDescending(x => x.Date))
            facts.RecentInstalls.Add((e.Name, e.Date.ToString("yyyy-MM-dd"), e.Version));

        return new
        {
            entries = entries.OrderByDescending(e => e.Date)
                .Select(e => new { name = e.Name, date = e.Date.ToString("yyyy-MM-dd"), version = e.Version, hive = e.Hive }).ToList(),
            entriesWithoutInstallDate = noDate,
        };
    }
}
