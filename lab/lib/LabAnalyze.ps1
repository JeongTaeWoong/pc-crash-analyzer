# Reading run files and the pre-registered arithmetic (docs/security-impact-design.md section 6). Dot-source after LabCommon.ps1.

# The metrics judged per app. Everything else in a run file is reported but not used to size or judge the experiment.
$script:LabPrimaryMetrics = @{
    blender   = @('gui_start_ms', 'bench_wall_s')
    godot     = @('gui_start_ms', 'import_s')
    notepadpp = @('start_ms', 'big_open_ms', 'type.p95_ms')
}
$script:LabT975 = @{ 1 = 12.706; 2 = 4.303; 3 = 3.182; 4 = 2.776; 5 = 2.571; 6 = 2.447; 7 = 2.365; 8 = 2.306; 9 = 2.262; 10 = 2.228 }
function Get-LabT([int]$df) { if ($df -lt 1) { return [double]::PositiveInfinity }; if ($script:LabT975.ContainsKey($df)) { return $script:LabT975[$df] }; return 2.0 }

function Get-LabMetric($Run, [string]$Name) {
    $v = $Run.metrics
    foreach ($part in $Name -split '\.') { if ($null -eq $v) { return $null }; $v = $v.$part }
    if ($v -is [double] -or $v -is [int] -or $v -is [long] -or $v -is [decimal]) { return [double]$v }
    return $null     # missing, or an {status:'unavailable'} object
}

function Get-LabStats([double[]]$v) {
    $v = @($v); $n = $v.Count
    if ($n -eq 0) { return $null }
    $mean = ($v | Measure-Object -Average).Average
    $var = if ($n -gt 1) { (($v | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Sum).Sum / ($n - 1) } else { 0.0 }
    return @{ n = $n; mean = $mean; var = $var; sd = [math]::Sqrt($var) }
}
function Get-LabMedian([double[]]$v) { $s = @($v | Sort-Object); if (-not $s.Count) { return $null }; if ($s.Count % 2) { return $s[($s.Count - 1) / 2] }; return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }

# All usable runs: not a warm-up, finished, not contaminated.
function Get-LabRuns($Lab, [string]$Phase) {
    $root = Join-Path $Lab.Runs 'boots'
    if (-not (Test-Path -LiteralPath $root)) { return @() }
    $runs = foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -Filter 'run-*.json') { Read-LabJson $f.FullName }
    return @($runs | Where-Object { -not $_.warmup -and $_.status -eq 'ok' -and @($_.contamination).Count -eq 0 -and (-not $Phase -or $_.phase -eq $Phase) })
}

# One row per phase x cell x metric: the per-boot medians and their spread.
function Get-LabBootTable($Lab, [string]$Phase) {
    $rows = @()
    $runs = Get-LabRuns $Lab $Phase
    foreach ($g in ($runs | Group-Object { "$($_.phase)|$($_.app)|$($_.background)" })) {
        $phase, $app, $bg = $g.Name -split '\|'
        foreach ($metric in $script:LabPrimaryMetrics[$app]) {
            $perBoot = @(); $within = @()
            foreach ($b in ($g.Group | Group-Object bootId)) {
                $vals = @($b.Group | ForEach-Object { Get-LabMetric $_ $metric } | Where-Object { $null -ne $_ })
                if (-not $vals.Count) { continue }
                $perBoot += (Get-LabMedian $vals)
                if ($vals.Count -gt 1) { $within += (Get-LabStats $vals).var }
            }
            if (-not $perBoot.Count) { continue }
            $s = Get-LabStats $perBoot
            $rows += [pscustomobject]@{ phase = $phase; app = $app; background = $bg; metric = $metric; boots = $s.n; mean = $s.mean; sdBoots = $s.sd; cvBootsPct = if ($s.mean) { $s.sd / $s.mean * 100 } else { 0 }; withinVar = if ($within.Count) { ($within | Measure-Object -Average).Average } else { $null }; perBoot = $perBoot }
        }
    }
    return $rows
}

# From the pilot boots: runs per cell per boot (r) and number of boots per state (k).
#   r = ceil( sqrt( (cost of a boot / cost of a run) * (within-boot variance / between-boot variance) ) )   [Kalibera & Jones 2013]
#   k = smallest k with  t(k-1) * CV(boot means) / sqrt(k)  <=  target / sqrt(2)     (the before/after ratio is about sqrt(2) wider)
# The worst primary metric decides. Values are clipped to the limits in lab.json.
function Get-LabPlan($Lab, [double]$BootCostSec, [double]$RunCostSec, [int]$PilotReps) {
    $cfg = $Lab.Config
    $table = Get-LabBootTable $Lab 'pilot'
    $detail = @(); $kNeed = $cfg.k_min; $rNeed = 1
    foreach ($row in $table) {
        if ($row.boots -lt 2 -or -not $row.mean) { continue }
        $tw = if ($null -ne $row.withinVar) { [double]$row.withinVar } else { 0.0 }
        $tb = [math]::Max(0.0, $row.sdBoots * $row.sdBoots - $tw / [math]::Max(1, $PilotReps))
        $r = if ($tb -gt 0 -and $tw -gt 0) { [int][math]::Ceiling([math]::Sqrt(($BootCostSec / $RunCostSec) * ($tw / $tb))) } elseif ($tw -gt 0) { 4 } else { 1 }
        $r = [math]::Max(1, [math]::Min(4, $r))
        $k = $cfg.k_max + 1
        for ($c = 2; $c -le $cfg.k_max; $c++) {
            $cvMeans = [math]::Sqrt($tb + $tw / $r) / $row.mean
            if ((Get-LabT ($c - 1)) * $cvMeans / [math]::Sqrt($c) -le ($cfg.target_half_width / [math]::Sqrt(2))) { $k = $c; break }
        }
        $detail += [pscustomobject]@{ cell = "$($row.app)/$($row.background)"; metric = $row.metric; mean = [math]::Round($row.mean, 3); cvBootsPct = [math]::Round($row.cvBootsPct, 2); r = $r; k = $k }
        if ($k -gt $kNeed) { $kNeed = $k }; if ($r -gt $rNeed) { $rNeed = $r }
    }
    $limited = $kNeed -gt $cfg.k_max
    $k = [math]::Max($cfg.k_min, [math]::Min($cfg.k_max, $kNeed))
    return [pscustomobject]@{
        source = if ($detail.Count) { 'pilot' } else { 'default (no usable pilot data)' }
        k = if ($detail.Count) { $k } else { $cfg.default_plan.k }
        reps_main = if ($detail.Count) { $rNeed } else { $cfg.default_plan.reps_main }
        reps_sub = [math]::Max(1, [math]::Min($cfg.default_plan.reps_sub, $rNeed))
        target_not_reachable = $limited      # true: even k_max boots will not reach the target half-width for some metric
        bootCostSec = $BootCostSec; runCostSec = $RunCostSec; detail = $detail
    }
}

# Before/after comparison per cell and metric, from the per-boot values. Welch interval on the difference of means, shown as a ratio.
function Get-LabEffects($Lab) {
    $a = Get-LabBootTable $Lab 's0'; $b = Get-LabBootTable $Lab 's1'
    $rows = @()
    foreach ($x in $a) {
        $y = $b | Where-Object { $_.app -eq $x.app -and $_.background -eq $x.background -and $_.metric -eq $x.metric } | Select-Object -First 1
        if (-not $y -or $x.boots -lt 2 -or $y.boots -lt 2) { continue }
        $se = [math]::Sqrt($x.sdBoots * $x.sdBoots / $x.boots + $y.sdBoots * $y.sdBoots / $y.boots)
        $df = [math]::Max(1, [math]::Min($x.boots, $y.boots) - 1)          # conservative degrees of freedom
        $half = (Get-LabT $df) * $se
        $diff = $y.mean - $x.mean
        $rows += [pscustomobject]@{ cell = "$($x.app)/$($x.background)"; metric = $x.metric; before = [math]::Round($x.mean, 3); after = [math]::Round($y.mean, 3); changePct = [math]::Round($diff / $x.mean * 100, 2); ciLowPct = [math]::Round(($diff - $half) / $x.mean * 100, 2); ciHighPct = [math]::Round(($diff + $half) / $x.mean * 100, 2); bootsBefore = $x.boots; bootsAfter = $y.boots; excludesZero = (($diff - $half) -gt 0 -or ($diff + $half) -lt 0) }
    }
    return $rows
}
