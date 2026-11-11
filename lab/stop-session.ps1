# Stops the running session. Cancels a pending restart, removes the logon launcher and tells the running block to stop
# after the current run. Finished blocks are kept; an unfinished block is measured again in the next session.
param([string]$RunsRoot)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"
$Lab = Get-LabContext $RunsRoot
# "no shutdown was in progress" is the normal case and must not stop this script (found by the 11/10 dry run)
& "$env:SystemRoot\System32\cmd.exe" /c "`"$env:SystemRoot\System32\shutdown.exe`" /a >nul 2>&1"
Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'pca-lab-run-next.cmd') -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $Lab.Runs)) { New-Item -ItemType Directory -Force $Lab.Runs | Out-Null }
[IO.File]::WriteAllText((Join-Path $Lab.Runs 'STOP'), (Get-Date).ToString('o'))
$state = Read-LabState $Lab
if ($state) {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -match 'run-(next|block)\.ps1' }).Count -gt 0
    if ($state.session -and $state.session.active -and -not $running) {
        # nothing is running (for example the PC was waiting between boots): close the session here
        $state.session.active = $false; $state.session.ended = (Get-Date).ToString('o'); $state.session.endReason = 'stopped on request'
        $state.sessions = @($state.sessions) + $state.session; $state.lastMessage = 'stopped on request'
        Remove-Item -LiteralPath (Join-Path $Lab.Runs 'STOP') -ErrorAction SilentlyContinue
    }
    else { $state.stopRequested = $true }
    Save-LabState $Lab $state
    Write-Host ("stop requested. progress: pilot {0}, before-install {1}, after-install {2}, phase {3}" -f (Get-LabBootCount $state 'pilot'), (Get-LabBootCount $state 's0'), (Get-LabBootCount $state 's1'), $state.phase)
    if ($running) { Write-Host 'A block is running: it stops after the current run (up to a minute or two).' }
}
else { Write-Host 'no experiment state found; pending restart cancelled and launcher removed.' }
