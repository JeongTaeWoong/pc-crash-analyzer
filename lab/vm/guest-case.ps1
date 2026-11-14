# Runs INSIDE a lab guest, in the desktop session of the lab user (sent there by lab\vm\case.ps1). ASCII only.
# Reads C:\lab\case\job.json, runs the collector (console edition) around a scenario and leaves C:\lab\case\out.zip.
#
#   start collector -> warm-up -> open background programs -> [launch check of each program | idle | fault injection]
#   -> [manual mark] -> wait for the dump -> stop collector -> result.json
#
# The collector only collects. Everything that starts, closes or stops programs is done here, in a throw-away lab guest.
#
# A run is "ok" only when the dump can be used as the case the scenario names (rules added after the first session,
# 2026-11-12, where three runs were reported ok although their dumps held no fault):
#   - the manual mark is sent after FaultLab printed that its load is running (mark.wait_for), not after it was started;
#   - a dump of the expected kind must exist (expect_dump_kind) - another trigger's dump does not count;
#   - FaultLab must end with an exit code that means the fault was made (fault.ok_exit).
# A background program that did not start is a warning: the case is still usable, but it must not claim that program.
$ErrorActionPreference = 'Continue'
$caseDir = 'C:\lab\case'
$out = Join-Path $caseDir 'out'
$job = Get-Content (Join-Path $caseDir 'job.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$collectorDir = 'C:\lab\collector'
$dumpRoot = Join-Path $collectorDir 'dumps'
if (Test-Path $dumpRoot) { Remove-Item -Recurse -Force $dumpRoot }
$t0 = Get-Date
# slowed-down profiles (CPU cap, disk limit): every wait is stretched, the fault itself is not
$slow = 1.0
if ($job.slow_factor -and [double]$job.slow_factor -gt 1) { $slow = [double]$job.slow_factor }
function Scale([double]$sec) { return [int][math]::Ceiling($sec * $slow) }
$timeline = New-Object System.Collections.ArrayList
function Note([string]$label, $extra = $null) {
    [void]$timeline.Add([pscustomobject]@{ at = (Get-Date).ToString('o'); t_sec = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1); label = $label; detail = $extra })
    "{0,7:N1}s  {1}  {2}" -f ((Get-Date) - $t0).TotalSeconds, $label, $extra
}

# ---- programs -------------------------------------------------------------------------------------------------------
$skipWords = 'uninstall|remove|help|readme|website|documentation|manual|license|release notes|safe mode|troubleshoot|reset'
function Resolve-AppExe($app) {
    if ($app.exe -and (Test-Path $app.exe)) { return $app.exe }
    $dirs = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
    $links = @(Get-ChildItem $dirs -Recurse -Filter *.lnk -ErrorAction SilentlyContinue | Where-Object { $_.BaseName -notmatch $skipWords })
    $sh = New-Object -ComObject WScript.Shell
    foreach ($needle in @($app.needles)) {
        if (-not $needle) { continue }
        $hit = $links | Where-Object { $_.BaseName -like "*$needle*" } | Sort-Object { $_.BaseName.Length } | Select-Object -First 1
        if ($hit) {
            $target = $sh.CreateShortcut($hit.FullName).TargetPath
            if ($target -and (Test-Path $target) -and $target -match '\.exe$') { return $target }
        }
    }
    # winget "portable" packages (Godot) have no shortcut: look in winget's package folder
    $pk = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    foreach ($needle in @($app.needles)) {
        if (-not $needle -or -not (Test-Path $pk)) { continue }
        $hit = Get-ChildItem $pk -Recurse -Filter *.exe -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*$needle*" -and $_.Name -notlike '*console*' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}
function Get-AppProcs([string]$folder) {
    @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase) })
}
function Start-App($app) {
    $r = [ordered]@{ id = $app.id; name = $app.name; exe = $null; version = $null; started_at = $null; ok = $false; reason = $null; processes = @(); first_exit_code = $null }
    $exe = Resolve-AppExe $app
    if (-not $exe) { $r.reason = 'no executable found (no file at the listed path, no Start-menu shortcut that matches)'; return [pscustomobject]$r }
    $r.exe = $exe
    try { $r.version = (Get-Item $exe).VersionInfo.ProductVersion } catch { }
    $r.started_at = (Get-Date).ToString('o')
    try { $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru -ErrorAction Stop }
    catch { $r.reason = "could not start: $($_.Exception.Message)"; return [pscustomobject]$r }
    $r['_proc'] = $p
    return [pscustomobject]$r
}
function Read-App($r) {
    if (-not $r.exe -or -not $r.started_at) { return $r }
    $folder = Split-Path $r.exe -Parent
    $procs = Get-AppProcs $folder
    $r.processes = @($procs | ForEach-Object { [pscustomobject]@{ name = $_.ProcessName; pid = $_.Id; window = $_.MainWindowTitle; has_window = ($_.MainWindowHandle -ne [IntPtr]::Zero); responding = $_.Responding; ws_mb = [int]($_.WorkingSet64 / 1MB) } })
    try { $r._proc.Refresh(); if ($r._proc.HasExited) { $r.first_exit_code = $r._proc.ExitCode } } catch { }
    $r.ok = ($procs.Count -gt 0)
    if (-not $r.ok) { $r.reason = "no process from $folder is alive (first process exit code $($r.first_exit_code))" }
    elseif (-not @($r.processes | Where-Object { $_.has_window }).Count) { $r.reason = 'alive but no window' }
    return $r
}
function Stop-App($r) {
    if (-not $r.exe) { return }
    $folder = Split-Path $r.exe -Parent
    foreach ($p in Get-AppProcs $folder) { try { [void]$p.CloseMainWindow() } catch { } }
    Start-Sleep -Seconds (Scale 4)
    foreach ($p in Get-AppProcs $folder) { try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { } }
}
function Get-CrashEvents($since) {
    @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1002; StartTime = $since } -ErrorAction SilentlyContinue | ForEach-Object {
        [pscustomobject]@{ at = $_.TimeCreated.ToString('o'); id = $_.Id; app = "$($_.Properties[0].Value)"; module = $(if ($_.Id -eq 1000) { "$($_.Properties[3].Value)" } else { $null }); code = $(if ($_.Id -eq 1000) { "$($_.Properties[6].Value)" } else { $null }) }
    })
}
function Get-Dumps {
    @(Get-ChildItem $dumpRoot -Directory -ErrorAction SilentlyContinue | Where-Object { (Test-Path (Join-Path $_.FullName 'meta.json')) -and (Test-Path (Join-Path $_.FullName 'engine-snapshot.json')) })
}
# The collector names a dump folder <time>-<kind>: crash-1000, hang-1001, manual, kernel-live ...
function Get-WantedDumps {
    $want = "$($job.expect_dump_kind)"
    if (-not $want) { return @(Get-Dumps) }
    return @(Get-Dumps | Where-Object { ($_.Name -replace '^\d{8}-\d{6}-', '') -like "$want*" })
}
# Lines like LOAD_AT=2026-11-12T13:10:51+09:00 that FaultLab prints when something really starts.
# The file is still open for writing by FaultLab: read it with sharing.
function Read-FaultMarkers([string]$path) {
    $m = [ordered]@{}
    if (-not (Test-Path $path)) { return $m }
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $text = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
        foreach ($line in ($text -split "`r?`n")) { if ($line -match '^([A-Z][A-Z_]*_AT)=(\S+)') { $m[$Matches[1]] = $Matches[2] } }
    } catch { }
    return $m
}

# Optional wait BEFORE the collector starts (job.pre_collector_sec, scenario 'idle-late'). Made for one question: GPU
# clones write LiveKernelEvent 0x193 about 112 s after the collector starts, in every run of two sessions - is that the
# collector's doing (its sensor readings) or the clone's own, some time after logon? With the collector started three
# minutes later the event either moves with it or stays where it was. boot_at and live_kernel_events in the result
# give the times since boot, whatever the collector saw.
$bootAt = $null
try { $bootAt = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime } catch { }
function Get-LiveKernelEvents {
    $since = $(if ($bootAt) { $bootAt } else { $t0.AddHours(-1) })
    @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1001; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { "$($_.Properties[2].Value)" -eq 'LiveKernelEvent' } | Sort-Object TimeCreated |
        ForEach-Object { [pscustomobject]@{ at = $_.TimeCreated.ToString('o'); code = "$($_.Properties[5].Value)" } })
}
$preCollector = 0
if ($job.pre_collector_sec) {
    $preCollector = [int]$job.pre_collector_sec
    Note 'pre_collector_wait' "$preCollector s with no collector running"
    Start-Sleep -Seconds $preCollector
}

# ---- collector ------------------------------------------------------------------------------------------------------
$collectorLog = Join-Path $out 'collector.log'
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "$env:SystemRoot\System32\cmd.exe"
$psi.Arguments = '/c BlackBox.exe > "' + $collectorLog + '" 2>&1'
$psi.WorkingDirectory = $collectorDir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.CreateNoWindow = $true
$collector = $null
$collectorError = $null
try { $collector = [System.Diagnostics.Process]::Start($psi) } catch { $collectorError = $_.Exception.Message }
Note 'collector_start' $(if ($collectorError) { "FAILED: $collectorError" } else { 'BlackBox.exe (console edition, elevated)' })
# Commands go to the collector as plain ASCII bytes, after one blank line. When the parent's console input encoding is
# UTF-8, Windows PowerShell sends a byte-order mark down the pipe the moment the process starts; it would be glued to
# the first command and the collector would not recognize "m slow" (seen 2026-11-12 on the host - the guests so far
# used the Korean code page, where nothing is sent). The blank line takes the mark with it: the collector answers
# that one line with its help text, or ignores it when there was no mark.
$collectorIn = $null
if ($collector) {
    $collectorIn = New-Object IO.StreamWriter($collector.StandardInput.BaseStream, [Text.Encoding]::ASCII)
    $collectorIn.AutoFlush = $true
    try { $collectorIn.WriteLine('') } catch { }
}
function Send-Collector([string]$line) {
    try { $collectorIn.WriteLine($line); return $true } catch { return $false }
}

$warm = [int]$job.warmup_sec
Start-Sleep -Seconds $warm
$collectorAliveAfterWarmup = [bool](Get-Process BlackBox -ErrorAction SilentlyContinue)
Note 'warmup_done' "collector alive: $collectorAliveAfterWarmup"

# ---- background programs (stay open during the scenario) ---------------------------------------------------------------
$opened = @()
foreach ($a in @($job.open)) {
    if (-not $a) { continue }
    $r = Start-App $a
    Note 'open' "$($a.id) -> $($r.exe) $($r.reason)"
    $opened += $r
    Start-Sleep -Seconds (Scale ([int]$job.open_gap_sec))
}
if ($opened.Count) { Start-Sleep -Seconds (Scale 15); $opened = @($opened | ForEach-Object { Read-App $_ }) }

# ---- decoys: alias builds of the fault program that only sit there (task.cfg: run=idle). They are not the culprit. ----
function Write-TaskCfg([string]$exe, [string[]]$lines) {
    [IO.File]::WriteAllLines((Join-Path (Split-Path $exe -Parent) 'task.cfg'), $lines, (New-Object Text.UTF8Encoding($false)))
}
$decoys = @()
foreach ($d in @($job.decoys)) {
    if (-not $d -or -not $d.exe) { continue }
    $row = [ordered]@{ id = $d.id; exe = $d.exe; started = $false; alive_at_end = $null; reason = $null }
    if (-not (Test-Path $d.exe)) { $row.reason = 'not installed in the guest'; $decoys += [pscustomobject]$row; continue }
    try {
        Write-TaskCfg $d.exe @('run=idle 7200', "title=$($d.title)")
        $dp = Start-Process -FilePath $d.exe -WorkingDirectory (Split-Path $d.exe -Parent) -PassThru -WindowStyle Hidden -ErrorAction Stop
        $row.started = $true; $row['_pid'] = $dp.Id
        Note 'decoy' "$($d.id) -> $($d.exe)"
    } catch { $row.reason = "could not start: $($_.Exception.Message)" }
    $decoys += [pscustomobject]$row
    Start-Sleep -Seconds (Scale 3)
}

# ---- a real program: the scenario itself (kind 'recipe') or the user's own job running next to a fault ('workload') ----
# Added 2026-11-14. spec.exe = a full path (a package from packages.json), spec.app_spec + exe_name = a program
# installed in the clone, spec.driver = a script under C:\lab\case\drivers that presses the keys a person would press.
function Join-Args($list) {
    # Start-Process joins arguments with spaces and adds no quotes: quote what holds a space or a semicolon
    return (@($list) | Where-Object { $null -ne $_ } | ForEach-Object { $a = "$_"; if ($a -match '[\s;]' -and $a -notmatch '^".*"$') { '"' + $a + '"' } else { $a } }) -join ' '
}
function Start-RunSpec($spec, [string]$logName) {
    $r = [ordered]@{ exe = $null; args = $null; started_at = $null; reason = $null; exit_code = $null; timed_out = $false; stopped_by_us = $false; alive_at_mark = $null; alive_at_end = $null }
    $exe = $null
    $argList = @($spec.args)
    if ($spec.driver) {
        $drv = Join-Path 'C:\lab\case\drivers' "$($spec.driver)"
        if (-not (Test-Path $drv)) { $r.reason = "driver script $drv is not in the guest"; return [pscustomobject]$r }
        $exe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $drv) + @($spec.driver_args)
    } elseif ($spec.exe) {
        $exe = "$($spec.exe)"
    } elseif ($spec.app_spec) {
        $found = Resolve-AppExe $spec.app_spec
        if ($found) { $exe = $(if ($spec.exe_name) { Join-Path (Split-Path $found -Parent) "$($spec.exe_name)" } else { $found }) }
    }
    if (-not $exe -or -not (Test-Path $exe)) { $r.reason = "program not found in the guest ($exe)"; return [pscustomobject]$r }
    $r.exe = $exe
    $cwd = $(if ($spec.cwd) { "$($spec.cwd)" } else { Split-Path $exe -Parent })
    if (-not (Test-Path $cwd)) { New-Item -ItemType Directory -Force -Path $cwd | Out-Null }
    $line = Join-Args $argList
    $r.args = $line
    try {
        $sp = @{ FilePath = $exe; WorkingDirectory = $cwd; PassThru = $true; WindowStyle = 'Hidden'; ErrorAction = 'Stop'; RedirectStandardOutput = (Join-Path $out $logName) }
        if ($line) { $sp.ArgumentList = $line }
        $p = Start-Process @sp
        $null = $p.Handle                                            # without this the exit code is not kept
        $r['_proc'] = $p
        $r.started_at = (Get-Date).ToString('o')
    } catch { $r.reason = "could not start: $($_.Exception.Message)" }
    return [pscustomobject]$r
}
function Stop-Tree($p) {
    if (-not $p) { return }
    try { if (-not $p.HasExited) { & "$env:SystemRoot\System32\taskkill.exe" /PID $p.Id /T /F 2>&1 | Out-Null } } catch { }
}
$workload = $null
if ($job.workload) {
    $workload = Start-RunSpec $job.workload 'workload.log'
    Note 'workload_start' $(if ($workload.started_at) { "$($workload.exe) $($workload.args)" } else { "FAILED: $($workload.reason)" })
    Start-Sleep -Seconds (Scale $(if ($job.workload.lead_sec) { [int]$job.workload.lead_sec } else { 15 }))
}

# ---- scenario -------------------------------------------------------------------------------------------------------
$launched = @()
$fault = $null
$recipeRun = $null
$markSent = $null
if ($job.kind -eq 'launch') {
    foreach ($a in @($job.launch)) {
        if (-not $a) { continue }
        $since = Get-Date
        $r = Start-App $a
        Note 'launch' "$($a.id) -> $($r.exe) $($r.reason)"
        if ($r.started_at) {
            $wait = if ($a.launch_sec) { [int]$a.launch_sec } else { [int]$job.launch_sec }
            Start-Sleep -Seconds (Scale $wait)
            $r = Read-App $r
            $r | Add-Member -NotePropertyName crash_events -NotePropertyValue (Get-CrashEvents $since) -Force
            $w = ($r.processes | Where-Object { $_.has_window } | Select-Object -First 1)
            Note 'launch_read' ("{0}: ok={1} window='{2}' procs={3} {4}" -f $a.id, $r.ok, $w.window, @($r.processes).Count, $r.reason)
            Stop-App $r
            Start-Sleep -Seconds 3
        }
        $launched += $r
    }
} elseif ($job.kind -eq 'idle') {
    $idle = if ($job.idle_sec) { [int]$job.idle_sec } else { 300 }
    Note 'idle' "nothing is started for $idle s"
    Start-Sleep -Seconds $idle
} elseif ($job.kind -eq 'fault') {
    # With an alias (job.fault_app) the fault program runs under an ordinary name from Program Files and is started
    # WITHOUT arguments - what it should do is in task.cfg next to it - so that neither the process name nor the
    # command line the collector may record says what was injected.
    $flExe = if ($job.fault_app -and $job.fault_app.exe) { "$($job.fault_app.exe)" } else { 'C:\lab\faultlab\FaultLab.exe' }
    $flLog = Join-Path $out 'faultlab.log'
    $flArgs = @($job.fault.mode) + @($job.fault.args | ForEach-Object { "$_" })
    $flTimeout = Scale ([int]$job.fault.timeout_sec)
    if (-not (Test-Path $flExe)) { Note 'fault_start' "FAILED: $flExe is not in the guest" }
    else {
        if ($job.fault_app) {
            Write-TaskCfg $flExe @(("run=" + ($flArgs -join ' ')), "title=$($job.fault_app.title)", "host=$($job.fault_app.host)", "plugin=$($job.fault_app.plugin)")
            $fp = Start-Process -FilePath $flExe -WorkingDirectory (Split-Path $flExe -Parent) -RedirectStandardOutput $flLog -PassThru
        } else {
            $fp = Start-Process -FilePath $flExe -ArgumentList $flArgs -WorkingDirectory 'C:\lab\faultlab' -RedirectStandardOutput $flLog -PassThru
        }
        $null = $fp.Handle                                           # without this the exit code is not kept
        Note 'fault_start' ((Split-Path $flExe -Leaf) + ' <- ' + ($flArgs -join ' '))
        $fault = [ordered]@{
            mode = $job.fault.mode; args = $flArgs; started_at = (Get-Date).ToString('o'); exit_code = $null; timed_out = $false
            wait_for = $null; active_at = $null; alive_at_mark = $null; markers = $null
        }
        if ($job.mark -and $job.mark.after_sec -ge 0) {
            # The mark is what a user does when the PC feels wrong - so the load must really be running by then.
            # Starting FaultLab is not that moment: on a slow guest loading the program alone took 80 s (2026-11-12).
            $waitFor = "$($job.mark.wait_for)"
            $active = $true
            if ($waitFor) {
                $fault.wait_for = $waitFor
                $active = $false
                $until = (Get-Date).AddSeconds($flTimeout)
                while ((Get-Date) -lt $until) {
                    if ((Read-FaultMarkers $flLog).Contains($waitFor)) { $active = $true; break }
                    if ($fp.HasExited) { $active = (Read-FaultMarkers $flLog).Contains($waitFor); break }
                    Start-Sleep -Seconds 2
                }
                if ($active) { $fault.active_at = (Get-Date).ToString('o'); Note 'fault_active' "FaultLab printed $waitFor" }
                else { Note 'fault_active' "FAILED: FaultLab never printed $waitFor (waited up to $flTimeout s)" }
            }
            if ($active) {
                Start-Sleep -Seconds ([int]$job.mark.after_sec)
                $fault.alive_at_mark = (-not $fp.HasExited)
                $ok = Send-Collector ("m " + $job.mark.text).Trim()
                $markSent = (Get-Date).ToString('o')
                Note 'manual_mark' "'m $($job.mark.text)' sent: $ok, fault still running: $($fault.alive_at_mark) (what a user would do when the PC feels wrong)"
            }
        }
        if (-not $fp.WaitForExit($flTimeout * 1000)) { $fault.timed_out = $true; try { $fp.Kill() } catch { }; Note 'fault_end' 'timed out, stopped' }
        else { $fault.exit_code = $fp.ExitCode; Note 'fault_end' ("exit code 0x{0:x}" -f $fp.ExitCode) }
        $fault.markers = Read-FaultMarkers $flLog
    }
} elseif ($job.kind -eq 'recipe') {
    # A real program run as a person would run it. With "crash" the program is expected to end badly (a documented
    # bug); with a mark it is the user's own heavy job and the user marks 'slow' while it runs.
    $recipeRun = Start-RunSpec $job.recipe 'recipe.log'
    if (-not $recipeRun.started_at) { Note 'recipe_start' "FAILED: $($recipeRun.reason)" }
    else {
        Note 'recipe_start' "$($recipeRun.exe) $($recipeRun.args)"
        $rp = $recipeRun._proc
        $deadlineR = (Get-Date).AddSeconds((Scale ([int]$job.recipe.timeout_sec)))
        $marked = $false
        if ($job.mark -and $job.mark.after_sec -ge 0) {
            Start-Sleep -Seconds ([int]$job.mark.after_sec)
            $recipeRun.alive_at_mark = (-not $rp.HasExited)
            $ok = Send-Collector ("m " + $job.mark.text).Trim()
            $markSent = (Get-Date).ToString('o')
            $marked = $true
            Note 'manual_mark' "'m $($job.mark.text)' sent: $ok, program still running: $($recipeRun.alive_at_mark) (what a user would do when the PC feels wrong)"
        }
        if ($marked -and $job.recipe.stop_at_end) {
            # the dump takes what comes up to 30 s after the mark; the job is not needed after that
            Start-Sleep -Seconds 45
            if (-not $rp.HasExited) { Stop-Tree $rp; $recipeRun.stopped_by_us = $true; Note 'recipe_end' 'stopped after the mark (the job was still running)' }
            else { $recipeRun.exit_code = $rp.ExitCode; Note 'recipe_end' ("ended by itself, exit code 0x{0:x}" -f $rp.ExitCode) }
        } else {
            $leftMs = [int][math]::Max(1000, ($deadlineR - (Get-Date)).TotalMilliseconds)
            if (-not $rp.WaitForExit($leftMs)) { $recipeRun.timed_out = $true; Stop-Tree $rp; $recipeRun.stopped_by_us = $true; Note 'recipe_end' 'still running at the time limit, stopped' }
            else { $recipeRun.exit_code = $rp.ExitCode; Note 'recipe_end' ("exit code 0x{0:x}" -f $rp.ExitCode) }
        }
    }
}
# programs that were open in the background: are they still alive after the scenario?
$opened = @($opened | ForEach-Object { Read-App $_ })

if ($job.kind -in 'launch', 'idle' -or ($job.mark -and $job.mark.at_end)) {
    $text = if ($job.mark -and $job.mark.text) { "m $($job.mark.text)" } else { 'm' }
    $ok = Send-Collector $text
    $markSent = (Get-Date).ToString('o')
    Note 'manual_mark' "'$text' sent at the end: $ok"
}

# ---- wait for the dump, stop the collector ----------------------------------------------------------------------------
# Wait for a dump of the kind the scenario is about: a GPU clone writes kernel-live dumps of its own, and such a dump
# must not end the wait before the one we need is written.
$expectDump = ($job.kind -in 'launch', 'idle') -or [bool]$job.expect_dump
$deadline = (Get-Date).AddSeconds((Scale ([int]$job.wait_dump_sec)))
if ($expectDump) { while ((Get-Date) -lt $deadline -and -not (Get-WantedDumps).Count) { Start-Sleep -Seconds 3 } }
if ((Get-Dumps).Count) { Start-Sleep -Seconds (Scale 8) }         # a second trigger (1001 after 1000) may still be writing
Note 'dump_wait_done' "dumps: $((Get-Dumps).Count), of the expected kind '$($job.expect_dump_kind)': $((Get-WantedDumps).Count)"
$collectorExit = $null
if ($collector -and -not $collector.HasExited) {
    [void](Send-Collector 'q')
    if (-not $collector.WaitForExit((Scale 90) * 1000)) { Note 'collector_stop' "did not stop in $(Scale 90) s, killed"; try { Get-Process BlackBox -ErrorAction SilentlyContinue | Stop-Process -Force } catch { } }
    else { $collectorExit = $collector.ExitCode; Note 'collector_stop' "exit code $collectorExit" }
} else { Note 'collector_stop' 'the collector was not running at the end' }
foreach ($r in $opened) { Stop-App $r }
if ($workload -and $workload._proc) {
    $workload.alive_at_end = (-not $workload._proc.HasExited)
    if (-not $workload.alive_at_end) { $workload.exit_code = $workload._proc.ExitCode }
    Stop-Tree $workload._proc
    Note 'workload_end' "still running at the end: $($workload.alive_at_end)"
}
foreach ($d in $decoys) {
    if (-not $d.started) { continue }
    $dp = Get-Process -Id $d._pid -ErrorAction SilentlyContinue
    $d.alive_at_end = [bool]$dp
    if ($dp) { try { Stop-Process -Id $d._pid -Force -ErrorAction Stop } catch { } }
}

# ---- result ---------------------------------------------------------------------------------------------------------
$dumps = @(Get-Dumps | ForEach-Object {
    $m = $null; try { $m = Get-Content (Join-Path $_.FullName 'meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    [pscustomobject]@{ name = $_.Name; kind = $m.kind; mark_time = $m.markTime; significance = $m.significance.Score; tier = $m.significance.Tier
        sources = @($m.sources | ForEach-Object { "$($_.Name)=$($_.Status)" }); events_in_dump = $m.eventsInDump; files = @(Get-ChildItem $_.FullName -File | ForEach-Object { "$($_.Name) $($_.Length)" }) }
})
if (Test-Path $dumpRoot) { Copy-Item $dumpRoot (Join-Path $out 'dumps') -Recurse -Force }
$os = Get-CimInstance Win32_OperatingSystem
$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$strip = { param($list) @($list | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty _proc }) }
$problems = @()
$warnings = @()
if ($collectorError) { $problems += "collector did not start: $collectorError" }
if (-not $collectorAliveAfterWarmup) { $problems += 'collector was not alive after the warm-up' }
if ($expectDump -and -not $dumps.Count) { $problems += 'no dump was written' }
elseif ($expectDump -and $job.expect_dump_kind -and -not @($dumps | Where-Object { "$($_.kind)" -like "$($job.expect_dump_kind)*" }).Count) {
    $problems += "no '$($job.expect_dump_kind)' dump was written (only: $(@($dumps | ForEach-Object { $_.kind }) -join ', ')) - the dumps do not hold this scenario"
}
if ($job.kind -eq 'fault' -and -not $fault) { $problems += 'the fault did not start' }
if ($fault) {
    if ($fault.timed_out) { $problems += 'FaultLab did not end in time and was stopped' }
    elseif ($job.fault.ok_exit -and (@($job.fault.ok_exit | ForEach-Object { [int]$_ }) -notcontains [int]$fault.exit_code)) {
        $problems += ("FaultLab ended with exit code 0x{0:x}: the fault was not made (see faultlab.log)" -f $fault.exit_code)
    }
    if ($fault.wait_for -and -not $fault.active_at) { $problems += "FaultLab never printed $($fault.wait_for): the load did not start, no mark was sent" }
    if ($fault.active_at -and $fault.alive_at_mark -eq $false) { $problems += 'the fault had already ended when the mark was sent' }
}
if ($job.kind -eq 'recipe') {
    if (-not $recipeRun -or -not $recipeRun.started_at) { $problems += "the program did not start: $($recipeRun.reason)" }
    else {
        if ($job.recipe.crash) {
            $failExit = @(0)
            if ($job.recipe.fail_exit) { $failExit = @($job.recipe.fail_exit | ForEach-Object { [int]$_ }) }
            if ($recipeRun.timed_out) { $problems += 'the program was still running at the time limit: the bug did not show' }
            elseif ($failExit -contains [int]$recipeRun.exit_code) { $problems += "the program ended with exit code $($recipeRun.exit_code): the bug did not show (see recipe.log)" }
        }
        if ($job.mark -and $recipeRun.alive_at_mark -eq $false) { $problems += 'the program had already ended when the mark was sent' }
    }
}
if ($job.workload) {
    if (-not $workload -or -not $workload.started_at) { $problems += "the user's job did not start: $($workload.reason)" }
    elseif ($workload.alive_at_end -eq $false) { $warnings += "the user's job had ended before the case ended (exit code $($workload.exit_code))" }
}
foreach ($o in $opened) { if (-not $o.ok) { $warnings += "background program '$($o.id)' was not running: $($o.reason)" } }
foreach ($d in $decoys) { if (-not $d.started -or $d.alive_at_end -eq $false) { $warnings += "decoy '$($d.id)' was not running through the case: $($d.reason)" } }
$result = [ordered]@{
    schema = 2
    scenario = $job.scenario; kind = $job.kind
    ok = ($problems.Count -eq 0); problems = $problems; warnings = $warnings
    slow_factor = $slow
    started_at = $t0.ToString('o'); ended_at = (Get-Date).ToString('o')
    guest = [ordered]@{
        computer = $env:COMPUTERNAME; os = $os.Caption; build = $os.BuildNumber; memory_mb = [int]($os.TotalVisibleMemorySize / 1024)
        free_memory_mb_at_end = [int]($os.FreePhysicalMemory / 1024); logical_cpus = [Environment]::ProcessorCount
        video = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) | $($_.Status) | driver $($_.DriverVersion)" })
        elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    collector = [ordered]@{ alive_after_warmup = $collectorAliveAfterWarmup; exit_code = $collectorExit; start_error = $collectorError }
    fault = $fault; manual_mark_at = $markSent
    opened = & $strip $opened
    decoys = @($decoys | ForEach-Object { $_ | Select-Object -Property * -ExcludeProperty _pid })
    fault_app = $(if ($job.fault_app) { Split-Path "$($job.fault_app.exe)" -Leaf } else { $null })
    launched = & $strip $launched
    crash_events = Get-CrashEvents $t0
    boot_at = $(if ($bootAt) { $bootAt.ToString('o') } else { $null }); pre_collector_sec = $preCollector
    live_kernel_events = Get-LiveKernelEvents
    dumps = $dumps
    installed_now = @(Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -and -not $_.SystemComponent } | Sort-Object DisplayName | ForEach-Object { "$($_.DisplayName) | $($_.DisplayVersion)" })
    timeline = $timeline
}
$result.recipe = $(if ($recipeRun) { $recipeRun | Select-Object -Property * -ExcludeProperty _proc } else { $null })
$result.workload = $(if ($workload) { $workload | Select-Object -Property * -ExcludeProperty _proc } else { $null })
ConvertTo-Json -InputObject $result -Depth 6 | Set-Content -Path (Join-Path $out 'result.json') -Encoding UTF8
$zip = Join-Path $caseDir 'out.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
"result: ok=$($result.ok) dumps=$($dumps.Count) problems=$($problems -join '; ') warnings=$($warnings -join '; ')"
exit $(if ($result.ok) { 0 } else { 3 })
