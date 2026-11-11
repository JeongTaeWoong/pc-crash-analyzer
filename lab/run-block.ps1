# One measurement block = everything measured in one boot. Normally started by run-next.ps1, right after a reboot.
#   run-block.ps1 -BootId s0-01 -Phase s0 -RepsMain 3 -RepsSub 2
# Exit codes: 0 complete, 2 preflight refused to start, 4 stopped on request, 1 error.
# -Test: short waits, no post-boot wait, games/launchers only warned about. For checking the plumbing, not for data.
param(
    [Parameter(Mandatory = $true)][string]$BootId,
    [ValidateSet('pilot', 's0', 's1', 'test')][string]$Phase = 'test',
    [int]$RepsMain = 1, [int]$RepsSub = 1,
    [string]$RunsRoot, [string]$Experiment = 'adhoc', [string]$Apps, [string]$BootTime,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$lib = Join-Path $PSScriptRoot 'lib'
. "$lib\LabNative.ps1"; . "$lib\LabCommon.ps1"; . "$lib\LabTasks.ps1"

$Lab = Get-LabContext $RunsRoot
$t = $Lab.Config.timing
$settle = if ($Test) { 5 } else { [int]$t.background_settle_sec }
$rest = if ($Test) { 2 } else { [int]$t.rest_sec }
$bootDir = Join-Path $Lab.Runs "boots\$BootId"
$summaryPath = Join-Path $bootDir 'summary.json'
$stopFlag = Join-Path $Lab.Runs 'STOP'
if (Test-Path -LiteralPath $summaryPath) { $s = Read-LabJson $summaryPath; if ($s.status -eq 'complete') { Write-LabLog $Lab "[$BootId] already complete"; exit 0 } }
New-Item -ItemType Directory -Force $bootDir | Out-Null
Write-LabLog $Lab "[$BootId] block start (phase $Phase, reps main $RepsMain sub $RepsSub$(if ($Test) { ', TEST' }))"

# ---- preflight -----------------------------------------------------------------------------------------------------------
$pre = Test-LabPreflight $Lab
if ($Test) { $pre.warnings += $pre.blocking; $pre.blocking = @() }
Write-LabJson (Join-Path $bootDir 'preflight.json') $pre
foreach ($w in $pre.warnings) { Write-LabLog $Lab "[$BootId] warning: $w" }
if ($pre.blocking.Count) { foreach ($b in $pre.blocking) { Write-LabLog $Lab "[$BootId] BLOCKED: $b" }; exit 2 }

$sampler = $null
$started = Get-Date
try {
    Remove-Item -LiteralPath $Lab.ChromeProfile -Recurse -Force -ErrorAction SilentlyContinue      # fresh browser profile every boot
    $idle = if ($Test) { @{ stable = $true; cpu = @(); waitedSec = 0 } } else { Wait-LabIdle $Lab ([int]$t.post_boot_wait_sec) }
    Write-LabLog $Lab "[$BootId] idle wait $($idle.waitedSec) s, stable=$($idle.stable), cpu $($idle.cpu -join '/')"
    $fpNow = Get-LabFingerprint $Lab
    Write-LabJson (Join-Path $bootDir 'fingerprint.json') $fpNow
    # same point after a boot as the previous block of this phase: any difference is recorded and reported, not fatal
    $fpDiff = $null
    try {
        $prev = Get-LabLastBootFingerprint $Lab $Phase $BootId
        if ($prev) {
            $d = Compare-LabFingerprint $prev.fingerprint $fpNow
            $fpDiff = [ordered]@{ against = $prev.id; blocking = @($d.blocking); notes = @($d.notes) }
            Write-LabJson (Join-Path $bootDir 'fingerprint-diff.json') $fpDiff
            foreach ($b in $d.blocking) { Write-LabLog $Lab "[$BootId] CHANGED since $($prev.id): $b" }
        }
    } catch { Write-LabLog $Lab "[$BootId] fingerprint comparison failed: $($_.Exception.Message)" }
    $sampler = Start-LabSampler $Lab (Join-Path $bootDir 'sampler.jsonl')

    # ---- schedule: one warm-up per app, then every cell in an order fixed by the boot id --------------------------------
    $appList = if ($Apps) { @($Apps -split ',') } else { @($Lab.Config.apps) }
    $cells = @()
    foreach ($a in $appList) {
        foreach ($b in $Lab.Config.main_backgrounds) { for ($i = 0; $i -lt $RepsMain; $i++) { $cells += , @($a, $b) } }
        foreach ($b in $Lab.Config.sub_backgrounds) { for ($i = 0; $i -lt $RepsSub; $i++) { $cells += , @($a, $b) } }
    }
    $md5 = [Security.Cryptography.MD5]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("$Experiment|$BootId"))
    $seed = [BitConverter]::ToInt32($md5, 0)
    $rng = New-Object Random $seed
    for ($i = $cells.Count - 1; $i -gt 0; $i--) { $j = $rng.Next($i + 1); $tmp = $cells[$i]; $cells[$i] = $cells[$j]; $cells[$j] = $tmp }
    $schedule = @(); foreach ($a in $appList) { $schedule += , @($a, 'B0', $true) }; foreach ($c in $cells) { $schedule += , @($c[0], $c[1], $false) }
    Write-LabJson (Join-Path $bootDir 'schedule.json') @{ seed = $seed; runs = @($schedule | ForEach-Object { "$($_[0])/$($_[1])$(if ($_[2]) { ' warmup' })" }) }

    $done = 0; $excluded = 0; $errors = 0; $durations = @()
    $lastStartOf = @{}      # app -> start of its previous run in this boot (for the definition-update rule)
    for ($n = 0; $n -lt $schedule.Count; $n++) {
        if (Test-Path -LiteralPath $stopFlag) { Write-LabLog $Lab "[$BootId] stop requested"; Write-LabJson $summaryPath @{ status = 'stopped'; bootId = $BootId; at = (Get-Date).ToString('o') }; exit 4 }
        $app, $bgKind, $warm = $schedule[$n]
        $runPath = Join-Path $bootDir ('run-{0:D3}.json' -f $n)
        if (Test-Path -LiteralPath $runPath) { $done++; continue }       # finished before an interruption
        [LabNative]::ParkMouse()
        $tick0 = [LabNative]::LastInputTick(); $Lab.LastScriptInputTick = $tick0
        $runStart = Get-Date
        $rec = [ordered]@{ runId = "$BootId/$n"; experiment = $Experiment; bootId = $BootId; phase = $Phase; index = $n; app = $app; background = $bgKind; warmup = [bool]$warm; start = $runStart.ToString('o'); test = [bool]$Test }
        $bg = $null
        try {
            $max = [int]$Lab.Config.backgrounds.B2.max_loads_per_boot
            if ($bgKind -eq 'B2' -and $Lab.BankLoads + @($Lab.Config.backgrounds.B2.urls).Count -gt $max) { throw "bank page load cap ($max) reached" }
            $bg = Start-LabBackground $Lab $bgKind
            Start-Sleep -Seconds $settle
            $rec.backgroundCheck = Test-LabBackground $Lab $bg
            $taskStart = Get-Date
            $rec.metrics = switch ($app) { 'blender' { Invoke-LabBlender $Lab } 'godot' { Invoke-LabGodot $Lab } 'notepadpp' { Invoke-LabNotepad $Lab } default { throw "unknown app $app" } }
            $taskEnd = Get-Date
            $rec.taskSec = [math]::Round(($taskEnd - $taskStart).TotalSeconds, 2)
            $rec.sampler = Get-LabSamplerSummary $sampler.Out $taskStart $taskEnd
            $rec.status = 'ok'
        }
        catch { $rec.status = 'error'; $rec.error = "$($_.Exception.Message)"; $errors++ }
        finally { if ($bg) { Stop-LabBackground $Lab $bg } }
        $runEnd = Get-Date
        $rec.end = $runEnd.ToString('o')
        $rec.contamination = @(Get-LabContamination $Lab $runStart $runEnd $tick0)
        # Defender took new definitions since this app last ran in this boot: this launch is scanned again, like a first launch
        try {
            $since = if ($lastStartOf.ContainsKey($app)) { $lastStartOf[$app] } else { $started }
            $defs = @(Get-LabDefinitionUpdates $since $runEnd)
            if ($defs.Count) { $rec.contamination += 'first launch after a Defender definition update'; $rec.definitionUpdateAt = $defs[0].ToString('o') }
        } catch { }
        $lastStartOf[$app] = $runStart
        if ($Test) { $rec.contamination = @($rec.contamination | Where-Object { $_ -notlike 'forbidden process*' }) }
        if ($rec.backgroundCheck -and -not $rec.backgroundCheck.ok) { $rec.contamination += 'background did not start' }
        if ($rec.contamination.Count) { $excluded++ }
        Write-LabJson $runPath $rec
        $done++; $durations += ($runEnd - $runStart).TotalSeconds
        Write-LabLog $Lab ("[{0}] run {1}/{2} {3}/{4}{5}: {6} in {7:N0} s{8}" -f $BootId, ($n + 1), $schedule.Count, $app, $bgKind, $(if ($warm) { ' (warm-up)' }), $rec.status, ($runEnd - $runStart).TotalSeconds, $(if ($rec.contamination.Count) { ' EXCLUDED: ' + ($rec.contamination -join '; ') }))
        Start-Sleep -Seconds $rest
    }
    $status = if ($errors -gt [math]::Floor($schedule.Count / 2)) { 'failed' } else { 'complete' }
    Write-LabJson $summaryPath ([ordered]@{ status = $status; bootId = $BootId; phase = $Phase; experiment = $Experiment; bootTime = $(if ($BootTime) { $BootTime } else { Get-LabBootTime }); started = $started.ToString('o'); finished = (Get-Date).ToString('o'); runs = $schedule.Count; done = $done; excluded = $excluded; errors = $errors; meanRunSec = $(if ($durations.Count) { [math]::Round(($durations | Measure-Object -Average).Average, 1) } else { 0 }); repsMain = $RepsMain; repsSub = $RepsSub; idle = $idle; test = [bool]$Test })
    Write-LabLog $Lab "[$BootId] block $status ($done runs, $excluded excluded, $errors errors, $([math]::Round(((Get-Date) - $started).TotalMinutes, 1)) min)"
    if ($status -eq 'complete') { exit 0 } else { exit 1 }
}
catch { Write-LabLog $Lab "[$BootId] block error: $($_.Exception.Message)"; Write-LabJson $summaryPath @{ status = 'failed'; bootId = $BootId; error = "$($_.Exception.Message)"; at = (Get-Date).ToString('o') }; exit 1 }
finally { Stop-LabSampler $sampler; Get-LabChromeProcesses $Lab | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
