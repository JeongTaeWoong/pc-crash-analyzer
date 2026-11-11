# Starts an unattended session: checks the PC, registers the logon launcher, restarts, and from then on run-next.ps1
# measures block after block until -Until, then locks the PC. Use the PC normally between sessions.
#   lab\start-session.ps1 -Until 07:30            (or -Hours 6)
#   lab\start-session.ps1 -Until 07:30 -AllowInstall   only this lets the session install the security programs
# Stop early with lab\stop-session.ps1.
param(
    [string]$Until, [double]$Hours,
    [switch]$AllowInstall,
    [switch]$AcceptFingerprintChange,   # go on although drivers/services/browser/OS changed since the first session (recorded)
    [switch]$Simulate, [string]$RunsRoot, [switch]$Test
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"
$Lab = Get-LabContext $RunsRoot

if ($Hours -gt 0) { $end = (Get-Date).AddHours($Hours) }
elseif ($Until) { $end = [datetime]::ParseExact($Until, 'HH:mm', $null); if ($end -le (Get-Date)) { $end = $end.AddDays(1) } }
else { throw 'give -Until HH:mm or -Hours N' }

$state = Read-LabState $Lab
if (-not $state) { $state = New-LabState $Lab }
if ($state.session -and $state.session.active) { throw 'a session is already active. Stop it with lab\stop-session.ps1 first' }
if ($state.phase -eq 'done') { throw 'the experiment is complete. Run lab\analyze.ps1' }

# ---- checks ------------------------------------------------------------------------------------------------------------------
$pre = Test-LabPreflight $Lab
foreach ($w in $pre.warnings) { Write-Host "warning: $w" -ForegroundColor Yellow }
if ($pre.blocking.Count -and -not $Test) { $pre.blocking | ForEach-Object { Write-Host "cannot start: $_" -ForegroundColor Red }; exit 2 }

$fp = Get-LabFingerprint $Lab
if (-not $state.reference) { $state.reference = $fp; Write-Host 'recorded this PC state as the reference for the whole experiment' }
else {
    # Compare with the state that was actually measured: the newest complete block of the current state (before or after
    # the install). Only when there is none yet, fall back to the fingerprint stored at a session start.
    $boot = Get-LabLastBootFingerprint $Lab $(if ($state.installed) { 's1' } else { 's0' })
    if ($boot) { $ref = $boot.fingerprint; $refName = "block $($boot.id)" }
    else {
        $ref = if ($state.installed -and $state.referenceAfterInstall) { $state.referenceAfterInstall } else { $state.reference }; $refName = 'the first session'
        # the install itself changes drivers and services: the first session after it becomes the reference for the "after" state
        if ($state.installed -and -not $state.referenceAfterInstall) { $state.referenceAfterInstall = $fp; $ref = $fp; $refName = 'this session' }
    }
    $d = Compare-LabFingerprint $ref $fp -ConfigOnly
    foreach ($n in $d.notes) { Write-Host "changed since $refName (recorded): $n" -ForegroundColor Yellow }
    if ($d.blocking.Count) {
        $d.blocking | ForEach-Object { Write-Host "CHANGED since ${refName}: $_" -ForegroundColor Red }
        if (-not $AcceptFingerprintChange) { Write-Host 'These changes can move the measurements by themselves. Undo them, or start again with -AcceptFingerprintChange to go on and have them recorded.' -ForegroundColor Red; exit 3 }
    }
    $state.fingerprintChanges = @($state.fingerprintChanges) + [pscustomobject]@{ at = (Get-Date).ToString('o'); against = $refName; blocking = $d.blocking; notes = $d.notes; accepted = [bool]$AcceptFingerprintChange }
}

$auto = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).AutoAdminLogon
if ("$auto" -ne '1' -and -not $Simulate) {
    Write-Host 'Auto-logon is off. After each restart the PC would wait at the sign-in screen, so nothing would be measured while you are away.' -ForegroundColor Red
    Write-Host 'Turn it on for the experiment period (see "one-time preparation" in lab\README.md), then start the session again.' -ForegroundColor Red
    exit 4
}
if ($AllowInstall -and -not $Simulate) {
    & "$env:SystemRoot\System32\schtasks.exe" /Query /TN '\PcaLab\Install' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'The install task \PcaLab\Install is not registered. Run lab\setup-once.ps1 as administrator first.' -ForegroundColor Red; exit 5 }
}

# ---- go ----------------------------------------------------------------------------------------------------------------------
$state.session = [pscustomobject]@{ id = ('session-' + (Get-Date).ToString('MMdd-HHmm')); started = (Get-Date).ToString('o'); until = $end.ToString('o'); allowInstall = [bool]$AllowInstall; active = $true; simulate = [bool]$Simulate; ended = $null; endReason = $null }
$state.stopRequested = $false
Remove-Item -LiteralPath (Join-Path $Lab.Runs 'STOP') -ErrorAction SilentlyContinue
Save-LabState $Lab $state
Write-LabLog $Lab ("SESSION START {0}: until {1}, allowInstall={2}, phase {3}" -f $state.session.id, $end.ToString('MM-dd HH:mm'), [bool]$AllowInstall, $state.phase)
if ($Simulate) { Write-Host 'simulated session registered (no restart, no logon launcher).'; exit 0 }

$startupFile = Join-Path ([Environment]::GetFolderPath('Startup')) 'pca-lab-run-next.cmd'
[IO.File]::WriteAllText($startupFile, "@echo off`r`ntitle PCA lab`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\run-next.ps1`"`r`n", (New-Object Text.ASCIIEncoding))
Write-Host "The PC restarts in 30 seconds and measures until $($end.ToString('MM-dd HH:mm')). Close your work now. Cancel: lab\stop-session.ps1" -ForegroundColor Green
& "$env:SystemRoot\System32\shutdown.exe" /r /t 30 /c "PCA lab session: restarting to start measuring. Cancel with lab\stop-session.ps1"
