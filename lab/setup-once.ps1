# One-time preparation for unattended sessions. Run it yourself from an ELEVATED PowerShell:
#   powershell -ExecutionPolicy Bypass -File lab\setup-once.ps1            register the install task and show what is left to do
#   powershell -ExecutionPolicy Bypass -File lab\setup-once.ps1 -Remove    take everything out again after the experiment
# What it changes:
#   - copies the install scripts and the installer manifest to C:\Program Files\PcaLab (only administrators can write there),
#     so that the elevated task never runs a file that a normal-rights program could have altered
#   - registers the scheduled task \PcaLab\Install: on demand only, this user, highest privileges
# What it does NOT change (you decide, it only reports): auto-logon, Windows Update pause, Store auto-update, PC BlackBox autostart.
# Status 2026-11-10: written but never run (it needs administrator rights). First real run is yours; tell me what it prints.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated PowerShell.' }
$lab = $PSScriptRoot
$repo = Split-Path -Parent $lab
$protected = Join-Path $env:ProgramFiles 'PcaLab'
$taskPath = '\PcaLab\'; $taskName = 'Install'

if ($Remove) {
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $protected) { Remove-Item -LiteralPath $protected -Recurse -Force }
    Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'pca-lab-run-next.cmd') -ErrorAction SilentlyContinue
    Write-Host 'Removed the task, the protected copies and the logon launcher. Remember to turn auto-logon off and to resume Windows Update.' -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Force $protected | Out-Null
$copies = @(
    "$lab\install-elevated.ps1", "$lab\lib\auto-click.ps1", "$lab\lib\auto-click.words.json",
    "$repo\collector\experiments\repro\morning-elevated.ps1", "$repo\collector\experiments\repro\perf-measure.ps1",
    "$repo\collector\experiments\repro\perf-compare.ps1", "$repo\collector\experiments\repro\conflict-evidence.ps1",
    "$repo\collector\experiments\repro-runs\installers\manifest.json"
)
foreach ($c in $copies) { Copy-Item -LiteralPath $c -Destination $protected -Force; Write-Host ("copied {0}  sha256 {1}" -f (Split-Path -Leaf $c), (Get-FileHash -LiteralPath $c -Algorithm SHA256).Hash.Substring(0, 16).ToLowerInvariant()) }

$runs = Join-Path $lab 'runs'
$arg = '-NoProfile -ExecutionPolicy Bypass -File "{0}\install-elevated.ps1" -RunsRoot "{1}" -ManifestPath "{0}\manifest.json"' -f $protected, $runs
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $arg
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 4)
Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "registered $taskPath$taskName (on demand, highest privileges). It runs only when a session was started with -AllowInstall." -ForegroundColor Green

Write-Host "`nStill to do by hand before the first session:" -ForegroundColor Cyan
$auto = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue).AutoAdminLogon
Write-Host ("  [{0}] auto-logon for the experiment period. Microsoft's Sysinternals Autologon stores the password encrypted; run it, enter the password, Enable. Disable when the experiment is over." -f $(if ("$auto" -eq '1') { 'x' } else { ' ' }))
$ux = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction SilentlyContinue
$paused = $false; if ($ux -and $ux.PauseUpdatesExpiryTime) { try { $paused = [datetime]::Parse($ux.PauseUpdatesExpiryTime).ToLocalTime() -gt (Get-Date) } catch { } }
Write-Host ("  [{0}] pause Windows Update (Settings > Windows Update > Pause). It installed 10 drivers in two weeks on this PC." -f $(if ($paused) { 'x' } else { ' ' }))
Write-Host '  [ ] Microsoft Store > profile > Settings > App updates: off (cannot be read reliably from here).'
$bb = @(Get-ScheduledTask -TaskPath '\PcBlackBox\' -ErrorAction SilentlyContinue | Where-Object { $_.State -ne 'Disabled' })
Write-Host ("  [{0}] PC BlackBox logon task disabled{1}" -f $(if ($bb.Count) { ' ' } else { 'x' }), $(if ($bb.Count) { ":  Disable-ScheduledTask -TaskPath '\PcBlackBox\' -TaskName '$($bb[0].TaskName)'" } else { '' }))
Write-Host '  [ ] NVIDIA app: automatic driver download off. Game launchers closed before each session.'
Write-Host "`nThen:  lab\start-session.ps1 -Until 07:30" -ForegroundColor Green
