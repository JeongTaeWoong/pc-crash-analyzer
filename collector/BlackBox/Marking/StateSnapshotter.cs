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
        };
        snapshot["captureDurationMs"] = sw.ElapsedMilliseconds;
        return new SnapshotResult(snapshot, facts, fgPid);
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
