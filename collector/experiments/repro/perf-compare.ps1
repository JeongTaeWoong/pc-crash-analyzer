# Summarizes perf-measure.ps1 results in time order, each compared with the first run. Writes a Markdown table next to the data.
param([string]$Dir = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs\perf')
$ErrorActionPreference = 'Stop'
$runs = @(Get-ChildItem $Dir -Filter *.json | Sort-Object LastWriteTime | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json })
if ($runs.Count -eq 0) { throw "no runs in $Dir" }
$b = $runs[0]
function Get-Delta($now, $base) {
    if ($null -eq $now) { return '-' }      # step not measured in this run (for example -NoBuild)
    if (-not $base) { return "$now" }
    return ("{0} ({1:+0.0;-0.0;0}%)" -f $now, ((($now - $base) / $base) * 100))
}
$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("| run | at | HVCI | idle CPU % | committed MB | nonpaged MB | procs | svc | drv | root certs | create ms | read ms | delete ms | spawn ms | build ms | watched processes (CPU%/MB) |")
[void]$lines.Add("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
foreach ($r in $runs) {
    $w = (@($r.idle.watched) | Where-Object { $_ } | ForEach-Object { "$($_.name) $($_.cpu)/$($_.privMB)" }) -join ', '
    [void]$lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9}/{10} | {11} | {12} | {13} | {14} | {15} | {16} |" -f $r.label, $r.at, $r.facts.hvciRunning, $r.idle.cpuPct, (Get-Delta $r.idle.committedMB $b.idle.committedMB), (Get-Delta $r.idle.poolNonpagedMB $b.idle.poolNonpagedMB), $r.facts.processes, $r.facts.servicesRunning, $r.facts.driversRunning, $r.facts.rootCertsMachine, $r.facts.rootCertsUser, (Get-Delta $r.median.createMs $b.median.createMs), (Get-Delta $r.median.readMs $b.median.readMs), (Get-Delta $r.median.deleteMs $b.median.deleteMs), (Get-Delta $r.median.spawnMs $b.median.spawnMs), (Get-Delta $r.median.buildMs $b.median.buildMs), $w))
}
$out = Join-Path (Split-Path $Dir -Parent) 'perf-summary.md'
[IO.File]::WriteAllLines($out, [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
$lines | ForEach-Object { Write-Host $_ }
Write-Host "[compare] $out"
