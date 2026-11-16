# Copies a large program folder (thousands of files, many of them executables and DLLs) to backup folders with
# robocopy, several times in a row - a person keeping dated copies of a tool before an upgrade.
# Used by the scenario sys-defender-copy (lab/vm/scenarios.json). ASCII only.
#
# The point is not this script: every file a process writes is scanned by Microsoft Defender's real-time protection,
# so MsMpEng.exe works as hard as the copy does. Microsoft describes this and the remedies (exclusions for trusted
# folders, performance analyzer - not turning the protection off):
#   https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-performance-issues
# The script changes no setting. It only copies files.
#
# Exit codes: 0 = all copies made (the lab normally stops it before that), 3 = the source folder is missing.
param(
    [string]$Source = 'C:\Tools\Blender51',
    [string]$DestRoot = 'C:\Work\backup',
    [int]$Copies = 8
)
$ErrorActionPreference = 'Continue'
function Say([string]$text) { Write-Output ("[driver {0}] {1}" -f (Get-Date).ToString('HH:mm:ss'), $text) }

if (-not (Test-Path $Source)) { Say "source folder missing: $Source"; exit 3 }
$rt = Get-MpComputerStatus -ErrorAction SilentlyContinue
Say ("Defender real-time protection: " + $(if ($rt) { "$($rt.RealTimeProtectionEnabled)" } else { 'status not readable' }))
New-Item -ItemType Directory -Force -Path $DestRoot | Out-Null
for ($i = 1; $i -le $Copies; $i++) {
    $dest = Join-Path $DestRoot ('{0}-copy{1}' -f (Split-Path $Source -Leaf), $i)
    $t = Get-Date
    & "$env:SystemRoot\System32\Robocopy.exe" $Source $dest /E /MT:8 /NFL /NDL /NP /NJH /R:0 /W:0 | Select-Object -Last 12
    # robocopy: 0-7 = copied (with or without extras), 8 and above = some files failed
    Say ("copy $i of $Copies ended with robocopy code $LASTEXITCODE after {0:N0} s" -f ((Get-Date) - $t).TotalSeconds)
}
exit 0
