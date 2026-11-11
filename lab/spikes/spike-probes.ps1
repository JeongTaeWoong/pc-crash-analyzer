# Spikes for docs/security-impact-design.md section 9 (items 1, 7, 8, 9). Read-only, non-admin, about 30 s.
#   1 sampler : three ways to read per-process CPU/memory; cost per sample, and whether SYSTEM-owned processes are readable
#   7 UI probe: WM_NULL round trip against a window that blocks its UI thread 300 ms every second
#   8 input   : does GetLastInputInfo move on scripted input, and is it quiet otherwise
#   9 events  : Defender scan and Windows Update install events readable without admin
# Side effects: one minimized window for ~9 s; the mouse pointer moves 1 px and back.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'lib\LabNative.ps1')    # the same native helpers the real runs use

$cores = [Environment]::ProcessorCount
function Ms([scriptblock]$b, [int]$n) { $t = @(); for ($i = 0; $i -lt $n; $i++) { $sw = [Diagnostics.Stopwatch]::StartNew(); & $b | Out-Null; $t += $sw.Elapsed.TotalMilliseconds }; return [math]::Round(($t | Measure-Object -Average).Average, 1) }

"===== 1. sampler ====="
$me = [Diagnostics.Process]::GetCurrentProcess()
$snap = [LabNative]::Snapshot()
$mine = $snap | Where-Object { $_.Pid -eq $PID }
$me.Refresh()
"layout check (this process): name '{0}' vs '{1}.exe' | cpu {2:N0} vs {3:N0} ms | private {4:N1} vs {5:N1} MB | working set {6:N1} vs {7:N1} MB" -f $mine.Name, $me.ProcessName, ($mine.CpuTicks / 1e4), $me.TotalProcessorTime.TotalMilliseconds, ($mine.PrivateBytes / 1MB), ($me.PrivateMemorySize64 / 1MB), ($mine.WorkingSet / 1MB), ($me.WorkingSet64 / 1MB)
$pidsNt = @($snap | ForEach-Object Pid | Where-Object { $_ -ne 0 })
$pidsGp = @(Get-Process | ForEach-Object Id | Where-Object { $_ -ne 0 })
"process count: Nt {0} vs Get-Process {1}; pids only in one list: {2}" -f $pidsNt.Count, $pidsGp.Count, @(Compare-Object $pidsNt $pidsGp).Count
$cat = New-Object System.Diagnostics.PerformanceCounterCategory 'Process'
"cost per sample (avg of 5): Nt snapshot {0} ms | PerformanceCounter ReadCategory {1} ms | CIM PerfProc {2} ms" -f (Ms { [LabNative]::Snapshot() } 5), (Ms { $cat.ReadCategory() } 5), (Ms { Get-CimInstance Win32_PerfFormattedData_PerfProc_Process } 5)
$a = @{}; foreach ($r in [LabNative]::Snapshot()) { $a[$r.Pid] = $r }
$sw = [Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Seconds 3; $secs = $sw.Elapsed.TotalSeconds
$top = foreach ($r in [LabNative]::Snapshot()) { if ($r.Pid -ne 0 -and $a.ContainsKey($r.Pid) -and $a[$r.Pid].Name -eq $r.Name) { [pscustomobject]@{ name = $r.Name; pid = $r.Pid; cpuPct = [math]::Round(($r.CpuTicks - $a[$r.Pid].CpuTicks) / 1e7 / $secs / $cores * 100, 2); privMB = [math]::Round($r.PrivateBytes / 1MB) } } }
"top CPU over 3 s (% of whole machine, $cores threads):"
$top | Sort-Object cpuPct -Descending | Select-Object -First 6 | Format-Table -AutoSize | Out-String | ForEach-Object { $_.TrimEnd() }
"SYSTEM-owned processes readable without admin (cpu ms, private MB):"
foreach ($n in 'MsMpEng.exe', 'PresentMon_x64.exe', 'services.exe', 'csrss.exe') { $x = $snap | Where-Object Name -eq $n | Select-Object -First 1; if ($x) { "  {0,-20} {1,12:N0} ms {2,8:N1} MB" -f $n, ($x.CpuTicks / 1e4), ($x.PrivateBytes / 1MB) } else { "  {0,-20} not running" -f $n } }

"===== 7. UI probe ====="
$target = @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object Windows.Forms.Form
$f.Text = 'lab-probe-target'; $f.Width = 260; $f.Height = 90; $f.WindowState = 'Minimized'
$t = New-Object Windows.Forms.Timer; $t.Interval = 1000; $t.add_Tick({ [Threading.Thread]::Sleep(300) }); $t.Start()
$c = New-Object Windows.Forms.Timer; $c.Interval = 9000; $c.add_Tick({ $f.Close() }); $c.Start()
[Windows.Forms.Application]::Run($f)
'@
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($target))
$tp = Start-Process powershell -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $enc -PassThru
try {
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 100; $tp.Refresh(); $h = $tp.MainWindowHandle }
    if ($h -eq [IntPtr]::Zero) { "target window not found: unavailable" }
    else {
        $me.Refresh(); $cpu0 = $me.TotalProcessorTime.TotalMilliseconds
        $d = New-Object System.Collections.Generic.List[double]; $fails = 0
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 6) { $ok = $false; $ms = [LabNative]::ProbeMs($h, 1000, [ref]$ok); if (-not $ok) { $fails++ }; $d.Add($ms); Start-Sleep -Milliseconds 100 }
        $me.Refresh(); $cpu1 = $me.TotalProcessorTime.TotalMilliseconds
        $s = @($d | Sort-Object)
        "probes {0} in 6 s | median {1:N2} ms | max {2:N0} ms | >=200 ms: {3} (target blocks 300 ms once per second, so about 5 to 6 expected) | no answer: {4}" -f $d.Count, $s[[int]($s.Count / 2)], $s[-1], @($d | Where-Object { $_ -ge 200 }).Count, $fails
        "probe loop CPU: {0:N0} ms over 6 s = {1:N2}% of one thread" -f ($cpu1 - $cpu0), (($cpu1 - $cpu0) / 6000 * 100)
    }
}
finally { if (-not $tp.HasExited) { $tp.Kill() } }

"===== 8. last input ====="
$t0 = [LabNative]::LastInputTick(); Start-Sleep -Milliseconds 1500; $t1 = [LabNative]::LastInputTick()
"quiet 1.5 s: tick {0} -> {1} ({2})" -f $t0, $t1, $(if ($t1 -eq $t0) { 'no input seen' } else { 'someone or something sent input' })
$sent = [LabNative]::JiggleMouse(); Start-Sleep -Milliseconds 200; $t2 = [LabNative]::LastInputTick()
"after scripted mouse move (SendInput returned {0}): tick {1} -> {2} ({3})" -f $sent, $t1, $t2, $(if ($t2 -ne $t1) { 'scripted input DOES move the tick' } else { 'scripted input does not move the tick' })

"===== 9. contamination events ====="
function Count-Events([string]$label, [hashtable]$filter) {
    try { $e = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop); "{0}: {1} events; latest {2} id {3}" -f $label, $e.Count, $e[0].TimeCreated.ToString('MM-dd HH:mm'), $e[0].Id; $e | Group-Object Id | ForEach-Object { "    id $($_.Name): $($_.Count)" } }
    catch { if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { "{0}: readable, 0 events" -f $label } else { "{0}: unavailable ({1})" -f $label, $_.Exception.Message } }
}
Count-Events 'Defender scans, 7 d (1000 start, 1001 finish, 1002 stopped)' @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1000, 1001, 1002; StartTime = (Get-Date).AddDays(-7) }
Count-Events 'Defender definition/engine updates, 7 d (2000, 2002)' @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 2000, 2002; StartTime = (Get-Date).AddDays(-7) }
Count-Events 'Windows Update installs, 14 d (19 ok, 20 fail, 43 start)' @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WindowsUpdateClient'; Id = 19, 20, 43; StartTime = (Get-Date).AddDays(-14) }
"done"
