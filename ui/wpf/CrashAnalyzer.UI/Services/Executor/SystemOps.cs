using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace CrashAnalyzer.UI.Services.Executor;

// 실행기가 Windows 를 실제로 읽고 바꾸는 부분 — 2026-11-16 추가
//
// 바꾸는 방법은 모두 Windows 기본 도구다: sc.exe(서비스), netsh(방화벽), PowerShell Defender 명령(Microsoft Defender),
// 레지스트리 읽기(서비스 시작 유형), 프로세스 종료·시작. 다른 회사 백신은 제품마다 방법이 달라 자동 실행하지 않는다.
//
// DryRunSystemOps: "시험 실행" — 읽기는 실제로 하고, 바꾸는 명령은 실행하지 않고 기록만 남긴다. (기본값)

public sealed class ExecException : Exception
{
    public ExecException(string message) : base(message) { }
}

public sealed record ProcInfo(int Pid, string Name, string Path);

public interface ISystemOps
{
    bool IsDryRun { get; }
    bool IsElevated { get; }

    List<ProcInfo> FindProcesses(string name);
    void KillProcess(int pid);
    void StartProgram(string path);

    /// <summary>서비스 시작 유형 (0 boot · 1 system · 2 auto · 3 demand · 4 disabled). 서비스가 없으면 null.</summary>
    int? GetServiceStartType(string service);
    void SetServiceStartType(string service, int startType);
    bool IsServiceRunning(string service);
    void StopService(string service);
    void StartService(string service);

    bool FirewallRuleExists(string ruleName);
    void AddFirewallAllow(string ruleName, string programPath);
    void DeleteFirewallRule(string ruleName);

    bool DefenderHasProcessExclusion(string process);
    void DefenderAddProcessExclusion(string process);
    void DefenderRemoveProcessExclusion(string process);
    bool DefenderRealtimeEnabled();
    void DefenderSetRealtime(bool enabled);
}

/// <summary>실제 Windows 조작</summary>
public sealed class WindowsSystemOps : ISystemOps
{
    public bool IsDryRun => false;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Windows 기본 도구 실행. (종료 코드, 출력) — 콘솔 창은 띄우지 않는다.</summary>
    public static (int Exit, string Output) Run(string file, string args, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new ExecException(file + " 를 실행하지 못했습니다");
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (Exception) { }
            throw new ExecException(file + " 응답 시간 초과");
        }
        return (p.ExitCode, output + err.Result);
    }

    private static string Sys(string exe) => System.IO.Path.Combine(Environment.SystemDirectory, exe);

    private static (int Exit, string Output) PowerShell(string command)
    {
        // 한 줄 명령을 Base64 로 넘겨서 따옴표·한글이 깨지지 않게 한다
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop'; " + command));
        return Run(Sys(@"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    // ─── 프로세스 ───

    public List<ProcInfo> FindProcesses(string name)
    {
        var list = new List<ProcInfo>();
        Process[] found = Process.GetProcessesByName(name);
        for (int i = 0; i < found.Length; i++)
        {
            string path = "";
            try { path = found[i].MainModule?.FileName ?? ""; } catch (Exception) { /* 권한·32비트 차이로 못 읽을 수 있음 */ }
            list.Add(new ProcInfo(found[i].Id, found[i].ProcessName, path));
            found[i].Dispose();
        }
        return list;
    }

    public void KillProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            p.WaitForExit(10_000);
        }
        catch (ArgumentException)
        {
            // 이미 끝난 프로세스
        }
        catch (Exception ex)
        {
            throw new ExecException("프로세스를 끄지 못했습니다: " + ex.Message);
        }
    }

    public void StartProgram(string path)
    {
        if (!System.IO.File.Exists(path)) throw new ExecException("프로그램 파일이 없습니다: " + path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    // ─── 서비스 ───

    public int? GetServiceStartType(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
        if (key?.GetValue("Start") is int v) return v;
        return null;
    }

    private static string StartWord(int startType) => startType switch
    {
        0 => "boot",
        1 => "system",
        2 => "auto",
        3 => "demand",
        4 => "disabled",
        _ => throw new ExecException("알 수 없는 시작 유형: " + startType),
    };

    public void SetServiceStartType(string service, int startType)
    {
        var r = Run(Sys("sc.exe"), "config \"" + service + "\" start= " + StartWord(startType));
        if (r.Exit != 0) throw new ExecException("서비스 시작 유형을 바꾸지 못했습니다 (sc 종료 코드 " + r.Exit + "): " + r.Output.Trim());
        if (GetServiceStartType(service) != startType) throw new ExecException("서비스 시작 유형이 바뀌지 않았습니다 (보안 프로그램이 막았을 수 있음)");
    }

    public bool IsServiceRunning(string service)
    {
        var r = Run(Sys("sc.exe"), "query \"" + service + "\"");
        // 상태 값 글자(RUNNING/STOPPED)는 한국어 Windows 에서도 영어로 나온다
        return r.Exit == 0 && (r.Output.Contains("RUNNING") || r.Output.Contains("START_PENDING"));
    }

    private void WaitService(string service, bool running)
    {
        for (int i = 0; i < 30; i++)
        {
            if (IsServiceRunning(service) == running) return;
            Thread.Sleep(500);
        }
        throw new ExecException("서비스가 " + (running ? "시작" : "중지") + "되지 않았습니다 (15초 대기)");
    }

    public void StopService(string service)
    {
        var r = Run(Sys("sc.exe"), "stop \"" + service + "\"");
        if (r.Exit != 0 && r.Exit != 1062) // 1062 = 이미 중지됨
            throw new ExecException("서비스를 중지하지 못했습니다 (sc 종료 코드 " + r.Exit + "): " + r.Output.Trim());
        WaitService(service, false);
    }

    public void StartService(string service)
    {
        var r = Run(Sys("sc.exe"), "start \"" + service + "\"");
        if (r.Exit != 0 && r.Exit != 1056) // 1056 = 이미 실행 중
            throw new ExecException("서비스를 시작하지 못했습니다 (sc 종료 코드 " + r.Exit + "): " + r.Output.Trim());
        WaitService(service, true);
    }

    // ─── 방화벽 ───

    public bool FirewallRuleExists(string ruleName) =>
        Run(Sys("netsh.exe"), "advfirewall firewall show rule name=\"" + ruleName + "\"").Exit == 0;

    public void AddFirewallAllow(string ruleName, string programPath)
    {
        string[] dirs = { "in", "out" };
        for (int i = 0; i < dirs.Length; i++)
        {
            var r = Run(Sys("netsh.exe"), "advfirewall firewall add rule name=\"" + ruleName + "\" dir=" + dirs[i] +
                                           " action=allow enable=yes program=\"" + programPath + "\"");
            if (r.Exit != 0) throw new ExecException("방화벽 허용 규칙을 만들지 못했습니다: " + r.Output.Trim());
        }
    }

    public void DeleteFirewallRule(string ruleName)
    {
        var r = Run(Sys("netsh.exe"), "advfirewall firewall delete rule name=\"" + ruleName + "\"");
        if (r.Exit != 0 && FirewallRuleExists(ruleName))
            throw new ExecException("방화벽 규칙을 지우지 못했습니다: " + r.Output.Trim());
    }

    // ─── Microsoft Defender ───

    public bool DefenderHasProcessExclusion(string process)
    {
        var r = PowerShell("if ((Get-MpPreference).ExclusionProcess -contains " + Quote(process) + ") { 'YES' } else { 'NO' }");
        if (r.Exit != 0) throw new ExecException("Defender 예외 목록을 읽지 못했습니다: " + r.Output.Trim());
        return r.Output.Contains("YES");
    }

    public void DefenderAddProcessExclusion(string process)
    {
        var r = PowerShell("Add-MpPreference -ExclusionProcess " + Quote(process));
        if (r.Exit != 0 || !DefenderHasProcessExclusion(process))
            throw new ExecException("Defender 예외를 추가하지 못했습니다: " + r.Output.Trim());
    }

    public void DefenderRemoveProcessExclusion(string process)
    {
        var r = PowerShell("Remove-MpPreference -ExclusionProcess " + Quote(process));
        if (r.Exit != 0 || DefenderHasProcessExclusion(process))
            throw new ExecException("Defender 예외를 지우지 못했습니다: " + r.Output.Trim());
    }

    public bool DefenderRealtimeEnabled()
    {
        var r = PowerShell("if ((Get-MpComputerStatus).RealTimeProtectionEnabled) { 'YES' } else { 'NO' }");
        if (r.Exit != 0) throw new ExecException("Defender 실시간 보호 상태를 읽지 못했습니다: " + r.Output.Trim());
        return r.Output.Contains("YES");
    }

    public void DefenderSetRealtime(bool enabled)
    {
        var r = PowerShell("Set-MpPreference -DisableRealtimeMonitoring $" + (enabled ? "false" : "true"));
        Thread.Sleep(1000);
        if (r.Exit != 0 || DefenderRealtimeEnabled() != enabled)
            throw new ExecException("Defender 실시간 보호를 바꾸지 못했습니다. '변조 보호(Tamper Protection)'가 켜져 있으면 프로그램이 바꿀 수 없어요." +
                                    (r.Output.Trim().Length > 0 ? " (" + r.Output.Trim() + ")" : ""));
    }
}

/// <summary>
/// 시험 실행: 읽기는 실제로 하고(못 읽으면 기본값), 바꾸는 명령은 실행하지 않고 Log 에 "이렇게 바꿀 것"만 남긴다.
/// 테스트 전 시연에서도 실행·확인·되돌리기 흐름이 그대로 보인다.
/// </summary>
public sealed class DryRunSystemOps : ISystemOps
{
    private readonly ISystemOps _real;
    public List<string> Log { get; } = new();

    public DryRunSystemOps(ISystemOps real) => _real = real;

    public bool IsDryRun => true;
    public bool IsElevated => true; // 시험 실행은 아무것도 바꾸지 않으므로 권한 확인을 건너뛴다

    private T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch (Exception) { return fallback; }
    }

    private void Would(string text) => Log.Add("[시험 실행 · 실제로 바꾸지 않음] " + text);

    public List<ProcInfo> FindProcesses(string name) => Read(() => _real.FindProcesses(name), new List<ProcInfo>());
    public void KillProcess(int pid) => Would("프로세스 " + pid + " 종료");
    public void StartProgram(string path) => Would("프로그램 다시 실행: " + path);

    public int? GetServiceStartType(string service) => Read(() => _real.GetServiceStartType(service), (int?)null);
    public void SetServiceStartType(string service, int startType) => Would("sc config " + service + " start=" + startType);
    public bool IsServiceRunning(string service) => Read(() => _real.IsServiceRunning(service), false);
    public void StopService(string service) => Would("sc stop " + service);
    public void StartService(string service) => Would("sc start " + service);

    public bool FirewallRuleExists(string ruleName) => Read(() => _real.FirewallRuleExists(ruleName), false);
    public void AddFirewallAllow(string ruleName, string programPath) => Would("방화벽 허용 규칙 추가: " + ruleName + " (" + programPath + ")");
    public void DeleteFirewallRule(string ruleName) => Would("방화벽 규칙 삭제: " + ruleName);

    public bool DefenderHasProcessExclusion(string process) => Read(() => _real.DefenderHasProcessExclusion(process), false);
    public void DefenderAddProcessExclusion(string process) => Would("Defender 예외 추가: " + process);
    public void DefenderRemoveProcessExclusion(string process) => Would("Defender 예외 삭제: " + process);
    public bool DefenderRealtimeEnabled() => Read(() => _real.DefenderRealtimeEnabled(), true);
    public void DefenderSetRealtime(bool enabled) => Would("Defender 실시간 보호 " + (enabled ? "켜기" : "끄기"));
}
