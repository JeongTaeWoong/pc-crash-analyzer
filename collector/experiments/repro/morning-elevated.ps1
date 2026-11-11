# Run once from an elevated PowerShell. Installs the verified security modules one at a time, and after each one
# records what changed on the PC and measures idle overhead and a fixed workload. A console collector records logs throughout.
#   powershell -ExecutionPolicy Bypass -File collector\experiments\repro\morning-elevated.ps1 [-DryRun] [-Only slug1,slug2] [-Interactive]
# Nothing is uploaded. Programs stay installed afterwards; remove them with Hoax Eliminator when done, then run:
#   collector\experiments\repro\perf-measure.ps1 -Label after-removal
# Quit the PC BlackBox tray app before running this, or these events would be uploaded as participant data.
param(
    [switch]$DryRun,
    [switch]$Interactive,   # do not pass silent flags; click through each installer yourself
    [string[]]$Only = @(),
    [int]$IdleSec = 180,
    [int]$InstallTimeoutSec = 600,
    # -Unattended (used by lab\install-elevated.ps1): nobody is at the PC. Installer windows are clicked through by
    # auto-click.ps1, an installer that is still open after the timeout is closed, and nothing from a user-writable
    # folder is executed with administrator rights: no collector, no project build in the workload.
    [switch]$Unattended,
    [string]$ManifestPath    # default: installers\manifest.json. The lab passes a copy kept where only administrators can write.
)
$ErrorActionPreference = 'Stop'
# powershell -File hands "-Only a,b" over as the single string 'a,b'; split it so several slugs select several installers
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$repo = 'F:\2026CAU\Capstone2\pc-crash-analyzer'
$here = $PSScriptRoot
$runs = "$repo\collector\experiments\repro-runs"
$dest = "$runs\installers"
$diffDir = "$runs\install-diff"
$measure = "$here\perf-measure.ps1"
$collector = "$repo\collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $DryRun -and -not $isAdmin) { throw 'Run this from an elevated PowerShell (or use -DryRun to only verify the files).' }
if (-not $DryRun -and (Get-Process PcBlackBox -ErrorAction SilentlyContinue)) { throw 'Quit the PC BlackBox tray app first.' }
# The tray's logon task would start it again after the reboots that follow this run (11/10: installed copy is 0.8.1 with the lost key).
# This script only checks; turning the task off is the user's step. 0.8.2's startup Repair re-registers a disabled-but-present task.
$autoOn = @(Get-ScheduledTask -TaskPath '\PcBlackBox\' -ErrorAction SilentlyContinue | Where-Object { $_.State -ne 'Disabled' })
if (-not $DryRun -and $autoOn.Count) { throw ("PC BlackBox logon task is enabled. In this elevated window run first:`n  Disable-ScheduledTask -TaskPath '\PcBlackBox\' -TaskName '{0}'" -f $autoOn[0].TaskName) }

if (-not $ManifestPath) { $ManifestPath = "$dest\manifest.json" }
$parsed = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$all = @($parsed) | Where-Object { $_.group -eq 'banking' }
if ($Only.Count) { $all = $all | Where-Object { $Only -contains $_.slug } }
$all = @($all)

# Refuse to run anything whose bytes or signature differ from what was checked when it was downloaded
foreach ($i in $all) {
    $p = Join-Path $dest $i.file
    if (-not (Test-Path -LiteralPath $p)) { throw "missing: $p" }
    $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $i.sha256) { throw "hash mismatch: $($i.slug)" }
    $sig = Get-AuthenticodeSignature -LiteralPath $p
    if ($sig.Status -ne 'Valid') { throw "signature not valid: $($i.slug) ($($sig.Status))" }
    Write-Host ("[verify] {0,-36} OK  {1}" -f $i.slug, $sig.SignerCertificate.Subject.Split(',')[0])
}
if ($DryRun) { Write-Host "[dry-run] $($all.Count) installers verified; nothing was installed."; return }

function Get-State {
    $u = foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') { Get-ItemProperty $k -ErrorAction SilentlyContinue | ForEach-Object { "$($_.DisplayName)" } }
    $flt = @(try { cmd /c "fltmc filters 2>nul" | Select-Object -Skip 3 | ForEach-Object { ($_ -split '\s+')[0] } | Where-Object { $_ } } catch { })
    $kb = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4D36E96B-E325-11CE-BFC1-08002BE10318}'
    return [ordered]@{
        uninstall = @($u | Where-Object { $_ } | Sort-Object -Unique)
        services  = @(Get-CimInstance Win32_Service | ForEach-Object { "$($_.Name)|$($_.StartMode)|$($_.PathName)" })
        drivers   = @(Get-CimInstance Win32_SystemDriver | ForEach-Object { "$($_.Name)|$($_.StartMode)|$($_.PathName)" })
        minifilters = $flt
        keyboardUpper = @((Get-ItemProperty $kb -ErrorAction SilentlyContinue).UpperFilters)
        rootCerts = @(Get-ChildItem Cert:\LocalMachine\Root, Cert:\CurrentUser\Root | ForEach-Object { "$($_.Thumbprint)|$($_.Subject)" } | Sort-Object -Unique)
        tasks     = @(Get-ScheduledTask | ForEach-Object { "$($_.TaskPath)$($_.TaskName)" })
        autoruns  = @(foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') { (Get-Item $k -ErrorAction SilentlyContinue).Property })
        processes = @(Get-Process | ForEach-Object { $_.ProcessName } | Sort-Object -Unique)
        # local web servers are how these modules talk to bank pages, and where most published flaws in them sit
        listening = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)|$((Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName)" } | Sort-Object -Unique)
    }
}

New-Item -ItemType Directory -Force $diffDir | Out-Null
$env:DOTNET_ROLL_FORWARD = 'Major'
$log = "$runs\morning-collector.log"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "$env:SystemRoot\System32\cmd.exe"
$psi.Arguments = "/c `"`"$collector`" > `"$log`" 2>&1`""
$psi.WorkingDirectory = $runs
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.CreateNoWindow = $true
$c = if ($Unattended) { $null } else { [System.Diagnostics.Process]::Start($psi) }
function Send-Collector([string]$line) { if ($c) { $c.StandardInput.WriteLine($line); $c.StandardInput.Flush() } }
$measureArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $measure, '-IdleSec', $IdleSec)
if ($Unattended) { $measureArgs += '-NoBuild' }
$clicker = @("$here\auto-click.ps1", "$repo\lab\lib\auto-click.ps1") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$summary = @()
try {
    Send-Collector ''   # lands any BOM on an empty line
    if ($c) { Start-Sleep -Seconds 20 }
    & powershell @measureArgs -Label 'admin-baseline'
    foreach ($i in $all) {
        Write-Host "`n===== $($i.product) =====" -ForegroundColor Cyan
        $before = Get-State
        $flags = if ($Interactive) { '' } else { switch ($i.packer) { 'NSIS' { '/S' } 'Inno' { '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' } default { '' } } }
        if (-not $flags) { Write-Host 'No documented silent option: click through the installer window with the default choices.' -ForegroundColor Yellow }
        $path = Join-Path $dest $i.file
        $p = if ($flags) { Start-Process -FilePath $path -ArgumentList $flags -PassThru } else { Start-Process -FilePath $path -PassThru }
        [void]$p.Handle
        $click = $null
        if ($Unattended -and $clicker) { $click = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$clicker`"", '-ProcessId', $p.Id, '-TimeoutSec', $InstallTimeoutSec, '-LogFile', "`"$diffDir\$($i.slug).clicks.log`"" }
        $outcome = if ($p.WaitForExit($InstallTimeoutSec * 1000)) { 'exit 0x{0:X8}' -f $p.ExitCode } else { "still running after ${InstallTimeoutSec}s; continuing" }
        if ($Unattended) {
            if ($click) { [void]$click.WaitForExit(60000); if (-not $click.HasExited) { Stop-Process -Id $click.Id -Force -ErrorAction SilentlyContinue } }
            # an installer window left open would sit on the desktop through every later measurement
            if (-not $p.HasExited) { & "$env:SystemRoot\System32\taskkill.exe" /PID $p.Id /T /F 2>$null | Out-Null; $outcome = "closed after ${InstallTimeoutSec}s without finishing" }
        }
        Write-Host "[install] $outcome"
        Start-Sleep -Seconds 30
        $after = Get-State
        $diff = [ordered]@{ product = $i.product; version = $i.version; outcome = $outcome; at = (Get-Date).ToString('s') }
        foreach ($k in @($before.Keys)) {
            $diff["new_$k"] = @($after[$k] | Where-Object { $before[$k] -notcontains $_ })
            $diff["removed_$k"] = @($before[$k] | Where-Object { $after[$k] -notcontains $_ })
        }
        [IO.File]::WriteAllText((Join-Path $diffDir "$($i.slug).json"), ($diff | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
        Write-Host ("[diff] programs +{0} services +{1} drivers +{2} minifilters +{3} rootcerts +{4} tasks +{5} autoruns +{6} processes +{7} ports +{8}" -f @($diff.new_uninstall).Count, @($diff.new_services).Count, @($diff.new_drivers).Count, @($diff.new_minifilters).Count, @($diff.new_rootCerts).Count, @($diff.new_tasks).Count, @($diff.new_autoruns).Count, @($diff.new_processes).Count, @($diff.new_listening).Count)
        $summary += [ordered]@{ slug = $i.slug; product = $i.product; outcome = $outcome; newPrograms = @($diff.new_uninstall); newServices = @($diff.new_services).Count; newDrivers = @($diff.new_drivers).Count }
        [IO.File]::WriteAllText("$runs\install-summary.json", (ConvertTo-Json @($summary) -Depth 4), (New-Object Text.UTF8Encoding($false)))
        & powershell @measureArgs -Label "after-$($i.slug)"
        Send-Collector "m slow"
        Start-Sleep -Seconds 45
    }
}
finally {
    try { Send-Collector 'q' } catch { }
    if ($c -and -not $c.WaitForExit(120000)) { Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue }
    Get-Process BlackBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $collector } | Stop-Process -Force -ErrorAction SilentlyContinue
}
& powershell -NoProfile -ExecutionPolicy Bypass -File "$here\perf-compare.ps1"
# read-only: error events since the first install, and version/signature/write access of each new service and driver binary
try { & powershell -NoProfile -ExecutionPolicy Bypass -File "$here\conflict-evidence.ps1" -Label 'after-install' } catch { Write-Host "[evidence] failed: $($_.Exception.Message)" -ForegroundColor Yellow }
Write-Host "`nDone. Reboot once, then run: perf-measure.ps1 -Label after-reboot and conflict-evidence.ps1 -Label after-reboot   (boot-loaded drivers only count after a restart)" -ForegroundColor Green
