# One case run in a lab clone: collector inside the guest + a scenario, results copied back to the host. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -List
#   powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -Name office1 -From bundle-office -Scenario baseline
#   powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -Name office1 -From bundle-office -Scenario mem-pressure -Open chrome,libreoffice
#
# -From <checkpoint>: the clone is put back to that checkpoint before the run AND after it, so every case starts from the
#   same state and leaves nothing behind. Without -From the clone must be running already and is left running.
# -Open a,b: programs (apps.json ids) started before the scenario and left open during it (what the user "was doing").
# Result: <vm root>\cases\<name>-<scenario>\<time>\ with result.json (what happened in the guest), scenario.json (what was
#   injected = the answer), dumps\ (what the collector wrote), collector.log, shots\ (screen every 30 s).
#   Exit code 0 = the run can be used as the case it names (a dump of the expected kind, made while the fault was
#   really running; scenario.json says "usable": true), 1 = it cannot.
# Uses the console collector built by payload.ps1; nothing is sent anywhere. Guest programs are started and stopped by
#   guest-case.ps1 - that is lab code, not collector code.
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'List')][switch]$List,
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string]$Name,
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)][string]$Scenario,
    [Parameter(ParameterSetName = 'Run')][string]$From,
    [Parameter(ParameterSetName = 'Run')][string[]]$Open = @(),
    [Parameter(ParameterSetName = 'Run')][int]$WarmupSec = 0,
    [Parameter(ParameterSetName = 'Run')][string]$Alias,         # aliases.json id or 'auto': the fault program runs under an ordinary name
    [Parameter(ParameterSetName = 'Run')][int]$Decoys = 0,       # other alias programs that only sit there (not the culprit)
    [string]$Root
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
$catalog = Get-Content (Join-Path $PSScriptRoot 'apps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$scn = Get-Content (Join-Path $PSScriptRoot 'scenarios.json') -Raw -Encoding UTF8 | ConvertFrom-Json

if ($List) {
    foreach ($p in $scn.scenarios.PSObject.Properties) { Write-Host ("  {0,-14} {1,-7} {2}" -f $p.Name, $p.Value.kind, $p.Value.title) }
    exit 0
}

Assert-HyperV
Assert-NoLabSession
$Open = @($Open | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$def = $scn.scenarios.$Scenario
if (-not $def) { throw "Unknown scenario '$Scenario'. See -List." }
$vmName = Get-CloneVmName $cfg $Name
if (-not (Get-VM -Name $vmName -ErrorAction SilentlyContinue)) { throw "No VM named $vmName." }
$payload = Join-Path (Join-Path $cfg.root 'payload') 'case-payload.zip'
if (-not (Test-Path $payload)) { throw "The payload is not built. Run: lab\vm\payload.ps1" }
$payloadInfo = $null
try { $payloadInfo = Get-Content (Join-Path (Split-Path $payload -Parent) 'payload.json') -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
# 'recipe' scenarios run a real program (an old version with a documented bug, or the user's own heavy job): its
# package must be built before a clone is started for nothing (lab\vm\packages.ps1, packages.json)
$pkgDir = Join-Path (Split-Path $payload -Parent) 'packages'
foreach ($pk in @($def.packages)) { if ($pk -and -not (Test-Path (Join-Path $pkgDir "$pk.zip"))) { throw "Package '$pk' is not built. Run: lab\vm\packages.ps1" } }

function Run-Script([string]$script, [string[]]$a) {
    $all = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $script)) + $a
    if ($Root) { $all += @('-Root', $Root) }
    $o = & powershell.exe @all 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o.Trim() }
}
function Get-FreshState { return (& powershell.exe -NoProfile -Command "(Get-VM -Name '$vmName' -ErrorAction SilentlyContinue).State" | Out-String).Trim() }
# What the guest needs to find and start a program: a file path when we know it, else words to look for in the Start menu.
function Get-AppSpec([string]$id) {
    $a = $catalog.apps.$id
    if (-not $a) { throw "Unknown program '$id' (apps.json)." }
    $exe = $null
    if ($a.kind -eq 'portable') { $exe = "C:\lab\apps\$id\$($a.exe)" } elseif ($a.launch -and $a.launch -ne 'skip') { $exe = $a.launch.Replace('/', [string][char]92) }
    return [pscustomobject]@{ id = $id; name = $a.name; exe = $exe; needles = @($a.launch_name, $a.match, $a.name | Where-Object { $_ }); launch_sec = $a.launch_sec }
}

# Alias runs (-Alias / -Decoys): the fault program is installed under an ordinary name from aliases.json, so that no
# process name, file name, command line or collector tag gives the answer away. Kept apart from the plain runs.
$aliasPool = @()
$useAlias = [bool]$Alias -or $Decoys -gt 0
if ($useAlias) {
    $aliasPool = @((Get-Content (Join-Path $PSScriptRoot 'aliases.json') -Raw -Encoding UTF8 | ConvertFrom-Json).aliases)
    if ($Alias -and $Alias -ne 'auto' -and -not ($aliasPool | Where-Object { $_.id -eq $Alias })) { throw "Unknown alias '$Alias' (aliases.json)." }
    $missing = @($aliasPool | Where-Object { -not (Test-Path (Join-Path (Join-Path (Split-Path $payload -Parent) 'aliases') "$($_.id).zip")) })
    if ($missing.Count) { throw "Alias builds are missing ($(($missing | ForEach-Object { $_.id }) -join ', ')). Run: lab\vm\payload.ps1 -Force" }
}
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$caseDir = Join-Path (Join-Path (Join-Path $cfg.root 'cases') ("$Name-$Scenario" + $(if ($useAlias) { '+alias' } else { '' }))) $stamp
New-Item -ItemType Directory -Force -Path (Join-Path $caseDir 'shots') | Out-Null
$shotJob = $null
$session = $null
$code = 1
try {
    if ($From) {
        if (-not (Get-VMSnapshot -VMName $vmName -Name $From -ErrorAction SilentlyContinue)) { throw "$vmName has no checkpoint '$From'." }
        $r = Run-Script 'clone.ps1' @('-Restore', $Name, '-Label', $From)
        if ($r.Code -ne 0) { throw "restore failed: $($r.Out)" }
        # A checkpoint brings back the disk limit it was saved with. vm.json is the truth for the profile: when the
        # limit there was changed after the checkpoint was made (office-old: 500 -> 3000 on 2026-11-13), set it again.
        $pn = Get-LabVmProfileName (Get-VM -Name $vmName)
        if ($cfg.profiles.$pn) {
            $wantIops = [uint64]$cfg.profiles.$pn.disk_iops
            $haveIops = [uint64](@(Get-VMHardDiskDrive -VMName $vmName | ForEach-Object { $_.MaximumIOPS }) | Measure-Object -Maximum).Maximum
            if ($haveIops -ne $wantIops) {
                Get-VMHardDiskDrive -VMName $vmName | Set-VMHardDiskDrive -MaximumIOPS $wantIops
                Write-Host "disk limit of $vmName set to $wantIops IOPS (the checkpoint had $haveIops; profile '$pn')"
            }
        }
        $r = Run-Script 'clone.ps1' @('-Start', $Name)
        if ($r.Code -ne 0) { throw "start failed: $($r.Out)" }
    } elseif ((Get-FreshState) -ne 'Running') { throw "$vmName is not running and no -From checkpoint was given." }
    # A slowed-down profile (CPU cap or disk limit) needs more time for everything: on 2026-11-12 the office-old clone
    # needed 80 s to load the test program and its baseline ran out of time. Waits are stretched, the fault is not.
    $vm = Get-VM -Name $vmName
    $profileName = Get-LabVmProfileName $vm
    $sd = Get-LabVmSlowdown $vmName $(if ($scn.defaults.slow_factor) { [double]$scn.defaults.slow_factor } else { 3 })
    $cpuCap = $sd.cpu_cap_percent; $diskIops = $sd.disk_iops; $slow = $sd.factor
    if (-not (Wait-GuestDesktop $cfg $Name ([int](420 * $slow)))) { throw "the guest desktop did not come up in $([int](7 * $slow)) minutes" }
    Start-Sleep -Seconds 20

    $session = New-GuestSession $cfg $Name
    Write-Host "copying the collector and the test program into the guest..."
    Invoke-Command -Session $session -ScriptBlock {
        New-Item -ItemType Directory -Force -Path 'C:\lab\case' | Out-Null
        foreach ($d in 'C:\lab\collector', 'C:\lab\faultlab', 'C:\lab\case\out') { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    }
    Copy-Item -ToSession $session -Path $payload -Destination 'C:\lab\case\payload.zip' -Force
    Invoke-Command -Session $session -ScriptBlock { Expand-Archive -Path 'C:\lab\case\payload.zip' -DestinationPath 'C:\lab' -Force; Remove-Item 'C:\lab\case\payload.zip' -Force }

    # alias programs: picked at random (recorded in scenario.json), installed under Program Files like any program
    $faultAlias = $null
    $decoyAliases = @()
    if ($useAlias) {
        $pool = @($aliasPool | Sort-Object { Get-Random })
        if ($Alias -and $def.kind -eq 'fault') {
            $faultAlias = if ($Alias -eq 'auto') { $pool[0] } else { $aliasPool | Where-Object { $_.id -eq $Alias } | Select-Object -First 1 }
        }
        $decoyAliases = @($pool | Where-Object { -not $faultAlias -or $_.id -ne $faultAlias.id } | Select-Object -First $Decoys)
        foreach ($a in @($faultAlias) + $decoyAliases) {
            if (-not $a) { continue }
            $guestDir = 'C:\Program Files\' + ($a.dir -replace '/', [string][char]92)
            Copy-Item -ToSession $session -Path (Join-Path (Join-Path (Split-Path $payload -Parent) 'aliases') "$($a.id).zip") -Destination "C:\lab\case\$($a.id).zip" -Force
            Invoke-Command -Session $session -ArgumentList $a.id, $guestDir -ScriptBlock {
                param($id, $dir)
                if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
                New-Item -ItemType Directory -Force -Path $dir | Out-Null
                Expand-Archive -Path "C:\lab\case\$id.zip" -DestinationPath $dir -Force
                Remove-Item "C:\lab\case\$id.zip" -Force
            }
        }
    }
    # real programs for this scenario (packages.json): expanded at C:\ as they would sit on a user's PC
    foreach ($pk in @($def.packages)) {
        if (-not $pk) { continue }
        Copy-Item -ToSession $session -Path (Join-Path $pkgDir "$pk.zip") -Destination "C:\lab\case\pkg-$pk.zip" -Force
        Invoke-Command -Session $session -ArgumentList "$pk" -ScriptBlock {
            param($id)
            Expand-Archive -Path "C:\lab\case\pkg-$id.zip" -DestinationPath 'C:\' -Force
            Remove-Item "C:\lab\case\pkg-$id.zip" -Force
        }
    }
    function Get-AliasJob($a) {
        $guestDir = 'C:\Program Files\' + ($a.dir -replace '/', [string][char]92)
        return [ordered]@{ id = $a.id; exe = (Join-Path $guestDir "$($a.exe).exe"); title = $a.title; host = $a.host; plugin = $a.plugin }
    }

    # the job the guest plays
    $guestMemMb = Invoke-Command -Session $session -ScriptBlock { [int]((Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize / 1024) }
    $d = $scn.defaults
    $job = [ordered]@{
        scenario = $Scenario; kind = $def.kind
        warmup_sec = $(if ($WarmupSec -gt 0) { $WarmupSec } else { [int]$d.warmup_sec })
        launch_sec = [int]$d.launch_sec; open_gap_sec = [int]$d.open_gap_sec; wait_dump_sec = [int]$d.wait_dump_sec
        expect_dump = [bool]$def.expect_dump; expect_dump_kind = $def.expect_dump_kind
        slow_factor = $slow; idle_sec = $def.idle_sec; pre_collector_sec = $def.pre_collector_sec
        open = @($Open | ForEach-Object { Get-AppSpec $_ })
        launch = @(); fault = $null; mark = $def.mark
        fault_app = $(if ($faultAlias) { Get-AliasJob $faultAlias } else { $null })
        decoys = @($decoyAliases | ForEach-Object { Get-AliasJob $_ })
    }
    $installed = @()
    $rec = Join-Path (Get-CloneDir $cfg $Name) 'installed.json'
    if (Test-Path $rec) { $installed = @((Get-Content $rec -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ } | Where-Object { $_.ok } | ForEach-Object { $_.id }) }
    if ($def.kind -eq 'launch') {
        $job.launch = @($installed | Where-Object { $catalog.apps.$_ -and $catalog.apps.$_.launch -ne 'skip' } | ForEach-Object { Get-AppSpec $_ })
        if (-not $job.launch.Count) { throw "Nothing to launch: $rec lists no installed program." }
    } elseif ($def.kind -eq 'fault') {
        $args2 = @($def.fault.args)
        if ($def.fault.mem_percent) { $args2 = @([int]($guestMemMb * [int]$def.fault.mem_percent / 100)) + @($def.fault.args_after_mb) }
        $job.fault = [ordered]@{ mode = $def.fault.mode; args = @($args2); timeout_sec = [int]$def.fault.timeout_sec; ok_exit = $def.fault.ok_exit }
    }
    # a real program to run: the scenario itself (kind 'recipe') or the user's own job next to a fault ('workload').
    # "app" = a program installed in the clone (apps.json); the guest finds its folder and starts exe_name from there.
    function Get-RunSpec($r) {
        if (-not $r) { return $null }
        $o = $r | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        if ($r.app) { $o | Add-Member -NotePropertyName app_spec -NotePropertyValue (Get-AppSpec "$($r.app)") -Force }
        return $o
    }
    $job.recipe = Get-RunSpec $def.recipe
    $job.workload = Get-RunSpec $def.workload
    $jobJson = ConvertTo-Json -InputObject $job -Depth 6
    Invoke-Command -Session $session -ArgumentList $jobJson -ScriptBlock { param($j) [IO.File]::WriteAllText('C:\lab\case\job.json', $j, (New-Object Text.UTF8Encoding($false))) }
    # how long the guest may take: every wait the guest stretches (slow_factor) is stretched here too
    $budget = $job.warmup_sec + $slow * ($job.wait_dump_sec + 240 + (@($job.open).Count * 30))
    if ($def.kind -eq 'launch') { $budget += $slow * (@($job.launch) | ForEach-Object { $(if ($_.launch_sec) { [int]$_.launch_sec } else { $job.launch_sec }) + 20 } | Measure-Object -Sum).Sum }
    elseif ($def.kind -eq 'idle') { $budget += $(if ($def.idle_sec) { [int]$def.idle_sec } else { 300 }) }
    else { $budget += $slow * [int]$def.fault.timeout_sec * $(if ($def.mark -and $def.mark.wait_for) { 2 } else { 1 }) + $(if ($def.mark) { [int]$def.mark.after_sec } else { 0 }) }
    if ($def.kind -eq 'recipe') { $budget += $slow * ([int]$def.recipe.timeout_sec + 60) }
    if ($def.workload) { $budget += $slow * (60 + [int]$def.workload.lead_sec) }
    if ($def.pre_collector_sec) { $budget += [int]$def.pre_collector_sec }
    $budget = [int][math]::Ceiling($budget)

    # the screen every 30 s, from a separate process (the main one is busy waiting for the guest)
    $shotJob = Start-Job -ArgumentList (Join-Path $PSScriptRoot 'VmLib.ps1'), $vmName, (Join-Path $caseDir 'shots') -ScriptBlock {
        param($lib, $vm, $dir)
        . $lib
        while ($true) {
            try { Save-VmScreenshot $vm (Join-Path $dir ((Get-Date).ToString('HHmmss') + '.png')) 960 540 | Out-Null } catch { }
            # What the host itself was doing - the PC may be in use during a daytime session (2026-11-14):
            # time, CPU % of the host's own programs, CPU % of all logical processors (guests included), free memory MB
            try {
                $c = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'" -ErrorAction Stop).PercentProcessorTime
                $h = $null
                try { $h = (Get-CimInstance Win32_PerfFormattedData_HvStats_HyperVHypervisorLogicalProcessor -Filter "Name='_Total'" -ErrorAction Stop).PercentTotalRunTime } catch { }
                $m = [int]((Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).FreePhysicalMemory / 1024)
                Add-Content -Path (Join-Path $dir 'host-load.csv') -Value ("{0},{1},{2},{3}" -f (Get-Date).ToString('HH:mm:ss'), $c, $h, $m) -Encoding ASCII
            } catch { }
            Start-Sleep -Seconds 30
        }
    }
    Write-Host ("running scenario '{0}' in {1} (up to {2} min)..." -f $Scenario, $vmName, [math]::Ceiling($budget / 60))
    $run = Invoke-GuestDesktop $session (Get-Content (Join-Path $PSScriptRoot 'guest-case.ps1') -Raw -Encoding UTF8) $budget
    Set-Content -Path (Join-Path $caseDir 'guest-run.log') -Value $run.Log -Encoding UTF8
    if ($shotJob) { Stop-Job $shotJob -ErrorAction SilentlyContinue; Remove-Job $shotJob -Force -ErrorAction SilentlyContinue; $shotJob = $null }
    try { Save-VmScreenshot $vmName (Join-Path $caseDir 'shots\end.png') 1280 720 | Out-Null } catch { }

    $haveZip = Invoke-Command -Session $session -ScriptBlock { Test-Path 'C:\lab\case\out.zip' }
    if ($haveZip) {
        Copy-Item -FromSession $session -Path 'C:\lab\case\out.zip' -Destination (Join-Path $caseDir 'out.zip') -Force
        Expand-Archive -Path (Join-Path $caseDir 'out.zip') -DestinationPath $caseDir -Force
        Remove-Item (Join-Path $caseDir 'out.zip') -Force
    }
    $result = $null
    if (Test-Path (Join-Path $caseDir 'result.json')) { $result = Get-Content (Join-Path $caseDir 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json }

    # 'open' is what really ran in the background, not what was asked for: a case must not claim a program that
    # was not there (2026-11-12: Chrome was not installed in office2, the case said it was open)
    $openRan = @($Open)
    $openMissing = @()
    if ($result) {
        $openRan = @($result.opened | Where-Object { $_ -and $_.id -and $_.ok } | ForEach-Object { $_.id })
        $openMissing = @($result.opened | Where-Object { $_ -and $_.id -and -not $_.ok } | ForEach-Object { $_.id })
    }
    # the answer names the program as it appears in this case (the alias), not the build it came from
    $truth = $def.truth
    if ($faultAlias -and $truth) {
        $truth = $def.truth | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $map = @{ 'FaultLab.exe' = "$($faultAlias.exe).exe"; 'FaultPlugin.dll' = "$($faultAlias.plugin)"; 'FaultHost.exe' = "$($faultAlias.host)" }
        foreach ($p in $truth.PSObject.Properties) { if ($p.Value -is [string]) { foreach ($k in $map.Keys) { $p.Value = $p.Value.Replace($k, $map[$k]) } } }
    }
    # the host's own load during the case (shots\host-load.csv, one line every 30 s): a case made while the PC was
    # busy with something else can be told apart later
    $hostLoad = $null
    try {
        $hl = Join-Path $caseDir 'shots\host-load.csv'
        if (Test-Path $hl) {
            $rows = @(Get-Content $hl | ForEach-Object { $q = "$_" -split ','; if ($q.Count -ge 4 -and $q[1] -ne '') { [pscustomobject]@{ cpu = [double]$q[1]; all = $q[2]; free = [double]$q[3] } } })
            if ($rows.Count) {
                $allv = @($rows | Where-Object { $_.all -ne '' } | ForEach-Object { [double]$_.all })
                $hostLoad = [ordered]@{
                    samples = $rows.Count
                    host_cpu_pct_avg = [math]::Round(($rows | Measure-Object -Property cpu -Average).Average, 1)
                    host_cpu_pct_max = ($rows | Measure-Object -Property cpu -Maximum).Maximum
                    all_cpu_pct_avg = $(if ($allv.Count) { [math]::Round(($allv | Measure-Object -Average).Average, 1) } else { $null })
                    all_cpu_pct_max = $(if ($allv.Count) { ($allv | Measure-Object -Maximum).Maximum } else { $null })
                    free_mb_min = ($rows | Measure-Object -Property free -Minimum).Minimum
                }
            }
        }
    } catch { $hostLoad = $null }
    [ordered]@{
        schema = 2; scenario = $Scenario; title = $def.title; kind = $def.kind
        truth = $truth; confidence = $(if ($def.confidence) { "$($def.confidence)" } else { 'injected' })
        packages = @($def.packages | Where-Object { $_ }); recipe = $def.recipe; workload = $def.workload
        alias = $(if ($faultAlias) { [ordered]@{ id = $faultAlias.id; exe = "$($faultAlias.exe).exe"; vendor = $faultAlias.vendor; product = $faultAlias.product; host = $faultAlias.host; plugin = $faultAlias.plugin } } else { $null })
        decoys = @($decoyAliases | ForEach-Object { "$($_.exe).exe" })
        usable = [bool]($result -and $result.ok -and -not $run.TimedOut)
        clone = $Name; checkpoint = $From; profile = $profileName; cpus = $vm.ProcessorCount; memory_gb = [math]::Round($vm.MemoryStartup / 1GB, 1)
        cpu_cap_percent = $cpuCap; disk_iops_limit = $diskIops; slow_factor = $slow
        gpu_partition = [bool](Test-LabVmGpu $vmName)
        open = $openRan; open_asked = $Open; open_missing = $openMissing; mark = $def.mark; fault = $job.fault
        payload = $payloadInfo; host_started_at = $stamp; host_load = $hostLoad
        guest_timed_out = $run.TimedOut; guest_exit_code = $run.ExitCode
        caveat = 'A virtual machine with a synthetic fault: the hardware profile is an approximation of a slower PC, sensors are mostly missing in a guest, and the GPU (if any) uses the host driver.'
    } | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $caseDir 'scenario.json') -Encoding UTF8

    if ($run.TimedOut) { Write-Host "  the guest run did not finish in time (see shots\end.png)" }
    if ($result) {
        Write-Host ("  result: ok={0}, dumps: {1}{2}" -f $result.ok, (@($result.dumps | ForEach-Object { "$($_.name) [$($_.kind)]" }) -join ', '), $(if (@($result.problems).Count) { '; problems: ' + (@($result.problems) -join '; ') } else { '' }))
        foreach ($w in @($result.warnings)) { if ($w) { Write-Host "  warning: $w" } }
        foreach ($l in @($result.launched)) { if ($l -and $l.id) { Write-Host ("  [{0}] {1,-16} {2} {3}" -f $(if ($l.ok -and -not $l.reason) { 'ok' } elseif ($l.ok) { '??' } else { 'NO' }), $l.id, $l.version, $l.reason) } }
        foreach ($e in @($result.crash_events)) { if ($e -and $e.id) { Write-Host ("  crash event {0}: {1} {2} {3}" -f $e.id, $e.app, $e.module, $e.code) } }
        if ($result.ok -and -not $run.TimedOut) { $code = 0 }
    } else { Write-Host '  no result.json came back from the guest' }
    Write-Host "case folder: $caseDir"
} catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    try { if ((Get-FreshState) -eq 'Running') { Save-VmScreenshot $vmName (Join-Path $caseDir 'shots\failed.png') 1280 720 | Out-Null } } catch { }
    Set-Content -Path (Join-Path $caseDir 'failed.txt') -Value $_.Exception.Message -Encoding UTF8
    $code = 1
} finally {
    if ($shotJob) { Stop-Job $shotJob -ErrorAction SilentlyContinue; Remove-Job $shotJob -Force -ErrorAction SilentlyContinue }
    if ($session) { Remove-PSSession $session -ErrorAction SilentlyContinue }
    if ($From) {
        # back to the checkpoint: the case leaves nothing in the clone
        $r = Run-Script 'clone.ps1' @('-Restore', $Name, '-Label', $From)
        if ($r.Code -ne 0) { Write-Host "WARNING: could not put $vmName back to '$From': $($r.Out)" }
    }
}
exit $code
