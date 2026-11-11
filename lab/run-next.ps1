# State machine step. Runs at logon while a session is active (start-session.ps1 puts a launcher in the Startup folder),
# does ONE unit of work (a measurement block, the install, or a settling boot) and then either reboots for the next
# unit or ends the session and locks the PC. With no active session it does nothing.
#   run-next.ps1 [-RunsRoot <dir>] [-Simulate] [-Test]
# -Simulate: never reboots, never locks, never starts the real installer; the caller invokes it again to play "next boot".
# -Test    : passes -Test to run-block (short waits). Use both for dry runs.
param([string]$RunsRoot, [switch]$Simulate, [switch]$Test, [string]$TestApps)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"; . "$lib\LabAnalyze.ps1"
$Lab = Get-LabContext $RunsRoot
$state = Read-LabState $Lab
if (-not $state -or -not $state.session -or -not $state.session.active) { Write-Host 'lab: no active session'; exit 0 }
$cfg = $Lab.Config
$startupFile = Join-Path ([Environment]::GetFolderPath('Startup')) 'pca-lab-run-next.cmd'
$installMarker = Join-Path $Lab.Runs 'install.done'

function End-Session([string]$why) {
    $state.session.active = $false; $state.session.ended = (Get-Date).ToString('o'); $state.session.endReason = $why
    $state.sessions = @($state.sessions) + $state.session
    $state.lastMessage = $why
    Save-LabState $Lab $state
    Remove-Item -LiteralPath $startupFile -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $Lab.Runs 'STOP') -ErrorAction SilentlyContinue
    Write-LabLog $Lab "SESSION END: $why"
    Write-LabLog $Lab ("progress: pilot {0}, before-install {1}, after-install {2}, phase {3}" -f (Get-LabBootCount $state 'pilot'), (Get-LabBootCount $state 's0'), (Get-LabBootCount $state 's1'), $state.phase)
    if (-not $Simulate) { [void][LabNative]::Lock() }      # auto-logon left the desktop open; do not leave it that way
    exit 0
}
function Request-Reboot([string]$why) {
    $state.reboots = [int]$state.reboots + 1
    if ($state.reboots -gt 40) { End-Session 'reboot limit reached (40)' }
    Save-LabState $Lab $state
    Write-LabLog $Lab "REBOOT: $why"
    if ($Simulate) { exit 10 }                               # the test harness treats 10 as "would reboot now"
    & "$env:SystemRoot\System32\shutdown.exe" /r /t 20 /c "PCA lab: restarting for the next measurement block. Stop with lab\stop-session.ps1"
    exit 0
}
function Minutes-Left { return ([datetime]$state.session.until - (Get-Date)).TotalMinutes }
function Enough-Time { return (Minutes-Left) -ge ([double]$cfg.timing.block_estimate_min + [double]$cfg.timing.session_margin_min) }

if ($state.stopRequested -or (Test-Path -LiteralPath (Join-Path $Lab.Runs 'STOP'))) { $state.stopRequested = $false; End-Session 'stopped on request' }

# ---- what is next ----------------------------------------------------------------------------------------------------------
$k = if ($state.plan) { [int]$state.plan.k } else { [int]$cfg.default_plan.k }
switch ($state.phase) {
    'pilot' { if ((Get-LabBootCount $state 'pilot') -ge [int]$cfg.pilot.boots) { $state.phase = 's0' } }
    's0' { if ((Get-LabBootCount $state 's0') -ge $k) { $state.phase = 'install' } }
    's1' { if ((Get-LabBootCount $state 's1') -ge $k) { $state.phase = 'done' } }
}
Save-LabState $Lab $state

if ($state.phase -eq 'done') { End-Session 'experiment complete: run lab\analyze.ps1' }

if ($state.phase -eq 'install') {
    if (-not $state.session.allowInstall) { End-Session 'before-install measurement is complete. Start the next session with -AllowInstall to install the security programs (cannot be undone for this experiment)' }
    # gate: the baseline cannot be extended after this point
    $s0 = @($state.boots | Where-Object { $_.phase -eq 's0' -and $_.status -eq 'complete' })
    $excluded = ($s0 | Measure-Object excluded -Sum).Sum; $total = ($s0 | Measure-Object runs -Sum).Sum
    if ($s0.Count -lt $k) { End-Session "install gate: only $($s0.Count) of $k baseline boots" }
    if ($total -gt 0 -and $excluded / $total -gt 0.2) { End-Session ("install gate: {0:P0} of baseline runs were excluded (limit 20%). Look at the run files before going on" -f ($excluded / $total)) }
    Write-LabLog $Lab "INSTALL: gate passed ($($s0.Count) baseline boots, $excluded of $total runs excluded)"
    Remove-Item -LiteralPath $installMarker -ErrorAction SilentlyContinue
    if ($Simulate) { [IO.File]::WriteAllText($installMarker, $(if ($env:PCA_LAB_SIM_INSTALL -eq 'fail') { '{"status":"failed","detail":"simulated failure"}' } else { '{"status":"ok","detail":"simulated: nothing was installed"}' })) }
    else {
        # The elevated task is registered once by the user (lab\setup-once.ps1). Starting it does not bypass UAC:
        # the consent was given when the task was created with "run with highest privileges".
        & "$env:SystemRoot\System32\schtasks.exe" /Run /TN '\PcaLab\Install' | Out-Null
        if ($LASTEXITCODE -ne 0) { End-Session 'install task \PcaLab\Install is not registered: run lab\setup-once.ps1 as administrator first' }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $installMarker) -and $sw.Elapsed.TotalHours -lt 3) { Start-Sleep -Seconds 30 }
        if (-not (Test-Path -LiteralPath $installMarker)) { End-Session 'install task did not finish within 3 hours' }
    }
    $result = Read-LabJson $installMarker
    Write-LabLog $Lab "INSTALL result: $($result.status) - $($result.detail)"
    # nothing installed means there is no "after" state to measure: stop here and leave the phase at 'install'
    if ($result.status -ne 'ok') { End-Session "install step failed: $($result.detail)" }
    $state.installed = (Get-Date).ToString('o'); $state.phase = 'settle'; $state.settleBootsDone = 0
    Request-Reboot 'install finished; settling boots next'
}

if ($state.phase -eq 'settle') {
    if ([int]$state.settleBootsDone -ge [int]$cfg.settle_boots_after_install) { $state.phase = 's1'; Save-LabState $Lab $state }
    else {
        if (-not $Simulate) { Start-Sleep -Seconds 180 }        # let first-run work after the install finish; nothing is measured
        $state.settleBootsDone = [int]$state.settleBootsDone + 1
        Request-Reboot "settling boot $($state.settleBootsDone) of $($cfg.settle_boots_after_install)"
    }
}

# ---- a measurement block -----------------------------------------------------------------------------------------------------
$phase = $state.phase
if ($phase -notin 'pilot', 's0', 's1') { End-Session "unexpected phase '$phase'" }
if (-not (Enough-Time)) { End-Session ("not enough time left for another block ({0:N0} min left, a block needs about {1})" -f (Minutes-Left), ([int]$cfg.timing.block_estimate_min + [int]$cfg.timing.session_margin_min)) }

$bootTime = if ($Simulate) { "sim-$([int]$state.reboots)" } else { Get-LabBootTime }
if (@($state.boots | Where-Object { $_.bootTime -eq $bootTime -and $_.status -eq 'complete' }).Count) { Request-Reboot 'a block was already measured in this boot; one block per boot' }

$number = @($state.boots | Where-Object { $_.phase -eq $phase }).Count + 1
$bootId = '{0}-{1:D2}' -f $phase, $number
if ($phase -eq 'pilot') { $rm = [int]$cfg.pilot.reps_main; $rs = [int]$cfg.pilot.reps_sub }
elseif ($state.plan) { $rm = [int]$state.plan.reps_main; $rs = [int]$state.plan.reps_sub }
else { $rm = [int]$cfg.default_plan.reps_main; $rs = [int]$cfg.default_plan.reps_sub }

$blockArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'run-block.ps1'), '-BootId', $bootId, '-Phase', $phase, '-RepsMain', $rm, '-RepsSub', $rs, '-RunsRoot', $Lab.Runs, '-Experiment', $state.experiment, '-BootTime', $bootTime)
if ($Test) { $blockArgs += '-Test' }
if ($TestApps) { $blockArgs += @('-Apps', $TestApps) }
& powershell @blockArgs
$code = $LASTEXITCODE
$summaryPath = Join-Path $Lab.Runs "boots\$bootId\summary.json"
$summary = if (Test-Path -LiteralPath $summaryPath) { Read-LabJson $summaryPath } else { $null }
$state = Read-LabState $Lab       # stop-session may have written to it meanwhile
$record = [pscustomobject]@{ id = $bootId; phase = $phase; bootTime = $bootTime; session = $state.session.id; finished = (Get-Date).ToString('o'); status = $(if ($code -eq 0) { 'complete' } elseif ($code -eq 4) { 'stopped' } elseif ($code -eq 2) { 'refused' } else { 'failed' }); runs = $(if ($summary) { [int]$summary.runs } else { 0 }); excluded = $(if ($summary) { [int]$summary.excluded } else { 0 }); meanRunSec = $(if ($summary) { $summary.meanRunSec } else { 0 }) }

if ($code -eq 4) { $state.stopRequested = $false; Remove-Item -LiteralPath (Join-Path $Lab.Runs "boots\$bootId") -Recurse -Force -ErrorAction SilentlyContinue; End-Session 'stopped on request (the unfinished block will be measured again)' }
if ($code -eq 2) { End-Session "preflight refused to start $bootId (see boots\$bootId\preflight.json)" }
$state.boots = @($state.boots) + $record
if ($code -ne 0) {
    $state.consecutiveFailures = [int]$state.consecutiveFailures + 1
    Rename-Item -LiteralPath (Join-Path $Lab.Runs "boots\$bootId") -NewName "$bootId-failed-$((Get-Date).ToString('HHmmss'))" -ErrorAction SilentlyContinue
    if ($state.consecutiveFailures -ge 2) { End-Session 'two blocks failed in a row' }
    Request-Reboot "block $bootId failed; trying once more after a reboot"
}
$state.consecutiveFailures = 0

# after the last pilot boot: choose k and r from the pilot data, by formula, and write them down before any baseline boot
if ($phase -eq 'pilot' -and (Get-LabBootCount $state 'pilot') -ge [int]$cfg.pilot.boots) {
    $pilot = @($state.boots | Where-Object { $_.phase -eq 'pilot' -and $_.status -eq 'complete' })
    $runCost = [math]::Max(10.0, ($pilot | Measure-Object meanRunSec -Average).Average)
    $bootCost = [double]$cfg.timing.post_boot_wait_sec + 150
    $state.plan = Get-LabPlan $Lab $bootCost $runCost ([int]$cfg.pilot.reps_main)
    $state.phase = 's0'
    Write-LabJson (Join-Path $Lab.Runs 'plan.json') $state.plan
    Write-LabLog $Lab ("PLAN from pilot: k={0} boots per state, {1} runs per main cell, {2} per secondary cell ({3}){4}" -f $state.plan.k, $state.plan.reps_main, $state.plan.reps_sub, $state.plan.source, $(if ($state.plan.target_not_reachable) { '; target precision not reachable within k_max' }))
}
Save-LabState $Lab $state

# is there anything left that fits in this session?
$k = if ($state.plan) { [int]$state.plan.k } else { [int]$cfg.default_plan.k }
$nextIsBlock = ($state.phase -eq 'pilot') -or ($state.phase -eq 's0' -and (Get-LabBootCount $state 's0') -lt $k) -or ($state.phase -eq 's1' -and (Get-LabBootCount $state 's1') -lt $k)
if ($nextIsBlock -and -not (Enough-Time)) { End-Session ("block $bootId done; no time for another ({0:N0} min left)" -f (Minutes-Left)) }
if ($state.phase -eq 's1' -and (Get-LabBootCount $state 's1') -ge $k) { $state.phase = 'done'; Save-LabState $Lab $state; End-Session 'experiment complete: run lab\analyze.ps1' }
if ($state.phase -eq 's0' -and (Get-LabBootCount $state 's0') -ge $k -and -not $state.session.allowInstall) { $state.phase = 'install'; Save-LabState $Lab $state; End-Session 'before-install measurement is complete. Start the next session with -AllowInstall to install the security programs (cannot be undone for this experiment)' }
Request-Reboot "block $bootId done"
