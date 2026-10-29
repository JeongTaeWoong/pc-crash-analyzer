using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BlackBox.Participant;

/// <summary>전경 창 정보(읽기만). 창 제목은 읽지 않는다 — 프로세스 이름과 창 종류만.</summary>
public static class Desktop
{
    private static readonly int OwnPid = Environment.ProcessId;

    /// <summary>작업 표시줄·알림 영역·시작 메뉴 — 트레이 아이콘을 누르면 전경이 이쪽으로 가므로 "문제 프로그램" 후보에서 뺀다.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "Progman", "WorkerW",
    };

    public sealed record Foreground(int Pid, string? ProcessName, bool Fullscreen);

    /// <summary>지금 전경 창(이름·전체 화면 여부 포함). 우리 창·셸 창이면 null. 단축키 순간에만 부른다.</summary>
    public static Foreground? Current()
    {
        var (hwnd, pid) = ExternalForeground();
        return pid > 0 ? new Foreground(pid, ProcessName(pid), IsFullscreen(hwnd)) : null;
    }

    /// <summary>가벼운 판: 전경 PID 만(우리 창·셸 창이면 0). 1.5초 주기 추적용 — 프로세스 이름 조회(전체 프로세스 열거)를 하지 않는다.</summary>
    public static int CurrentPid() => ExternalForeground().Pid;

    private static (IntPtr Hwnd, int Pid) ExternalForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (IntPtr.Zero, 0);
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == OwnPid) return (IntPtr.Zero, 0);
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            if (ShellClasses.Contains(cls.ToString())) return (IntPtr.Zero, 0);
            return (hwnd, (int)pid);
        }
        catch { return (IntPtr.Zero, 0); }
    }

    public static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch { return null; }
    }

    /// <summary>창이 모니터 전체를 덮는가(전체 화면 게임·영상). 이때는 질문 창을 띄우지 않는다 — 게임이 최소화되거나 멈출 수 있다.</summary>
    public static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>
    /// 창이 보이는 프로그램 목록(증상 대화상자의 "문제 프로그램" 선택지). 프로세스 이름과 파일 설명만 쓴다 — 창 제목은 읽지 않는다.
    /// EnumWindows 한 번으로 PID 를 모은다(Process.MainWindowHandle 을 수백 번 묻는 것보다 훨씬 싸다 — ProcessListSource 와 같은 이유).
    /// </summary>
    public static List<(string ProcessName, string Label)> WindowedApps()
    {
        var pids = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != 0 && pid != OwnPid) pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var name = p.ProcessName + ".exe";
                if (result.ContainsKey(name)) continue;
                string label = name;
                try
                {
                    var desc = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && !desc.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                        label = $"{desc.Trim()} ({name})";
                }
                catch { /* 권한 없는 프로세스 — 이름만 */ }
                result[name] = label;
            }
            catch { /* 그새 종료 */ }
        }
        return result.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int cbSize; public Rect rcMonitor; public Rect rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);
}

/// <summary>설치 위치 점검.</summary>
public static class Installation
{
    /// <summary>zip 을 풀지 않고 탐색기 압축 보기에서 바로 실행하면 임시 폴더에서 돈다 — 곧 지워지므로 자동 실행이 깨진다.</summary>
    public static bool RunningFromTemp()
    {
        try
        {
            var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
            var temp = Path.GetFullPath(Path.GetTempPath());
            return baseDir.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PcBlackBox.exe");
}
