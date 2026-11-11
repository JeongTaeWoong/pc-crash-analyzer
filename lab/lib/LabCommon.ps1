# Context, state, fingerprint, preflight and contamination checks for the lab. Dot-source after LabNative.ps1.
$script:LabRoot = Split-Path -Parent $PSScriptRoot
$script:Utf8NoBom = New-Object Text.UTF8Encoding($false)

function Write-LabJson([string]$Path, $Object, [int]$Depth = 10) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $tmp = "$Path.tmp"
    [IO.File]::WriteAllText($tmp, ($Object | ConvertTo-Json -Depth $Depth), $script:Utf8NoBom)
    Move-Item -LiteralPath $tmp -Destination $Path -Force      # replace in one step so a crash never leaves half a file
}
function Read-LabJson([string]$Path) { return (Get-Content -Raw -Encoding UTF8 -LiteralPath $Path | ConvertFrom-Json) }

function Write-LabLog($Lab, [string]$Text) {
    $line = "{0} {1}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Text
    Write-Host $line
    if ($Lab -and $Lab.Runs) { if (-not (Test-Path -LiteralPath $Lab.Runs)) { New-Item -ItemType Directory -Force $Lab.Runs | Out-Null }; [IO.File]::AppendAllText((Join-Path $Lab.Runs 'lab.log'), $line + "`r`n", $script:Utf8NoBom) }
}

# Paths, settings and pinned tools. Throws if a tool is missing or differs from tools.lock.json.
function Get-LabContext([string]$RunsRoot) {
    # PCA_LAB_CONFIG lets the dry-run tests use a shortened copy of lab.json; real sessions never set it.
    $cfgPath = if ($env:PCA_LAB_CONFIG) { $env:PCA_LAB_CONFIG } else { Join-Path $script:LabRoot 'lab.json' }
    $cfg = Read-LabJson $cfgPath
    $lock = Read-LabJson (Join-Path $script:LabRoot 'tools.lock.json')
    $toolsDir = Join-Path $script:LabRoot 'tools'
    $tools = @{}
    foreach ($t in $lock.tools) {
        $pairs = @(@{ key = $t.id; rel = $t.exe; hash = $t.exe_sha256 })
        if ($t.console_exe) { $pairs += @{ key = "$($t.id)_console"; rel = $t.console_exe; hash = $t.console_exe_sha256 } }
        foreach ($p in $pairs) {
            $full = Join-Path $toolsDir ($p.rel -replace '/', '\')
            if (-not (Test-Path -LiteralPath $full)) { throw "tool missing: $full (see lab/README.md)" }
            $h = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($h -ne $p.hash) { throw "tool changed since it was pinned: $($p.rel)" }
            $tools[$p.key] = $full
        }
    }
    if (-not (Test-Path -LiteralPath $cfg.chrome)) { throw "browser not found: $($cfg.chrome)" }
    $work = [Environment]::ExpandEnvironmentVariables($cfg.work_root)
    if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Force $work | Out-Null }
    if (-not $RunsRoot) { $RunsRoot = Join-Path $script:LabRoot 'runs' }
    return @{ Root = $script:LabRoot; Config = $cfg; Tools = $tools; Work = $work; Runs = $RunsRoot; ChromeProfile = (Join-Path $work 'chrome-profile'); BankLoads = 0; LastScriptInputTick = [uint32]0 }
}

# ---- state -------------------------------------------------------------------------------------------------------------
function Get-LabStatePath($Lab) { return (Join-Path $Lab.Runs 'state.json') }
function Read-LabState($Lab) { $p = Get-LabStatePath $Lab; if (Test-Path -LiteralPath $p) { return (Read-LabJson $p) } return $null }
function Save-LabState($Lab, $State) { $State.updated = (Get-Date).ToString('o'); Write-LabJson (Get-LabStatePath $Lab) $State }
function New-LabState($Lab) {
    return [pscustomobject]@{
        schema = 1; experiment = ('exp-' + (Get-Date).ToString('yyyyMMdd-HHmmss')); created = (Get-Date).ToString('o'); updated = $null
        phase = 'pilot'; plan = $null; boots = @(); session = $null; sessions = @(); reference = $null
        installed = $null; referenceAfterInstall = $null; fingerprintChanges = @()
        settleBootsDone = 0; consecutiveFailures = 0; reboots = 0; stopRequested = $false; lastMessage = ''
    }
}
function Get-LabBootCount($State, [string]$Phase) { return @($State.boots | Where-Object { $_.phase -eq $Phase -and $_.status -eq 'complete' }).Count }
function Get-LabBootTime { return (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o') }

# ---- fingerprint ---------------------------------------------------------------------------------------------------------
# What must stay the same between sessions for "before" and "after" to be comparable.
function Get-LabFingerprint($Lab) {
    $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $drivers = foreach ($d in Get-CimInstance Win32_SystemDriver) {
        $f = "$($d.PathName)".Trim('"') -replace '^\\\?\?\\', '' -replace '^(?i)\\SystemRoot', $env:SystemRoot
        if ($f -match '^(?i)system32\\') { $f = Join-Path $env:SystemRoot $f }
        $h = try { (Get-FileHash -LiteralPath $f -Algorithm SHA256 -ErrorAction Stop).Hash.Substring(0, 16).ToLowerInvariant() } catch { 'unreadable' }
        "$(if ($d.State -eq 'Running') { 'R' } else { 'S' })|$($d.Name)|$h"
    }
    $programs = foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') {
        Get-ItemProperty $k -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | ForEach-Object { "$($_.DisplayName)|$($_.DisplayVersion)" }
    }
    $hvci = [bool](@((Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue).SecurityServicesRunning) -contains 2)
    $defender = try { $s = Get-MpComputerStatus -ErrorAction Stop; "$($s.AMEngineVersion)|$($s.AntivirusSignatureVersion)" } catch { 'unavailable' }
    return [ordered]@{
        at = (Get-Date).ToString('o')
        os = "$($cv.DisplayVersion) $($cv.CurrentBuild).$($cv.UBR)"
        edition = "$($cv.EditionID)"
        browser = (Get-Item -LiteralPath $Lab.Config.chrome).VersionInfo.ProductVersion
        gpu = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name)|$($_.DriverVersion)" } | Sort-Object)
        hvci = $hvci
        drivers = @($drivers | Where-Object { $_ -like 'R|*' } | ForEach-Object { $_.Substring(2) } | Sort-Object -Unique)            # loaded now
        driversStopped = @($drivers | Where-Object { $_ -like 'S|*' } | ForEach-Object { $_.Substring(2) } | Sort-Object -Unique)     # registered, not loaded
        services = @(Get-CimInstance Win32_Service | ForEach-Object { "$($_.Name)|$($_.PathName)" } | Sort-Object -Unique)
        serviceStart = @(Get-CimInstance Win32_Service | ForEach-Object { "$($_.Name)|$($_.StartMode)" } | Sort-Object -Unique)
        programs = @($programs | Sort-Object -Unique)
        defender = $defender
    }
}

function Compare-LabList($a, $b, [int]$max = 8) {
    $a = @($a); $b = @($b)
    $added = @($b | Where-Object { $a -notcontains $_ }); $removed = @($a | Where-Object { $b -notcontains $_ })
    return @{ added = $added; removed = $removed; text = (@($added | Select-Object -First $max | ForEach-Object { "+$_" }) + @($removed | Select-Object -First $max | ForEach-Object { "-$_" })) -join '; ' }
}

# Per-user services get a new suffix at every logon (CDPUserSvc_d5677 -> CDPUserSvc_9f5f8): compare them without it.
function ConvertTo-LabStableNames($List) {
    return @(@($List) | ForEach-Object { "$_" -replace '^([^|]*?)_[0-9a-f]{4,8}\|', '$1_*|' } | Sort-Object -Unique)
}

# blocking: changes that can move the measurements by themselves. notes: recorded, do not stop a session.
# Default: both fingerprints were taken at the same point after a boot, so the set of LOADED drivers is compared.
# -ConfigOnly: one of them was taken on a PC that had been in use for hours (session start). Which drivers happen to
# be loaded then says little, so the REGISTERED drivers (loaded or not, name and file hash) are compared instead.
# (2026-11-11: the first reference was taken before the first restart; the restart then loaded newly installed tablet
# drivers and finished a Windows edition change, and every later boot differed from that reference.)
function Compare-LabFingerprint($Ref, $Cur, [switch]$ConfigOnly) {
    $blocking = @(); $notes = @()
    foreach ($k in 'os', 'browser', 'hvci') { if ("$($Ref.$k)" -ne "$($Cur.$k)") { $blocking += "${k}: $($Ref.$k) -> $($Cur.$k)" } }
    if ($Ref.edition -and $Cur.edition -and "$($Ref.edition)" -ne "$($Cur.edition)") { $blocking += "edition: $($Ref.edition) -> $($Cur.edition)" }
    $d = Compare-LabList $Ref.gpu $Cur.gpu; if ($d.added.Count -or $d.removed.Count) { $blocking += "gpu: $($d.text)" }
    if ($ConfigOnly) {
        $d = Compare-LabList (@($Ref.drivers) + @($Ref.driversStopped) | Sort-Object -Unique) (@($Cur.drivers) + @($Cur.driversStopped) | Sort-Object -Unique)
        if ($d.added.Count -or $d.removed.Count) { $blocking += "drivers (registered): $($d.text)" }
    }
    else {
        $d = Compare-LabList $Ref.drivers $Cur.drivers; if ($d.added.Count -or $d.removed.Count) { $blocking += "drivers: $($d.text)" }
        $d = Compare-LabList $Ref.driversStopped $Cur.driversStopped; if ($d.added.Count -or $d.removed.Count) { $notes += "driversStopped: $($d.text)" }
    }
    $d = Compare-LabList (ConvertTo-LabStableNames $Ref.services) (ConvertTo-LabStableNames $Cur.services); if ($d.added.Count -or $d.removed.Count) { $blocking += "services: $($d.text)" }
    $d = Compare-LabList (ConvertTo-LabStableNames $Ref.serviceStart) (ConvertTo-LabStableNames $Cur.serviceStart); if ($d.added.Count -or $d.removed.Count) { $notes += "serviceStart: $($d.text)" }
    $d = Compare-LabList $Ref.programs $Cur.programs; if ($d.added.Count -or $d.removed.Count) { $notes += "programs: $($d.text)" }
    if ("$($Ref.defender)" -ne "$($Cur.defender)") { $notes += "defender: $($Ref.defender) -> $($Cur.defender)" }
    return @{ blocking = $blocking; notes = $notes }
}

# The fingerprint of the newest complete block of a phase (taken right after that boot), or $null.
function Get-LabLastBootFingerprint($Lab, [string]$Phase, [string]$Before) {
    $root = Join-Path $Lab.Runs 'boots'
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -Filter "$Phase-*" | Sort-Object Name -Descending)) {
        if ($Before -and $dir.Name -ge $Before) { continue }
        $s = Join-Path $dir.FullName 'summary.json'; $f = Join-Path $dir.FullName 'fingerprint.json'
        if (-not (Test-Path -LiteralPath $s) -or -not (Test-Path -LiteralPath $f)) { continue }
        try { if ((Read-LabJson $s).status -eq 'complete') { return @{ id = $dir.Name; fingerprint = (Read-LabJson $f) } } } catch { }
    }
    return $null
}

# Times at which Defender took new definitions (Windows Defender/Operational 2000). After one, Defender scans every
# program again the first time it starts: on 2026-11-11 03:25 the next Godot import took 4.52 s instead of 3.44 s and the
# next Blender window 2.7 s instead of 1.0 s, and the launches after those were normal again.
function Get-LabDefinitionUpdates([datetime]$From, [datetime]$To) {
    return @(Get-LabEvents 'Microsoft-Windows-Windows Defender/Operational' @(2000) $From $To | ForEach-Object { $_.TimeCreated })
}

# ---- preflight -----------------------------------------------------------------------------------------------------------
# Returns @{ blocking = [...]; warnings = [...] }. Read-only.
function Test-LabPreflight($Lab) {
    $blocking = @(); $warnings = @()
    if (-not (Test-LabSnapshotLayout)) { $blocking += 'process sampler layout check failed on this Windows build' }
    $bad = @(Get-Process | Where-Object { $_.ProcessName -match $Lab.Config.forbidden_processes } | ForEach-Object ProcessName | Sort-Object -Unique)
    if ($bad.Count) { $blocking += ('close these first (games, anti-cheat, PC BlackBox): ' + ($bad -join ', ')) }
    $noisy = @(Get-Process | Where-Object { $_.ProcessName -match $Lab.Config.noisy_processes } | ForEach-Object ProcessName | Sort-Object -Unique)
    if ($noisy.Count) { $warnings += ('running and recorded, may add noise: ' + ($noisy -join ', ')) }
    $free = (Get-PSDrive -Name (Split-Path -Qualifier $Lab.Work).TrimEnd(':')).Free
    if ($free -lt 5GB) { $blocking += 'less than 5 GB free on the work drive' }
    $ux = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction SilentlyContinue
    $pausedUntil = $null
    if ($ux -and $ux.PauseUpdatesExpiryTime) { try { $pausedUntil = [datetime]::Parse($ux.PauseUpdatesExpiryTime).ToLocalTime() } catch { } }
    if (-not $pausedUntil -or $pausedUntil -lt (Get-Date)) { $warnings += 'Windows Update is not paused (it installed 10 drivers in the 14 days before 2026-11-10)' }
    $open = @(Get-Process | Where-Object { $_.MainWindowTitle -and $_.ProcessName -notmatch '^(powershell|WindowsTerminal|conhost|explorer|TextInputHost|SystemSettings|ApplicationFrameHost|NVIDIA Overlay)$' -and $_.ProcessName -notmatch $Lab.Config.noisy_processes } | ForEach-Object ProcessName | Sort-Object -Unique)
    if ($open.Count) { $warnings += ('other apps are open: ' + ($open -join ', ')) }
    return @{ blocking = $blocking; warnings = $warnings }
}

# After a boot: wait a fixed time, then until total CPU stops moving (three windows within idle_stable_points of each other).
function Wait-LabIdle($Lab, [int]$PostBootWaitSec) {
    $t = $Lab.Config.timing
    $bootAge = ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalSeconds
    $wait = [math]::Max(0, $PostBootWaitSec - $bootAge)
    if ($wait -gt 0) { Start-Sleep -Seconds ([int]$wait) }
    $cores = [Environment]::ProcessorCount; $hist = @(); $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $t.idle_max_wait_sec) {
        $a = ([LabNative]::Snapshot() | Where-Object Pid -eq 0).CpuTicks; $s = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $t.idle_window_sec
        $b = ([LabNative]::Snapshot() | Where-Object Pid -eq 0).CpuTicks
        $hist += [math]::Round(100 - (($b - $a) / 1e7 / $s.Elapsed.TotalSeconds / $cores * 100), 2)
        if ($hist.Count -ge 3) { $last = $hist[-3..-1]; if ((($last | Measure-Object -Maximum).Maximum - ($last | Measure-Object -Minimum).Minimum) -le $t.idle_stable_points) { return @{ stable = $true; cpu = $last; waitedSec = [math]::Round($wait + $sw.Elapsed.TotalSeconds, 0) } } }
    }
    return @{ stable = $false; cpu = $hist; waitedSec = [math]::Round($wait + $sw.Elapsed.TotalSeconds, 0) }
}

# ---- sampler ---------------------------------------------------------------------------------------------------------------
function Start-LabSampler($Lab, [string]$OutFile) {
    $stop = "$OutFile.stop"
    Remove-Item -LiteralPath $stop, $OutFile -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f (Join-Path $Lab.Root 'lib\sampler.ps1')), '-Out', ('"{0}"' -f $OutFile), '-StopFile', ('"{0}"' -f $stop), '-Watch', ('"{0}"' -f ($Lab.Config.watch_processes + '|' + $Lab.Config.security_modules))
    return @{ Process = $p; Out = $OutFile; Stop = $stop }
}
function Stop-LabSampler($s) { if (-not $s) { return }; [IO.File]::WriteAllText($s.Stop, 'stop'); if (-not $s.Process.WaitForExit(5000)) { try { $s.Process.Kill() } catch { } }; Remove-Item -LiteralPath $s.Stop -ErrorAction SilentlyContinue }

# Average machine CPU and per-process CPU/memory between two instants, from the sampler's lines.
function Get-LabSamplerSummary([string]$File, [datetime]$From, [datetime]$To) {
    if (-not (Test-Path -LiteralPath $File)) { return [ordered]@{ status = 'unavailable'; reason = 'no sampler file' } }
    $fs = New-Object IO.FileStream $File, 'Open', 'Read', 'ReadWrite'
    $sr = New-Object IO.StreamReader $fs
    $cpu = @(); $proc = @{}
    try {
        while ($null -ne ($line = $sr.ReadLine())) {
            if ($line.Length -lt 10) { continue }
            $o = $null; try { $o = $line | ConvertFrom-Json } catch { continue }
            $t = [datetime]$o.t
            if ($t -lt $From -or $t -gt $To) { continue }
            $cpu += [double]$o.cpu
            foreach ($p in $o.p) { $n = "$($p[0])"; if (-not $proc.ContainsKey($n)) { $proc[$n] = @{ cpu = 0.0; mem = 0.0 } }; $proc[$n].cpu += [double]$p[1]; if ([double]$p[2] -gt $proc[$n].mem) { $proc[$n].mem = [double]$p[2] } }
        }
    }
    finally { $sr.Dispose(); $fs.Dispose() }
    if (-not $cpu.Count) { return [ordered]@{ status = 'unavailable'; reason = 'no samples in the interval' } }
    $top = $proc.GetEnumerator() | ForEach-Object { [pscustomobject]@{ name = $_.Key; cpuPct = [math]::Round($_.Value.cpu / $cpu.Count, 3); maxPrivMB = [math]::Round($_.Value.mem, 0) } } | Sort-Object cpuPct -Descending | Select-Object -First 12
    return [ordered]@{ samples = $cpu.Count; cpuAvg = [math]::Round(($cpu | Measure-Object -Average).Average, 2); cpuMax = [math]::Round(($cpu | Measure-Object -Maximum).Maximum, 1); processes = @($top) }
}

# ---- contamination -------------------------------------------------------------------------------------------------------
function Get-LabEvents([string]$Log, [int[]]$Ids, [datetime]$From, [datetime]$To, [string]$Provider) {
    $f = @{ LogName = $Log; Id = $Ids; StartTime = $From; EndTime = $To }
    if ($Provider) { $f.ProviderName = $Provider }
    try { return @(Get-WinEvent -FilterHashtable $f -ErrorAction Stop) } catch { return @() }
}

# Store / MSIX package installs and updates that finished in the window. The Store's automatic
# app updates could not be paused on this PC (11/10), so they are detected instead of prevented.
# AppXDeploymentServer event 400 = a deployment operation finished; DeploymentOperation codes seen
# on this PC (14 days, 271 events): 1 Add, 2 Remove, 4 Stage, 5 DeStage count as a change;
# 6 Register, 27 OnDemandRegister, 29 Provision, 33 Deprovision happen at logon and do not.
function Get-LabPackageChanges([datetime]$From, [datetime]$To) {
    $names = @()
    foreach ($e in (Get-LabEvents 'Microsoft-Windows-AppXDeploymentServer/Operational' @(400) $From $To)) {
        try {
            $d = ([xml]$e.ToXml()).Event.EventData.Data
            $op = ($d | Where-Object { $_.Name -eq 'DeploymentOperation' }).'#text'
            if (@('1', '2', '4', '5') -contains $op) {
                $names += (($d | Where-Object { $_.Name -eq 'PackageFullName' }).'#text' -replace '_.*$', '')
            }
        } catch { }
    }
    return @($names | Sort-Object -Unique)
}

# Reasons to drop a run (pre-registered in docs/security-impact-design.md 4.5). Empty list = clean.
function Get-LabContamination($Lab, [datetime]$From, [datetime]$To, [uint32]$TickAtStart) {
    $reasons = @()
    $tick = [LabNative]::LastInputTick()
    if ($tick -ne $TickAtStart -and $tick -ne $Lab.LastScriptInputTick) { $reasons += 'input that the script did not send' }
    if ((Get-LabEvents 'Microsoft-Windows-Windows Defender/Operational' @(1000) $From $To).Count) { $reasons += 'Defender scan started' }
    if ((Get-LabEvents 'System' @(19, 43) $From $To 'Microsoft-Windows-WindowsUpdateClient').Count) { $reasons += 'Windows Update install' }
    $pk = @(Get-LabPackageChanges $From $To)
    if ($pk.Count) { $reasons += ('app package install or update: ' + (($pk | Select-Object -First 3) -join ', ')) }
    $bad = @(Get-Process | Where-Object { $_.ProcessName -match $Lab.Config.forbidden_processes } | ForEach-Object ProcessName | Sort-Object -Unique)
    if ($bad.Count) { $reasons += ('forbidden process: ' + ($bad -join ', ')) }
    return $reasons
}
