# Prints where the experiment stands and what the data say so far. Reads only lab\runs; writes runs\report.md.
#   lab\analyze.ps1 [-RunsRoot <dir>]
param([string]$RunsRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"; . "$lib\LabAnalyze.ps1"
$Lab = Get-LabContext $RunsRoot
$state = Read-LabState $Lab
$out = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $out.Add($s); Write-Host $s }

Say "# Lab report ($((Get-Date).ToString('yyyy-MM-dd HH:mm')))"
if ($state) {
    Say ''
    Say ("experiment {0}, phase **{1}**; boots complete: pilot {2}, before-install {3}, after-install {4}; restarts {5}" -f $state.experiment, $state.phase, (Get-LabBootCount $state 'pilot'), (Get-LabBootCount $state 's0'), (Get-LabBootCount $state 's1'), $state.reboots)
    if ($state.plan) { Say ("plan: k = {0} boots per state, {1} runs per main cell, {2} per secondary cell (source: {3})" -f $state.plan.k, $state.plan.reps_main, $state.plan.reps_sub, $state.plan.source) }
    if ($state.lastMessage) { Say "last message: $($state.lastMessage)" }
    $bad = @($state.boots | Where-Object { $_.status -ne 'complete' })
    if ($bad.Count) { Say ("blocks that did not complete: " + (($bad | ForEach-Object { "$($_.id) ($($_.status))" }) -join ', ')) }
}
$root = Join-Path $Lab.Runs 'boots'
$all = if (Test-Path -LiteralPath $root) { @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'run-*.json' | ForEach-Object { Read-LabJson $_.FullName }) } else { @() }
$real = @($all | Where-Object { -not $_.warmup })
Say ''
Say ("runs on disk: {0} (warm-ups {1}); errors {2}; excluded {3}" -f $all.Count, @($all | Where-Object warmup).Count, @($real | Where-Object { $_.status -ne 'ok' }).Count, @($real | Where-Object { @($_.contamination).Count }).Count)
$reasons = $real | ForEach-Object { $_.contamination } | Where-Object { $_ } | Group-Object | Sort-Object Count -Descending
foreach ($r in $reasons) { Say "  excluded for: $($r.Name) x $($r.Count)" }
$b2 = @($real | Where-Object { $_.background -eq 'B2' -and $_.phase -eq 's1' })
if ($b2.Count) { Say ("after-install bank-page runs where a security module process was seen: {0} of {1}" -f @($b2 | Where-Object { @($_.backgroundCheck.securityModules).Count }).Count, $b2.Count) }

# environment: every block's fingerprint is taken at the same point after its boot, so blocks of one state must agree
foreach ($phase in 's0', 's1') {
    $fps = @(); if (Test-Path -LiteralPath $root) { $fps = @(Get-ChildItem -LiteralPath $root -Directory -Filter "$phase-*" | Sort-Object Name | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'fingerprint.json') }) }
    if ($fps.Count -lt 2) { continue }
    $first = Read-LabJson (Join-Path $fps[0].FullName 'fingerprint.json'); $diffs = 0
    foreach ($d in ($fps | Select-Object -Skip 1)) {
        $c = Compare-LabFingerprint $first (Read-LabJson (Join-Path $d.FullName 'fingerprint.json'))
        foreach ($b in $c.blocking) { Say "  environment: $($d.Name) differs from $($fps[0].Name): $b"; $diffs++ }
    }
    if (-not $diffs) { Say ("environment ({0}): OS {1}, browser {2}, GPU drivers, loaded drivers and services are the same in all {3} blocks" -f $phase, $first.os, $first.browser, $fps.Count) }
}

foreach ($phase in 'pilot', 's0', 's1', 'test') {
    $t = Get-LabBootTable $Lab $phase
    if (-not $t) { continue }
    Say ''
    Say "## $phase : per-boot medians"
    Say '| cell | metric | boots | mean | spread between boots (CV %) |'
    Say '|---|---|---|---|---|'
    foreach ($r in ($t | Sort-Object app, background, metric)) { Say ("| {0}/{1} | {2} | {3} | {4:N3} | {5:N2} |" -f $r.app, $r.background, $r.metric, $r.boots, $r.mean, $r.cvBootsPct) }
}
$fx = Get-LabEffects $Lab
if ($fx) {
    Say ''
    Say '## before vs after install (per-boot values, 95% interval; conservative degrees of freedom)'
    Say '| cell | metric | before | after | change % | interval % | excludes 0 |'
    Say '|---|---|---|---|---|---|---|'
    foreach ($r in ($fx | Sort-Object cell, metric)) { Say ("| {0} | {1} | {2} | {3} | {4:+0.00;-0.00} | {5:+0.00;-0.00} .. {6:+0.00;-0.00} | {7} |" -f $r.cell, $r.metric, $r.before, $r.after, $r.changePct, $r.ciLowPct, $r.ciHighPct, $(if ($r.excludesZero) { 'yes' } else { 'no' })) }
    Say ''
    Say 'Read with the pre-registered rule (docs/security-impact-design.md 6): an effect counts only if its interval excludes 0 and the same direction shows in at least two of the three apps.'
}
if (-not (Test-Path -LiteralPath $Lab.Runs)) { New-Item -ItemType Directory -Force $Lab.Runs | Out-Null }
[IO.File]::WriteAllLines((Join-Path $Lab.Runs 'report.md'), $out, (New-Object Text.UTF8Encoding($false)))
