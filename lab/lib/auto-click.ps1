# Clicks the default "go on" buttons of ONE installer so that it can finish with nobody at the PC.
#   auto-click.ps1 -ProcessId <installer pid> [-TimeoutSec 600] [-LogFile <path>]
# Scope: only windows that belong to that process or to processes it started. It never touches other windows.
# What it presses: Next / Install / Agree / OK / Yes / Finish / Close style buttons, and "I accept" style options (once each).
# What it never presses: Cancel / No / Back / Remove / Repair. The word lists are in auto-click.words.json.
# How: classic Win32 child controls (button classes) found with EnumChildWindows and pressed with BM_CLICK. On this PC
# UI Automation reported such controls only as panes without patterns (11/10), so it is not used. Installers that draw
# their own buttons (web-style or Qt user interfaces) have no child controls and cannot be clicked this way: the caller's
# timeout then closes them and records that.
# Status 2026-11-10: checked against a three-page test wizard (lab\spikes\test-autoclick.ps1). NOT yet run against the
# real security-program installers; that can only happen in the real install step.
param([Parameter(Mandatory = $true)][int]$ProcessId, [int]$TimeoutSec = 600, [string]$LogFile)
$ErrorActionPreference = 'Stop'
if (-not ('LabClick' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class LabControl { public IntPtr Handle; public IntPtr Top; public string Class; public string Text; public string TopTitle; }
public static class LabClick
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    static string Text(IntPtr h) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
    static string Cls(IntPtr h) { var sb = new StringBuilder(128); GetClassName(h, sb, 128); return sb.ToString(); }

    // Visible, enabled, titled child controls of every visible top-level window owned by one of the given processes.
    public static List<LabControl> Controls(int[] pids)
    {
        var set = new HashSet<uint>(); foreach (int p in pids) set.Add((uint)p);
        var res = new List<LabControl>();
        EnumWindows(delegate (IntPtr top, IntPtr l)
        {
            uint pid; GetWindowThreadProcessId(top, out pid);
            if (!set.Contains(pid) || !IsWindowVisible(top)) return true;
            string title = Text(top);
            EnumChildWindows(top, delegate (IntPtr c, IntPtr l2)
            {
                if (IsWindowVisible(c) && IsWindowEnabled(c)) { string t = Text(c); if (t.Length > 0) res.Add(new LabControl { Handle = c, Top = top, Class = Cls(c), Text = t, TopTitle = title }); }
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return res;
    }
    public static void Click(IntPtr h) { PostMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }   // BM_CLICK, posted so a modal handler cannot block us
}
'@
}
# The word lists live in a UTF-8 data file: this script must stay ASCII-only (see the note inside the file).
$words = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $PSScriptRoot 'auto-click.words.json') | ConvertFrom-Json
$go = '(?i)^(' + (@($words.go) -join '|') + ')'
$never = '(?i)(' + (@($words.never) -join '|') + ')'
$acceptOption = '(?i)(' + (@($words.acceptOption) -join '|') + ')'
$buttonClass = '(?i)button|radio|check'
function Log([string]$s) { $line = "{0} {1}" -f (Get-Date).ToString('HH:mm:ss'), $s; Write-Output $line; if ($LogFile) { [IO.File]::AppendAllText($LogFile, $line + "`r`n", (New-Object Text.UTF8Encoding($false))) } }
function Clean([string]$n) { return (("$n" -replace '&', '') -replace '\s+', ' ').Trim() }
function Get-Tree([int]$root) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId)
    $set = New-Object System.Collections.Generic.HashSet[int]; [void]$set.Add($root); $grew = $true
    while ($grew) { $grew = $false; foreach ($p in $all) { if ($set.Contains([int]$p.ParentProcessId) -and $set.Add([int]$p.ProcessId)) { $grew = $true } } }
    return $set
}

$sw = [Diagnostics.Stopwatch]::StartNew(); $clicks = 0
$optionsDone = New-Object System.Collections.Generic.HashSet[long]      # "I accept" style controls are pressed once
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    $pids = Get-Tree $ProcessId
    if (-not @(Get-Process -Id @($pids) -ErrorAction SilentlyContinue).Count) { Log "installer and its children have exited ($clicks clicks)"; exit 0 }
    $controls = @([LabClick]::Controls([int[]]@($pids)) | Where-Object { $_.Class -match $buttonClass })
    $pressed = $false
    foreach ($c in $controls) {              # 1) accept options first, so that Next becomes available
        $name = Clean $c.Text
        if ($name -match $never -or $name -notmatch $acceptOption -or $optionsDone.Contains($c.Handle.ToInt64())) { continue }
        [LabClick]::Click($c.Handle); [void]$optionsDone.Add($c.Handle.ToInt64()); $clicks++; $pressed = $true
        Log "chose '$name' in '$(Clean $c.TopTitle)'"
    }
    if (-not $pressed) {
        foreach ($c in $controls) {          # 2) then one "go on" button per pass
            $name = Clean $c.Text
            if ($name -match $never -or $name -notmatch $go -or $optionsDone.Contains($c.Handle.ToInt64())) { continue }
            [LabClick]::Click($c.Handle); $clicks++
            Log "pressed '$name' in '$(Clean $c.TopTitle)'"
            break
        }
    }
    Start-Sleep -Milliseconds 1200
}
Log "timeout after $TimeoutSec s ($clicks clicks); installer still running"
exit 1
