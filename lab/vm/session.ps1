# Unattended VM session: runs the jobs of a plan one after another while nobody uses the PC, and leaves every lab VM off. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\bundles.json -Until 07:30
#   powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\bundles.json -Hours 3
#   powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\bundles.json -Hours 3 -DryRun     # only show what would run
#
# A job (plan file, "jobs" list):  name (clone name), spec (hardware profile), gpu (optional: igpu | nvidia),
#   bundle (programs to install), save (checkpoint label to keep the result under),
#   from (optional, existing clone only: go back to this checkpoint first - used to install what failed the first time
#   and keep the result under a new label).
# A case job:  name, case (scenario of scenarios.json), from (checkpoint the case starts from and is put back to),
#   open (optional: programs left open during the scenario), repeat (optional: run even if a finished run exists),
#   alias (optional: aliases.json id or "auto" - the fault program runs under an ordinary name), decoys (optional:
#   number of other alias programs that only sit there). Alias runs go to <name>-<case>+alias.
#   It calls case.ps1; results go to <vm root>\cases\<name>-<case>\<time>\.
# For each job: make the clone if missing -> [attach GPU, copy the host driver, restart] -> install the bundle -> shut down
#   -> checkpoint. A job whose checkpoint already exists is skipped, so a plan can be run again night after night.
# Stops starting new jobs when less than -MinMinutes remain. Keeps the host awake while it runs (no setting is changed:
#   the request ends with this process). Everything is logged under <vm root>\sessions\<start time>\.
# Jobs run one at a time on purpose: simple to follow in the log and nothing fights for the disk. Parallel runs are for later.
param(
    [Parameter(Mandatory = $true)][string]$Plan,
    [string]$Until,
    [double]$Hours = 0,
    [int]$MinMinutes = 25,
    [switch]$DryRun,
    [string]$Root
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
Assert-HyperV
Assert-NoLabSession
if (-not (Test-Path $Plan)) { throw "Plan not found: $Plan" }
$planObj = Get-Content $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
$jobs = @($planObj.jobs)
if (-not $jobs.Count) { throw 'The plan has no jobs.' }

$start = Get-Date
if ($Until) {
    $t = [datetime]::ParseExact($Until, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)
    $deadline = $start.Date.AddHours($t.Hour).AddMinutes($t.Minute)
    if ($deadline -le $start) { $deadline = $deadline.AddDays(1) }
} elseif ($Hours -gt 0) { $deadline = $start.AddHours($Hours) }
else { throw 'Give -Until HH:mm or -Hours N.' }

$dir = Join-Path (Join-Path $cfg.root 'sessions') $start.ToString('yyyyMMdd-HHmmss')
function Log([string]$m) { $line = "{0} {1}" -f (Get-Date).ToString('HH:mm:ss'), $m; Write-Host $line; if (-not $DryRun) { Add-Content -Path (Join-Path $dir 'session.log') -Value $line -Encoding UTF8 } }
# A long-lived PowerShell session can show a VM state that another process changed seconds ago: ask a fresh process.
function Get-FreshState([string]$vmName) { return (& powershell.exe -NoProfile -Command "(Get-VM -Name '$vmName' -ErrorAction SilentlyContinue).State" | Out-String).Trim() }
function Run([string]$script, [string[]]$a, [string]$logName) {
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $script)) + $a
    if ($Root) { $all += @('-Root', $Root) }
    $out = & powershell.exe @all 2>&1 | Out-String
    $code = $LASTEXITCODE
    Add-Content -Path (Join-Path $dir $logName) -Value ("--- {0} {1} {2} (exit {3})`r`n{4}" -f (Get-Date).ToString('HH:mm:ss'), $script, ($a -join ' '), $code, $out.Trim()) -Encoding UTF8
    return [pscustomobject]@{ Code = $code; Out = $out.Trim() }
}
# The PC is also used for work and play (sessions may run in the daytime, 2026-11-14): no job is started while a game
# or its anti-cheat runs on the host (vm.json host_wait_processes). The session waits and goes on when they are gone.
function Get-HostBusyProcess {
    $names = @($cfg.host_wait_processes | Where-Object { $_ })
    if (-not $names.Count) { return $null }
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        foreach ($n in $names) { if ($p.ProcessName -like "$n") { return $p.ProcessName } }
    }
    return $null
}
function Wait-HostFree {
    $t = Get-Date
    $said = $false
    while ((Get-Date) -lt $deadline) {
        $b = Get-HostBusyProcess
        if (-not $b) { break }
        if (-not $said) { Log "the host is busy ($b is running): waiting, no job is started"; $said = $true }
        Start-Sleep -Seconds 60
    }
    return [int][math]::Round(((Get-Date) - $t).TotalMinutes)
}
function Wait-Off([string]$vmName, [int]$sec) {
    $d = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $d) { if ((Get-FreshState $vmName) -eq 'Off') { return $true }; Start-Sleep -Seconds 4 }
    return $false
}

Write-Host ("Plan {0}: {1} job(s). Ends by {2}." -f $Plan, $jobs.Count, $deadline.ToString('MM-dd HH:mm'))
if ($DryRun) {
    foreach ($j in $jobs) {
        $vmName = Get-CloneVmName $cfg $j.name
        $exists = [bool](Get-VM -Name $vmName -ErrorAction SilentlyContinue)
        $done = $exists -and $j.save -and [bool](Get-VMSnapshot -VMName $vmName -Name $j.save -ErrorAction SilentlyContinue)
        if ($j.case) {
            $tag = $(if ($j.alias -or $j.decoys) { '+alias' } else { '' })
            $made = [bool](Get-ChildItem (Join-Path (Join-Path $cfg.root 'cases') "$($j.name)-$($j.case)$tag") -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'result.json') })
            if ($j.id) { $made = Test-Path (Join-Path (Join-Path (Join-Path $cfg.root 'cases') '_done') "$($j.id).txt") }
            Write-Host ("  {0,-12} case {1,-19} from {2,-15} open {3,-28} {4}" -f $j.name, "$($j.case)$tag", $j.from, (@($j.open) -join ','), $(if (-not $exists) { 'clone missing (made by an earlier job?)' } elseif ($made) { 'has a run already (skipped if it was ok)' } else { 'would run' }))
            continue
        }
        Write-Host ("  {0,-12} spec {1,-11} gpu {2,-6} bundle {3,-11} -> checkpoint {4,-16} {5}" -f $j.name, $j.spec, $(if ($j.gpu) { $j.gpu } else { '-' }), $j.bundle, $j.save, $(if ($done) { 'already done, would skip' } elseif ($exists) { 'clone exists, would continue' } else { 'would create' }))
    }
    exit 0
}

# One session at a time: a session turns every lab VM off when it ends, so a second one would lose its clone mid-case.
$lockFile = Join-Path $cfg.root 'session.lock'
if (Test-Path $lockFile) {
    $held = $null
    try { $held = Get-Content $lockFile -Raw -Encoding ASCII | ConvertFrom-Json } catch { }
    if ($held -and $held.pid) {
        $hp = Get-Process -Id ([int]$held.pid) -ErrorAction SilentlyContinue
        if ($hp -and $hp.StartTime.Ticks -eq [long]$held.ticks) { throw "Another session is running (pid $($held.pid), plan $($held.plan), since $($held.since)). Wait for it to end, or stop that process." }
    }
}
[ordered]@{ pid = $PID; ticks = (Get-Process -Id $PID).StartTime.Ticks; plan = "$Plan"; since = $start.ToString('yyyy-MM-dd HH:mm') } | ConvertTo-Json -Compress | Set-Content -Path $lockFile -Encoding ASCII

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Add-Type -Namespace PcaLab -Name Awake -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[void][PcaLab.Awake]::SetThreadExecutionState([uint32]"0x80000001")          # ES_CONTINUOUS | ES_SYSTEM_REQUIRED
$summary = @()
try {
    Log "session start, plan $Plan, deadline $($deadline.ToString('MM-dd HH:mm')), log folder $dir"
    foreach ($j in $jobs) {
        $vmName = Get-CloneVmName $cfg $j.name
        $jobLog = "job-$($j.name).log"
        $left = [int]($deadline - (Get-Date)).TotalMinutes
        $res = [pscustomobject]@{ name = $j.name; bundle = $j.bundle; result = ''; detail = ''; minutes = 0 }
        $waited = Wait-HostFree
        if ($waited -gt 0) { Log "waited $waited min for the host"; $left = [int]($deadline - (Get-Date)).TotalMinutes }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
            if ($j.case) {
                # a case job: collector + scenario inside the clone (case.ps1). Done once: a finished run is not repeated
                # unless the job says "repeat": true.
                $res.bundle = "case:$($j.case)" + $(if ($j.alias -or $j.decoys) { '+alias' } else { '' })
                # alias runs (the fault program under an ordinary name, optional decoys) are kept in their own folder
                $caseRoot = Join-Path (Join-Path $cfg.root 'cases') ("$($j.name)-$($j.case)" + $(if ($j.alias -or $j.decoys) { '+alias' } else { '' }))
                $doneRun = Get-ChildItem $caseRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
                    $rj = Join-Path $_.FullName 'result.json'
                    (Test-Path $rj) -and [bool]((Get-Content $rj -Raw -Encoding UTF8 | ConvertFrom-Json).ok)
                } | Select-Object -First 1
                # A job with an "id" is its own case even when the clone and the scenario are the same as another job's
                # (many alias runs of one scenario, added 2026-11-13): it is done when cases\_done\<id>.txt exists.
                $doneMark = $null
                if ($j.id) {
                    $doneMark = Join-Path (Join-Path (Join-Path $cfg.root 'cases') '_done') "$($j.id).txt"
                    $doneRun = $(if (Test-Path $doneMark) { Get-Item $doneMark } else { $null })
                }
                if ($doneRun -and -not $j.repeat) { $res.result = 'skipped'; $res.detail = "case already made: $($doneRun.FullName)"; continue }
                if ($left -lt 15) { $res.result = 'not started'; $res.detail = "$left min left (needs 15)"; continue }
                if (-not $vm) { throw "the clone $($j.name) does not exist (its bundle job must come first)" }
                if (-not $j.from) { throw 'a case job needs "from": the checkpoint the case starts from' }
                if (-not (Get-VMSnapshot -VMName $vmName -Name $j.from -ErrorAction SilentlyContinue)) { throw "the clone has no checkpoint '$($j.from)'" }
                Log "job $($j.name) case $($j.case): start ($left min left)"
                $ca = @('-Name', $j.name, '-Scenario', $j.case, '-From', $j.from)
                if ($j.open) { $ca += @('-Open', (@($j.open) -join ',')) }
                if ($j.alias) { $ca += @('-Alias', "$($j.alias)") }
                if ($j.decoys) { $ca += @('-Decoys', "$([int]$j.decoys)") }
                $r = Run 'case.ps1' $ca $jobLog
                $where = (@($r.Out -split "`n" | Where-Object { $_ -match '^case folder:' }) | Select-Object -Last 1)
                $line = (@($r.Out -split "`n" | Where-Object { $_ -match '^\s*result:|^FAILED:' }) | Select-Object -Last 1)
                $res.result = $(if ($r.Code -eq 0) { 'done' } else { 'failed' })
                $res.detail = ("$line $where" -replace '\s+', ' ').Trim()
                if ($doneMark -and $r.Code -eq 0) {
                    New-Item -ItemType Directory -Force -Path (Split-Path $doneMark -Parent) | Out-Null
                    Set-Content -Path $doneMark -Value "$where" -Encoding ASCII
                }
                continue
            }
            if ($vm -and (Get-VMSnapshot -VMName $vmName -Name $j.save -ErrorAction SilentlyContinue)) { $res.result = 'skipped'; $res.detail = "checkpoint $($j.save) already exists"; continue }
            if ($left -lt $MinMinutes) { $res.result = 'not started'; $res.detail = "$left min left (needs $MinMinutes)"; continue }
            Log "job $($j.name): start ($left min left)"
            if (-not $vm) {
                $r = Run 'clone.ps1' @('-New', $j.name, '-Spec', $j.spec) $jobLog
                if ($r.Code -ne 0) { throw "could not create the clone: $(($r.Out -split "`n")[0])" }
            } elseif ((Get-FreshState $vmName) -ne 'Off') {
                Run 'clone.ps1' @('-Stop', $j.name, '-TurnOff') $jobLog | Out-Null
                if (-not (Wait-Off $vmName 60)) { throw 'the clone was running and could not be turned off' }
            }
            if ($vm -and $j.from) {
                # continue from an earlier checkpoint (e.g. install what failed the first time, keep it under a new label)
                if (-not (Get-VMSnapshot -VMName $vmName -Name $j.from -ErrorAction SilentlyContinue)) { throw "the clone has no checkpoint '$($j.from)'" }
                $r = Run 'clone.ps1' @('-Restore', $j.name, '-Label', $j.from) $jobLog
                if ($r.Code -ne 0) { throw "could not go back to '$($j.from)': $(($r.Out -split "`n")[0])" }
            }
            # a clone that is slowed down on purpose gets longer waits
            $slow = 1
            try { $slow = (Get-LabVmSlowdown $vmName).factor } catch { }
            if ($j.gpu -and -not (Test-LabVmGpu $vmName)) {
                $r = Run 'gpu.ps1' @('-Name', $j.name, '-Attach', '-Gpu', $j.gpu) $jobLog
                if ($r.Code -ne 0) { throw "could not attach the GPU: $(($r.Out -split "`n")[0])" }
            }
            $r = Run 'clone.ps1' @('-Start', $j.name) $jobLog
            if ($r.Code -ne 0) { throw "could not start: $(($r.Out -split "`n")[0])" }
            if (-not (Wait-GuestDesktop $cfg $j.name ([int](420 * $slow)))) { throw "the guest desktop did not come up in $([int](7 * $slow)) minutes" }
            Start-Sleep -Seconds 20                                                   # let the first logon settle
            if ($j.gpu) {
                $r = Run 'gpu.ps1' @('-Name', $j.name, '-InstallDriver') $jobLog
                if ($r.Code -ne 0) { throw "GPU driver copy failed: $(($r.Out -split "`n")[0])" }
                Run 'guest.ps1' @('-Name', $j.name, '-Run', 'Restart-Computer -Force') $jobLog | Out-Null
                Start-Sleep -Seconds 30
                if (-not (Wait-GuestDesktop $cfg $j.name 420)) { throw 'the guest did not come back after the GPU driver restart' }
                Start-Sleep -Seconds 20
            }
            $r = Run 'install.ps1' @('-Name', $j.name, '-Bundle', $j.bundle) $jobLog
            $installOk = ($r.Code -eq 0)
            $failed = @($r.Out -split "`n" | Where-Object { $_ -match '^\s*\[FAIL\]\s+(\S+)' } | ForEach-Object { ($_ -replace '^\s*\[FAIL\]\s+(\S+).*$', '$1').Trim() })
            # "[ok*]" = recorded as failed at first, found in the guest at the end of the install run
            $foundLater = @($r.Out -split "`n" | Where-Object { $_ -match '^\s*\[ok\*\]\s+(\S+)' } | ForEach-Object { ($_ -replace '^\s*\[ok\*\]\s+(\S+).*$', '$1').Trim() })
            $failed = @($failed | Where-Object { $foundLater -notcontains $_ })
            try { Save-VmScreenshot $vmName (Join-Path $dir "$($j.name)-after-install.png") 1280 720 | Out-Null } catch { }
            Run 'clone.ps1' @('-Stop', $j.name) $jobLog | Out-Null
            if (-not (Wait-Off $vmName ([int](300 * $slow)))) { Run 'clone.ps1' @('-Stop', $j.name, '-TurnOff') $jobLog | Out-Null; if (-not (Wait-Off $vmName 60)) { throw 'the clone did not shut down' } }
            $r = Run 'clone.ps1' @('-Save', $j.name, '-Label', $j.save) $jobLog
            if ($r.Code -ne 0) { throw "checkpoint failed: $(($r.Out -split "`n")[0])" }
            if ($installOk) { $res.result = 'done'; $res.detail = "checkpoint $($j.save)" }
            else { $res.result = 'done with failures'; $res.detail = "checkpoint $($j.save); not installed: $($failed -join ', ')" }
        } catch {
            $res.result = 'failed'; $res.detail = $_.Exception.Message
            try { if ((Get-FreshState $vmName) -eq 'Running') { Save-VmScreenshot $vmName (Join-Path $dir "$($j.name)-failed.png") 1280 720 | Out-Null } } catch { }
        } finally {
            $res.minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1)
            $summary += $res
            Log ("job {0}: {1} - {2} ({3} min)" -f $res.name, $res.result, $res.detail, $res.minutes)
        }
    }
} finally {
    # leave nothing running, whatever happened
    foreach ($v in Get-LabVms $cfg) {
        $s = Get-FreshState $v.Name
        if ($s -and $s -ne 'Off') {
            try { Stop-VM -Name $v.Name -Force -ErrorAction Stop } catch { }
            if (-not (Wait-Off $v.Name 180)) { try { Stop-VM -Name $v.Name -TurnOff -Force } catch { } }
            Log "turned off $($v.Name) (was $s)"
        }
    }
    Remove-Item $lockFile -Force -ErrorAction SilentlyContinue
    ConvertTo-Json -InputObject @($summary) -Depth 3 | Set-Content -Path (Join-Path $dir 'summary.json') -Encoding UTF8
    [void][PcaLab.Awake]::SetThreadExecutionState([uint32]"0x80000000")      # ES_CONTINUOUS: drop the stay-awake request
    Log ("session end: {0}" -f (($summary | Group-Object result | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '))
}
$bad = @($summary | Where-Object { $_.result -in 'failed', 'done with failures' }).Count
exit $(if ($bad) { 1 } else { 0 })
