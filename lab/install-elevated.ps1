# The install step of the experiment. Started by the scheduled task \PcaLab\Install with administrator rights
# (registered once by setup-once.ps1); run-next.ps1 triggers it only in a session started with -AllowInstall.
# It runs the protected copy of morning-elevated.ps1 in -Unattended mode and always leaves a result in runs\install.done,
# so the state machine can tell "installed" from "failed" from "never ran".
param([Parameter(Mandatory = $true)][string]$RunsRoot, [string]$ManifestPath)
$ErrorActionPreference = 'Stop'
$marker = Join-Path $RunsRoot 'install.done'
$log = Join-Path $RunsRoot 'install.log'
$utf8 = New-Object Text.UTF8Encoding($false)
function Finish([string]$status, [string]$detail) {
    [IO.File]::WriteAllText($marker, (@{ status = $status; detail = $detail; at = (Get-Date).ToString('o') } | ConvertTo-Json), $utf8)
    exit $(if ($status -eq 'ok') { 0 } else { 1 })
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Finish 'failed' 'not running as administrator' }
    $script = Join-Path $PSScriptRoot 'morning-elevated.ps1'
    if (-not (Test-Path -LiteralPath $script)) { Finish 'failed' "missing $script (run setup-once.ps1 again)" }
    $restore = 'not attempted'
    try { Checkpoint-Computer -Description 'PCA lab: before security modules' -RestorePointType APPLICATION_INSTALL -ErrorAction Stop; $restore = 'created' }
    catch { $restore = "not created: $($_.Exception.Message)" }      # System Protection may be off, or a point was made in the last 24 h
    [IO.File]::AppendAllText($log, "$((Get-Date).ToString('o')) restore point: $restore`r`n", $utf8)
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Unattended')
    if ($ManifestPath) { $a += @('-ManifestPath', $ManifestPath) }
    & powershell @a *>> $log
    $code = $LASTEXITCODE
    $summaryPath = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs\install-summary.json'
    $installed = if (Test-Path -LiteralPath $summaryPath) { @(Get-Content -Raw -Encoding UTF8 -LiteralPath $summaryPath | ConvertFrom-Json | Where-Object { @($_.newPrograms).Count -or $_.newServices -or $_.newDrivers }).Count } else { 0 }
    if ($installed -eq 0) { Finish 'failed' "no program was installed (script exit $code, restore point $restore). See install.log" }
    Finish 'ok' "$installed products changed the system (script exit $code, restore point $restore)"
}
catch { Finish 'failed' "$($_.Exception.Message)" }
