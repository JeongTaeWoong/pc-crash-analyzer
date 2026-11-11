# Background sampler: once a second writes machine CPU and the watched (or busy) processes as one JSON line.
#   {"t":"<ISO time>","cpu":<machine %>,"p":[["name",cpuPct,privateMB],...]}
# Stops when -StopFile appears. Read-only; its own cost was 7 ms per sample on 2026-11-10.
param([string]$Out, [string]$StopFile, [string]$Watch, [int]$IntervalMs = 1000)
. (Join-Path $PSScriptRoot 'LabNative.ps1')
$cores = [Environment]::ProcessorCount
$w = New-Object IO.StreamWriter $Out, $false, (New-Object Text.UTF8Encoding($false))
$w.AutoFlush = $true
$prev = @{}; foreach ($r in [LabNative]::Snapshot()) { $prev[$r.Pid] = $r }
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    while (-not (Test-Path -LiteralPath $StopFile)) {
        Start-Sleep -Milliseconds $IntervalMs
        $secs = $sw.Elapsed.TotalSeconds; $sw.Restart()
        $cur = @{}; $rows = New-Object System.Collections.ArrayList; $idle = 0.0
        $agg = @{}
        foreach ($r in [LabNative]::Snapshot()) {
            $cur[$r.Pid] = $r
            $p = $prev[$r.Pid]
            if (-not $p -or $p.Name -ne $r.Name) { continue }
            $pct = ($r.CpuTicks - $p.CpuTicks) / 1e7 / $secs / $cores * 100
            if ($r.Pid -eq 0) { $idle = $pct; continue }
            if ($pct -ge 0.5 -or $r.Name -match $Watch) {
                if (-not $agg.ContainsKey($r.Name)) { $agg[$r.Name] = @(0.0, 0.0) }
                $agg[$r.Name][0] += $pct; $agg[$r.Name][1] += $r.PrivateBytes / 1MB
            }
        }
        foreach ($k in $agg.Keys) { if ($agg[$k][0] -ge 0.05 -or $k -match $Watch) { [void]$rows.Add(@($k, [math]::Round($agg[$k][0], 2), [math]::Round($agg[$k][1], 0))) } }
        $w.WriteLine((@{ t = (Get-Date).ToString('o'); cpu = [math]::Round([math]::Max(0, 100 - $idle), 2); p = @($rows) } | ConvertTo-Json -Compress -Depth 4))
        $prev = $cur
    }
}
finally { $w.Dispose() }
