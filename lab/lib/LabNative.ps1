# Shared native helpers for lab scripts. Dot-source this file: . "$PSScriptRoot\..\lib\LabNative.ps1"
# Reads system state; the only inputs it sends are the ones named Type*/Send*/Jiggle*, and callers send those only to
# windows the lab itself started (see Test-LabForeground).
# Verified 2026-11-10 on Windows 26H2 x64 (collector/NOTES.md). Test-LabSnapshotLayout must pass before Snapshot() is trusted.
if (-not ('LabNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class LabProcRow { public int Pid; public string Name; public long CpuTicks; public long PrivateBytes; public long WorkingSet; public long ReadBytes; public long WriteBytes; }

public static class LabNative
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    // ---- processes -------------------------------------------------------------------------------------------------
    // SystemProcessInformation (class 5): every process in one call, no handles opened, works for SYSTEM-owned processes
    // without admin. The structure is only partly documented, so callers check it first (Test-LabSnapshotLayout).
    public static List<LabProcRow> Snapshot()
    {
        int len = 1 << 20;
        IntPtr buf = IntPtr.Zero;
        try
        {
            while (true)
            {
                buf = Marshal.AllocHGlobal(len);
                int ret;
                int st = NtQuerySystemInformation(5, buf, len, out ret);
                if (st == 0) break;
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if ((uint)st == 0xC0000004u) { len = Math.Max(ret + 65536, len * 2); continue; }
                throw new InvalidOperationException("NtQuerySystemInformation 0x" + st.ToString("X8"));
            }
            var rows = new List<LabProcRow>();
            long off = 0;
            while (true)
            {
                IntPtr p = new IntPtr(buf.ToInt64() + off);
                int next = Marshal.ReadInt32(p, 0);
                var r = new LabProcRow();
                r.CpuTicks = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);   // UserTime + KernelTime, 100 ns units
                int nameBytes = (ushort)Marshal.ReadInt16(p, 56);
                IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                r.Name = (nameBytes > 0 && namePtr != IntPtr.Zero) ? Marshal.PtrToStringUni(namePtr, nameBytes / 2) : "Idle";
                r.Pid = (int)Marshal.ReadInt64(p, 80);
                r.WorkingSet = Marshal.ReadInt64(p, 144);
                r.PrivateBytes = Marshal.ReadInt64(p, 200);
                r.ReadBytes = Marshal.ReadInt64(p, 232);
                r.WriteBytes = Marshal.ReadInt64(p, 240);
                rows.Add(r);
                if (next == 0) break;
                off += next;
            }
            return rows;
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    // ---- windows ---------------------------------------------------------------------------------------------------
    static IntPtr Send(IntPtr hwnd, uint msg, long w, long l, uint timeoutMs, out bool ok)
    {
        IntPtr res;
        IntPtr r = SendMessageTimeout(hwnd, msg, new IntPtr(w), new IntPtr(l), 0x0002, timeoutMs, out res);
        ok = r != IntPtr.Zero;
        return res;
    }

    // WM_NULL round trip. ok=false: no answer within timeoutMs, or the window was already flagged as hung.
    public static double ProbeMs(IntPtr hwnd, uint timeoutMs, out bool ok)
    {
        var sw = Stopwatch.StartNew();
        Send(hwnd, 0, 0, 0, timeoutMs, out ok);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    // Largest visible titled top-level window of a process (Process.MainWindowHandle misses some apps while they start).
    public static IntPtr MainWindowOf(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && IsWindowVisible(h)) { var sb = new StringBuilder(8); if (GetWindowText(h, sb, 8) > 0) { found = h; return false; } }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr hwnd) { var sb = new StringBuilder(512); GetWindowText(hwnd, sb, 512); return sb.ToString(); }

    // Titles of every visible top-level window of a process (a browser shows prompts as separate windows).
    public static string[] WindowTitlesOf(int pid)
    {
        var list = new List<string>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == (uint)pid && IsWindowVisible(h)) { var sb = new StringBuilder(512); if (GetWindowText(h, sb, 512) > 0) list.Add(sb.ToString()); }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
    public static IntPtr ChildByClass(IntPtr parent, string cls) { return FindWindowEx(parent, IntPtr.Zero, cls, null); }
    public static int ForegroundPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return (int)p; }
    // Windows refuses SetForegroundWindow from a process that is not in the foreground unless that process just
    // received input; a tap of Alt counts. Used only on windows the lab started.
    public static bool BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, 9);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;
        Key(0x12, false, false); Key(0x12, true, false);
        SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        return GetForegroundWindow() == hwnd;
    }
    public static void CloseWindow(IntPtr hwnd) { PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero); }
    public static bool Lock() { return LockWorkStation(); }
    public static void ParkMouse() { SetCursorPos(0, 0); }

    // Scintilla (the edit control inside Notepad++): read-only queries plus "mark as saved".
    public static long SciLength(IntPtr sci, out bool ok) { return Send(sci, 2006, 0, 0, 2000, out ok).ToInt64(); }
    public static bool SciModified(IntPtr sci, out bool ok) { return Send(sci, 2159, 0, 0, 2000, out ok).ToInt64() != 0; }

    // ---- background UI probe -----------------------------------------------------------------------------------------
    static Thread probeThread; static volatile bool probeStop; static List<double> probeMs; static int probeMisses;
    public static void StartProbe(IntPtr hwnd, int intervalMs)
    {
        StopProbe();
        probeMs = new List<double>(); probeMisses = 0; probeStop = false;
        probeThread = new Thread(delegate ()
        {
            while (!probeStop)
            {
                bool ok; double ms = ProbeMs(hwnd, 2000, out ok);
                if (ok) { lock (probeMs) probeMs.Add(ms); } else Interlocked.Increment(ref probeMisses);
                Thread.Sleep(intervalMs);
            }
        });
        probeThread.IsBackground = true; probeThread.Start();
    }
    public static double[] StopProbe()
    {
        if (probeThread == null) return new double[0];
        probeStop = true; probeThread.Join(3000); probeThread = null;
        lock (probeMs) return probeMs.ToArray();
    }
    public static int ProbeMisses() { return probeMisses; }

    // ---- input -------------------------------------------------------------------------------------------------------
    public static uint LastInputTick()
    {
        var i = new LASTINPUTINFO(); i.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref i)) throw new InvalidOperationException("GetLastInputInfo failed");
        return i.dwTime;
    }

    // Relative mouse move by +1 then -1 px: scripted input that types nothing into whatever window has focus.
    public static uint JiggleMouse()
    {
        var a = new INPUT[2];
        a[0].type = 0; a[0].mi.dx = 1; a[0].mi.dwFlags = 0x0001;
        a[1].type = 0; a[1].mi.dx = -1; a[1].mi.dwFlags = 0x0001;
        return SendInput(2, a, 40);
    }

    static void Key(ushort vk, bool up, bool extended)
    {
        var a = new INPUT[1];
        a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.dwFlags = (up ? 0x0002u : 0u) | (extended ? 0x0001u : 0u);
        SendInput(1, a, 40);
    }
    public static void SendCtrlS() { Key(0x11, false, false); Key(0x53, false, false); Key(0x53, true, false); Key(0x11, true, false); }
    public static void SendCtrlEnd() { Key(0x11, false, false); Key(0x23, false, true); Key(0x23, true, true); Key(0x11, true, false); }

    // Types `count` characters into the foreground window one at a time and, for each, measures the time until the
    // Scintilla document grows. Stops early (returns what it has) if `pid` loses the foreground or a character times out.
    public static double[] TypeAndMeasure(IntPtr sci, int pid, int count, int timeoutMs, int gapMs)
    {
        var res = new List<double>();
        var a = new INPUT[2];
        a[0].type = 1; a[0].ki.dwFlags = 0x0004;            // KEYEVENTF_UNICODE down
        a[1].type = 1; a[1].ki.dwFlags = 0x0004 | 0x0002;   // up
        for (int i = 0; i < count; i++)
        {
            if (ForegroundPid() != pid) break;
            bool ok; long before = SciLength(sci, out ok);
            if (!ok) break;
            ushort ch = (ushort)('a' + (i % 26));
            a[0].ki.wScan = ch; a[1].ki.wScan = ch;
            var sw = Stopwatch.StartNew();
            SendInput(2, a, 40);
            bool grown = false;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                long now = SciLength(sci, out ok);
                if (ok && now > before) { grown = true; break; }
                Thread.Sleep(0);
            }
            sw.Stop();
            if (!grown) break;
            res.Add(sw.Elapsed.TotalMilliseconds);
            if (gapMs > 0) Thread.Sleep(gapMs);
        }
        return res.ToArray();
    }
}
'@
}

# True when Snapshot() agrees with the documented Process API for this process and for the process list.
function Test-LabSnapshotLayout {
    $me = [Diagnostics.Process]::GetCurrentProcess()
    $snap = [LabNative]::Snapshot()
    $mine = $snap | Where-Object { $_.Pid -eq $PID } | Select-Object -First 1
    if (-not $mine) { return $false }
    $me.Refresh()
    $nameOk = $mine.Name -ieq ($me.ProcessName + '.exe')
    $cpuOk = [math]::Abs(($mine.CpuTicks / 1e4) - $me.TotalProcessorTime.TotalMilliseconds) -lt 500
    $memOk = [math]::Abs($mine.PrivateBytes - $me.PrivateMemorySize64) -lt 64MB
    $pids = @(Get-Process | ForEach-Object Id)
    $missing = @($snap | Where-Object { $_.Pid -ne 0 -and $pids -notcontains $_.Pid }).Count
    return ($nameOk -and $cpuOk -and $memOk -and $missing -le 5)
}

# Per-process CPU (% of the whole machine) and memory over an interval, for processes whose name matches -Pattern.
function Measure-LabProcesses {
    param([Parameter(Mandatory = $true)][string]$Pattern, [int]$Seconds = 30)
    $cores = [Environment]::ProcessorCount
    $a = @{}; foreach ($r in [LabNative]::Snapshot()) { $a[$r.Pid] = $r }
    $sw = [Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Seconds $Seconds; $secs = $sw.Elapsed.TotalSeconds
    foreach ($r in [LabNative]::Snapshot()) {
        if ($r.Name -notmatch $Pattern) { continue }
        $prev = $a[$r.Pid]
        $cpu = if ($prev -and $prev.Name -eq $r.Name) { ($r.CpuTicks - $prev.CpuTicks) / 1e7 / $secs / $cores * 100 } else { $null }
        $io = if ($prev -and $prev.Name -eq $r.Name) { (($r.ReadBytes + $r.WriteBytes) - ($prev.ReadBytes + $prev.WriteBytes)) / 1MB / $secs } else { $null }
        [pscustomobject]@{ name = $r.Name; pid = $r.Pid; cpuPct = if ($null -ne $cpu) { [math]::Round($cpu, 3) } else { $null }; cpuTotalMin = [math]::Round($r.CpuTicks / 1e7 / 60, 1); privateMB = [math]::Round($r.PrivateBytes / 1MB, 1); workingSetMB = [math]::Round($r.WorkingSet / 1MB, 1); ioMBps = if ($null -ne $io) { [math]::Round($io, 3) } else { $null } }
    }
}

# Summary of UI probe round trips (ms): what a user would feel as "the window stopped answering".
function Get-LabProbeSummary([double[]]$ms, [int]$misses) {
    $s = @($ms | Sort-Object)
    if (-not $s.Count) { return [ordered]@{ n = 0; misses = $misses } }
    return [ordered]@{
        n = $s.Count; misses = $misses
        p50 = [math]::Round($s[[int][math]::Floor(($s.Count - 1) * 0.5)], 3)
        p95 = [math]::Round($s[[int][math]::Floor(($s.Count - 1) * 0.95)], 3)
        max = [math]::Round($s[-1], 1)
        stalls100 = @($s | Where-Object { $_ -ge 100 }).Count
        stalls200 = @($s | Where-Object { $_ -ge 200 }).Count
    }
}
