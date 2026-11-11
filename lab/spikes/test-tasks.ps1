# Runs each foreground task once (or -Runs times) outside the state machine and prints its metrics. Windows come to
# the front and Notepad++ receives scripted typing, so run it when nobody is using the PC.
#   powershell -ExecutionPolicy Bypass -File lab\spikes\test-tasks.ps1 [-Apps blender,godot,notepadpp] [-Runs 1] [-Backgrounds]
param([string]$Apps = 'blender,godot,notepadpp', [int]$Runs = 1, [switch]$Backgrounds)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lib = Join-Path (Split-Path -Parent $PSScriptRoot) 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"; . "$lib\LabTasks.ps1"
$Lab = Get-LabContext (Join-Path $env:LOCALAPPDATA 'Temp\pca-lab-test-runs')
"work: $($Lab.Work)"
$quiet0 = [LabNative]::LastInputTick(); Start-Sleep -Seconds 3
"input quiet for 3 s: " + ([LabNative]::LastInputTick() -eq $quiet0)
foreach ($app in ($Apps -split ',')) {
    for ($i = 0; $i -lt $Runs; $i++) {
        [LabNative]::ParkMouse()
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $m = switch ($app) { 'blender' { Invoke-LabBlender $Lab } 'godot' { Invoke-LabGodot $Lab } 'notepadpp' { Invoke-LabNotepad $Lab } }
        "===== $app run $i ({0:N1} s) =====" -f $sw.Elapsed.TotalSeconds
        $m | ConvertTo-Json -Depth 5 -Compress
    }
}
if ($Backgrounds) {
    foreach ($kind in 'B1', 'B2', 'B3') {
        $bg = Start-LabBackground $Lab $kind
        Start-Sleep -Seconds 30
        "===== background $kind after 30 s ====="
        Test-LabBackground $Lab $bg | ConvertTo-Json -Depth 4 -Compress
        Stop-LabBackground $Lab $bg
        "lab chrome left after stop: " + @(Get-LabChromeProcesses $Lab).Count
        Start-Sleep -Seconds 2
    }
    "user's own chrome still running: " + @(Get-Process chrome -ErrorAction SilentlyContinue).Count
}
