# Measures idle overhead and a fixed workload so a program's effect can be compared before and after installing it.
# Writes only to its own temp folder and to -OutDir. Output contains local process names: keep it out of git.
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [int]$IdleSec = 180,
    [int]$SampleSec = 5,
    [int]$Rounds = 3,
    [int]$FileCount = 3000,
    [int]$SpawnCount = 200,
    [string]$OutDir = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs\perf',
    [string]$BuildProject = 'F:\2026CAU\Capstone2\pc-crash-analyzer\participant\StudyTools\StudyTools.csproj'
)
$ErrorActionPreference = 'Stop'
$WatchRx = '(?i)astx|ahnlab|nprotect|nos(svc|tray|bg)|npkcrypt|touchen|nxkey|veraport|wizvera|ipinside|i3gproc|delfino|anysign|magicline|inisafe|crossweb|keysharp|ksign|markany|maepagesafer|softcamp|vgc|vgtray|easyanticheat|beservice|battleye|xigncode|xhunter|gameguard|sguard'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$cores = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors

function Get-Median($values) {
    $s = @($values | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    if ($s.Count % 2) { return $s[[int](($s.Count - 1) / 2)] }
    return ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2
}
function Get-Average($values) { return (@($values) | Measure-Object -Average).Average }

# --- idle ---
$cpu = @(); $dpc = @(); $avail = @(); $commit = @(); $poolNp = @(); $poolP = @(); $disk = @()
$procCpu = @{}; $procMem = @{}
$end = (Get-Date).AddSeconds($IdleSec)
$first = $true
while ((Get-Date) -lt $end) {
    $p = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    $m = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
    $d = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'"
    $ps = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.Name -ne '_Total' -and $_.Name -ne 'Idle' }
    if (-not $first) {
        $cpu += [double]$p.PercentProcessorTime; $dpc += [double]$p.PercentDPCTime
        $avail += [double]$m.AvailableMBytes; $commit += [double]$m.CommittedBytes / 1MB
        $poolNp += [double]$m.PoolNonpagedBytes / 1MB; $poolP += [double]$m.PoolPagedBytes / 1MB
        $disk += [double]$d.DiskBytesPersec / 1MB
        $byName = $ps | Group-Object { $_.Name -replace '#\d+$', '' }
        foreach ($g in $byName) {
            if (-not $procCpu.ContainsKey($g.Name)) { $procCpu[$g.Name] = New-Object System.Collections.ArrayList; $procMem[$g.Name] = New-Object System.Collections.ArrayList }
            [void]$procCpu[$g.Name].Add((($g.Group | Measure-Object PercentProcessorTime -Sum).Sum) / $cores)
            [void]$procMem[$g.Name].Add((($g.Group | Measure-Object WorkingSetPrivate -Sum).Sum) / 1MB)
        }
    }
    $first = $false
    Start-Sleep -Seconds $SampleSec
}
$samples = $cpu.Count
$procRows = foreach ($k in $procCpu.Keys) {
    [pscustomobject]@{ name = $k; cpu = [math]::Round(((($procCpu[$k] | Measure-Object -Sum).Sum) / $samples), 3); privMB = [math]::Round((Get-Average $procMem[$k]), 1) }
}
$idle = [ordered]@{
    samples = $samples
    cpuPct = [math]::Round((Get-Average $cpu), 3); dpcPct = [math]::Round((Get-Average $dpc), 3)
    availMB = [math]::Round((Get-Average $avail), 0); committedMB = [math]::Round((Get-Average $commit), 0)
    poolNonpagedMB = [math]::Round((Get-Average $poolNp), 1); poolPagedMB = [math]::Round((Get-Average $poolP), 1)
    diskMBps = [math]::Round((Get-Average $disk), 3)
    topCpu = @($procRows | Sort-Object cpu -Descending | Select-Object -First 15)
    topMem = @($procRows | Sort-Object privMB -Descending | Select-Object -First 15)
    watched = @($procRows | Where-Object { $_.name -match $WatchRx })
}

# --- system facts ---
$runKeys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$autoruns = foreach ($k in $runKeys) { (Get-Item $k -ErrorAction SilentlyContinue).Property }
$kbClass = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}'
$facts = [ordered]@{
    processes = @(Get-Process).Count
    servicesRunning = @(Get-Service | Where-Object Status -eq 'Running').Count
    servicesTotal = @(Get-Service).Count
    driversRunning = @(Get-CimInstance Win32_SystemDriver -Filter "State='Running'").Count
    # HVCI state can change between runs (Windows may turn it on); 2 in SecurityServicesRunning means HVCI is running
    hvciRunning = [bool](@((Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue).SecurityServicesRunning) -contains 2)
    keyboardUpper = @((Get-ItemProperty $kbClass -ErrorAction SilentlyContinue).UpperFilters)
    autorunCount = @($autoruns).Count
    autoruns = @($autoruns | Sort-Object)
    rootCertsMachine = @(Get-ChildItem Cert:\LocalMachine\Root).Count
    rootCertsUser = @(Get-ChildItem Cert:\CurrentUser\Root).Count
    rootCertThumbprints = @(Get-ChildItem Cert:\LocalMachine\Root, Cert:\CurrentUser\Root | ForEach-Object Thumbprint | Sort-Object -Unique)
    watchedServices = @(Get-Service | Where-Object { $_.Name -match $WatchRx -or $_.DisplayName -match $WatchRx } | ForEach-Object { "$($_.Name)=$($_.Status)" })
    watchedDrivers = @(Get-CimInstance Win32_SystemDriver | Where-Object { $_.Name -match $WatchRx } | ForEach-Object { "$($_.Name)=$($_.State)" })
}

# --- workload (temp folder on C:, where real-time filters apply) ---
$work = Join-Path $env:LOCALAPPDATA 'Temp\pca-perf'
$data = New-Object byte[] 4096
(New-Object Random 42).NextBytes($data)
$rows = for ($r = 1; $r -le $Rounds; $r++) {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force $work | Out-Null
    $paths = 1..$FileCount | ForEach-Object { Join-Path $work "f$_.bin" }
    $sw = [Diagnostics.Stopwatch]::StartNew(); foreach ($f in $paths) { [IO.File]::WriteAllBytes($f, $data) }; $create = $sw.ElapsedMilliseconds
    $sw.Restart(); foreach ($f in $paths) { [void][IO.File]::ReadAllBytes($f) }; $read = $sw.ElapsedMilliseconds
    $sw.Restart(); foreach ($f in $paths) { [IO.File]::Delete($f) }; $del = $sw.ElapsedMilliseconds
    $sw.Restart()
    for ($i = 0; $i -lt $SpawnCount; $i++) {
        $si = New-Object Diagnostics.ProcessStartInfo "$env:SystemRoot\System32\cmd.exe", '/c exit'
        $si.UseShellExecute = $false; $si.CreateNoWindow = $true
        $pr = [Diagnostics.Process]::Start($si); $pr.WaitForExit(); $pr.Dispose()
    }
    $spawn = $sw.ElapsedMilliseconds
    $sw.Restart()
    & dotnet build $BuildProject -c Debug --no-incremental --nologo -v q -p:UseSharedCompilation=false 2>&1 | Out-Null
    $build = $sw.ElapsedMilliseconds
    [pscustomobject]@{ round = $r; createMs = $create; readMs = $read; deleteMs = $del; spawnMs = $spawn; buildMs = $build }
}
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
$median = [ordered]@{}
foreach ($k in 'createMs', 'readMs', 'deleteMs', 'spawnMs', 'buildMs') { $median[$k] = Get-Median ($rows | ForEach-Object { $_.$k }) }

$result = [ordered]@{ label = $Label; at = (Get-Date).ToString('s'); cores = $cores; fileCount = $FileCount; spawnCount = $SpawnCount; idle = $idle; facts = $facts; rounds = @($rows); median = $median }
$out = Join-Path $OutDir ("{0}-{1}.json" -f ($Label -replace '[^\w.-]', '_'), (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.File]::WriteAllText($out, ($result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
Write-Host "[perf] $out"
Write-Host ("[perf] idle cpu {0}% dpc {1}% committed {2}MB nonpaged {3}MB | procs {4} svc {5} drv {6} rootcerts {7}/{8}" -f $idle.cpuPct, $idle.dpcPct, $idle.committedMB, $idle.poolNonpagedMB, $facts.processes, $facts.servicesRunning, $facts.driversRunning, $facts.rootCertsMachine, $facts.rootCertsUser)
Write-Host ("[perf] median create {0} read {1} delete {2} spawn {3} build {4} ms" -f $median.createMs, $median.readMs, $median.deleteMs, $median.spawnMs, $median.buildMs)
