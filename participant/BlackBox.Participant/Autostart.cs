using System.Diagnostics;
using System.Security;
using System.Text;

namespace BlackBox.Participant;

/// <summary>
/// "Windows 로그인 시 자동 실행" 켜기·끄기·점검. 참여자가 동의 화면·트레이 메뉴에서 켰을 때만 만든다.
/// 관리자로 돌면 작업 스케줄러 + Program Files 복사본(ElevatedAutostart), 아니면 시작 프로그램 폴더 바로가기(StartupShortcut).
/// 관리자 프로세스는 사용자의 시작 프로그램 폴더를 건드리지 않는다 — 같은 사용자의 일반 권한 프로그램이 그 폴더에 링크를 심어
/// 관리자 권한 삭제를 엉뚱한 파일로 돌릴 수 있고, 그 폴더의 파일을 "참여자가 켜 둔 것"으로 믿으면 동의 없이 켜진다(10/30 보안 검토).
/// 시험(--startup-dir, 명령줄로만 줌)은 예외.
/// </summary>
public static class Autostart
{
    private static ElevatedAutostart Elevated(AppOptions o) => new(o.StartupDir);

    /// <summary>지금 켜져 있는가. 관리자면 schtasks 를 부르므로(약 0.1초) 메뉴 갱신마다 부르지 말고 상태 값을 쓴다.</summary>
    public static bool Exists(AppOptions o) =>
        Elevation.IsElevated ? Elevated(o).Exists() : StartupShortcut.Exists(o.StartupDir);

    public static void Enable(AppOptions o)
    {
        if (!Elevation.IsElevated)
        {
            StartupShortcut.Create(Installation.ExePath, o.StartupDir, o.ShortcutDataDir);
            return;
        }
        Elevated(o).Enable(o.ShortcutDataDir);
    }

    public static void Disable(AppOptions o)
    {
        if (!Elevation.IsElevated)
        {
            try { StartupShortcut.Remove(o.StartupDir); } catch { }
            return;
        }
        Elevated(o).Disable();
        if (o.StartupDir != null) try { StartupShortcut.Remove(o.StartupDir); } catch { } // 시험 폴더만
    }

    /// <summary>
    /// 시작할 때: 켜져 있으면 경로·인자·설치본을 지금 실행 파일에 맞춘다. 참여자가 끈 것(지운 것)은 되살리지 않는다. 반환: 켜져 있는가.
    /// 관리자 실행은 사용자가 쓸 수 있는 곳의 흔적이 아니라 보호된 기록 폴더의 participant.json 에 남은 참여자 선택(wanted)만 믿는다.
    /// </summary>
    public static bool Repair(AppOptions o, bool wanted)
    {
        if (!Elevation.IsElevated) return StartupShortcut.RepairIfMoved(Installation.ExePath, o.StartupDir, o.ShortcutDataDir);
        if (!wanted) return false;
        var t = Elevated(o);
        if (!t.Exists()) return false; // 참여자가 작업 스케줄러에서 직접 지움 — 되살리지 않는다
        t.Enable(o.ShortcutDataDir);   // 설치본·인자를 지금 실행 파일에 맞춘다
        return true;
    }
}

/// <summary>
/// 관리자 권한으로 로그인 때 켜기(0.8). 시작 프로그램 폴더 바로가기로는 관리자 권한 프로그램이 로그인 때 뜨는지 확실하지 않아(미확인) 작업 스케줄러를 쓴다.
/// 작업이 가리키는 exe 는 일반 권한으로 바꿀 수 없는 Program Files\PcBlackBox 의 복사본이다 — 다운로드 폴더의 exe 를 가리키면
/// 악성 프로그램이 그 파일을 바꿔치기해 로그인 때 관리자 권한을 얻는다. 작업 XML 도 같은 이유로 설치 폴더 안에서 만들어 넘긴다.
/// 등록·삭제는 Windows 기본 도구 schtasks.exe 로 한다. 시험(--startup-dir)은 작업 이름에 -TEST, 설치 폴더는 시험 폴더 안.
/// </summary>
public sealed class ElevatedAutostart
{
    /// <summary>설치 폴더로 보일 최대 크기 — zip 을 바탕화면에 그냥 흩어 풀었을 때 바탕화면 전체를 복사하지 않게.</summary>
    private const int MaxFiles = 800;
    private const long MaxBytes = 500L * 1024 * 1024;

    private static string? _pendingRemoval;

    public string TaskName { get; }
    public string InstallDir { get; }

    public ElevatedAutostart(string? testDir)
    {
        TaskName = $@"PcBlackBox\Autostart-{Elevation.UserSid}" + (testDir != null ? "-TEST" : "");
        InstallDir = testDir != null
            ? Path.Combine(testDir, "install")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcBlackBox");
    }

    public string InstalledExe => Path.Combine(InstallDir, Path.GetFileName(Installation.ExePath));

    public bool Exists() => Schtasks("/Query", "/TN", TaskName).ExitCode == 0;

    public void Enable(string? dataDir)
    {
        Install();
        var xmlPath = Path.Combine(InstallDir, "autostart-task.xml");
        File.WriteAllText(xmlPath, TaskXml(InstalledExe, StartupShortcut.ArgsFor(dataDir)), Encoding.Unicode);
        try
        {
            var r = Schtasks("/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            if (r.ExitCode != 0) throw new InvalidOperationException($"작업 스케줄러 등록 실패({r.ExitCode}) {r.Output.Trim()}");
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public void Disable()
    {
        if (Exists())
        {
            var r = Schtasks("/Delete", "/TN", TaskName, "/F");
            if (r.ExitCode != 0) OpsLog.Write($"[autostart] 작업 삭제 실패({r.ExitCode}) {r.Output.Trim()}");
        }
        Uninstall();
    }

    /// <summary>지금 실행 파일 폴더를 설치 폴더로 복사한다. 이미 같은 버전이 있거나 설치본에서 도는 중이면 하지 않는다.</summary>
    public void Install()
    {
        var src = Path.GetFullPath(AppContext.BaseDirectory);
        var dst = Path.GetFullPath(InstallDir);
        if (SameDir(src, dst)) return;
        if (File.Exists(InstalledExe) && Version(InstalledExe) == Version(Installation.ExePath)) return;

        // 원본 폴더는 사용자가 쓸 수 있는 곳이다 — 그 안의 링크(정션·심볼릭 링크)를 따라가면 관리자 권한으로 남의 파일을
        // 누구나 읽을 수 있는 Program Files 로 복사하게 된다. 링크는 건너뛴다(배포 zip 에는 링크가 없다).
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var files = Directory.EnumerateFiles(src, "*", walk).Take(MaxFiles + 1).ToList();
        if (files.Count > MaxFiles || files.Sum(f => new FileInfo(f).Length) > MaxBytes || !File.Exists(Path.Combine(src, "study.json")))
            throw new InvalidOperationException("프로그램 폴더가 zip 을 푼 폴더 그대로가 아니에요. zip 을 새 폴더에 다시 풀어 주세요.");

        Directory.CreateDirectory(dst);
        foreach (var file in files)
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        OpsLog.Write($"[autostart] 설치본 복사 {files.Count}개 → {dst}");
    }

    /// <summary>설치 폴더를 지운다. 지금 그 설치본에서 도는 중이면 프로그램이 끝난 뒤 지우도록 남겨 둔다(RunPendingRemoval).</summary>
    public void Uninstall()
    {
        var dst = Path.GetFullPath(InstallDir);
        if (!Directory.Exists(dst)) return;
        // 우리 설치 폴더라는 표시가 없으면 손대지 않는다
        if (!File.Exists(Path.Combine(dst, "PcBlackBox.exe")) || !File.Exists(Path.Combine(dst, "study.json"))) return;
        if (SameDir(AppContext.BaseDirectory, dst)) { _pendingRemoval = dst; return; }
        Directory.Delete(dst, recursive: true);
        OpsLog.Write($"[autostart] 설치본 삭제 {dst}");
    }

    /// <summary>Main 이 끝나기 직전에 부른다: 실행 중이던 설치본 폴더를 프로그램이 끝난 뒤 지운다(2초 간격으로 최대 10분 재시도).</summary>
    public static void RunPendingRemoval()
    {
        var dst = _pendingRemoval;
        if (dst == null) return;
        _pendingRemoval = null;
        try
        {
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var script = $"for /l %i in (1,1,300) do (ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dst}\" 2>nul & if not exist \"{dst}\" exit /b 0)";
            Process.Start(new ProcessStartInfo(cmd, $"/d /c {script}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Exception ex) { OpsLog.Write($"[autostart] 설치본 삭제 예약 실패: {ex.Message}"); }
    }

    private static string Version(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? ""; } catch { return ""; }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static (int ExitCode, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(); } catch { }
            return (-1, "시간 초과");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>
    /// 로그인 트리거(이 사용자, 30초 지연) · 가장 높은 권한 · 배터리여도 실행 · 실행 시간 제한 없음(기본 72시간이면 사흘 뒤 꺼진다) · 이미 돌면 새로 안 띄움.
    /// </summary>
    private static string TaskXml(string exe, string args)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        var sid = E(Elevation.UserSid);
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>PC 블랙박스</Author>
    <Description>PC 블랙박스(중앙대 캡스톤 연구) 로그인 시 실행. 트레이 메뉴의 자동 실행 끄기나 참여 그만두기로 지워집니다.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{sid}</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{sid}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"{E(exe)}"</Command>
      <Arguments>{E(args)}</Arguments>
      <WorkingDirectory>{E(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";
    }
}
