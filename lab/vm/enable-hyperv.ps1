# Turns on Hyper-V on this PC (decision of 2026-11-11, docs/capstone-plan-v2.md). ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\enable-hyperv.ps1 -Check      # show the current state only (no elevation, changes nothing)
#   powershell -ExecutionPolicy Bypass -File lab\vm\enable-hyperv.ps1             # turn it on: asks for elevation once (UAC "Yes"). Does NOT restart.
#   powershell -ExecutionPolicy Bypass -File lab\vm\enable-hyperv.ps1 -AddMeToHyperVAdmins
#        also adds the current user to the local group "Hyper-V Administrators" (S-1-5-32-578), so that virtual machines
#        can later be created and driven from a normal, not elevated, PowerShell (takes effect at the next sign-in).
#
# What changes: the Windows feature Microsoft-Hyper-V-All (hypervisor, services, management tools, PowerShell module). Effective after a restart.
#   The host then loads other drivers and services, so lab measurements taken before and after are different experiments
#   (the experiment in lab\runs is archived and measured again from the pilot; see lab\README.md).
# Refuses to run while a measurement session is active.
# Undo (elevated): Disable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All   and restart.
# A window that starts in C:\WINDOWS\system32 is not necessarily elevated; this script does not care, it asks by itself.
param(
    [switch]$Check,
    [switch]$AddMeToHyperVAdmins,
    [switch]$Elevated,                                             # internal: the elevated half
    [string]$User = "$env:USERDOMAIN\$env:USERNAME",
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'pca-lab-enable-hyperv.log')
)
$ErrorActionPreference = 'Stop'
$lab = Split-Path $PSScriptRoot -Parent
$feature = 'Microsoft-Hyper-V-All'
function Out($m) { Write-Host $m; if ($Elevated) { try { Add-Content -Path $Log -Value $m -Encoding UTF8 } catch { } } }
function Show-State {
    $names = @('Microsoft-Hyper-V-All', 'Microsoft-Hyper-V-Hypervisor', 'Microsoft-Hyper-V-Services', 'Microsoft-Hyper-V-Management-PowerShell')
    foreach ($n in $names) {
        $f = Get-CimInstance Win32_OptionalFeature -Filter "Name='$n'" -ErrorAction SilentlyContinue
        $s = if (-not $f) { 'not offered on this edition' } else { switch ($f.InstallState) { 1 { 'enabled' } 2 { 'disabled' } 3 { 'absent' } default { "state $($f.InstallState)" } } }
        Out ('  feature {0,-42} {1}' -f $n, $s)
    }
    foreach ($svc in @('vmms', 'vmcompute')) {
        $x = Get-Service $svc -ErrorAction SilentlyContinue
        Out ('  service {0,-42} {1}' -f $svc, $(if ($x) { "$($x.Status)" } else { 'not installed' }))
    }
    Out ('  hypervisor running                                 {0}  (true already with memory integrity; it does not mean Hyper-V is on)' -f (Get-CimInstance Win32_ComputerSystem).HypervisorPresent)
    Out ('  PowerShell module (Get-VM)                         {0}' -f [bool](Get-Command Get-VM -ErrorAction SilentlyContinue))
}

if ($Check) { Write-Host 'Hyper-V state:'; Show-State; exit 0 }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $Elevated) {
    $stateFile = Join-Path $lab 'runs\state.json'
    if (Test-Path $stateFile) {
        try { if ((Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json).session.active) { Write-Host 'A measurement session is active. Stop it first (lab\stop-session.ps1).'; exit 1 } } catch { }
    }
    Write-Host 'Before:'; Show-State
    Remove-Item $Log -ErrorAction SilentlyContinue
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-User', "`"$User`"", '-Log', "`"$Log`"")
    if ($AddMeToHyperVAdmins) { $a += '-AddMeToHyperVAdmins' }
    if ($isAdmin) { & powershell.exe @a }
    else {
        Write-Host 'A UAC prompt will appear: choose "Yes". (This turns on a Windows feature; the PC is not restarted.)'
        try { Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList $a | Out-Null }
        catch { Write-Host "Elevation was not granted ($($_.Exception.Message)). Nothing was changed."; exit 2 }
        if (Test-Path $Log) { Get-Content $Log -Encoding UTF8 } else { Write-Host 'The elevated half left no log; nothing is known to have changed.'; exit 2 }
    }
    Write-Host ''
    Write-Host 'Next: restart Windows yourself when convenient, then run this script with -Check. Expected after the restart: feature enabled, services vmms and vmcompute present, Get-VM available.'
    exit 0
}

# ---- elevated half ----
if (-not $isAdmin) { Out 'ERROR: the elevated half is not elevated.'; exit 2 }
try {
    $r = Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart
    Out "Enable-WindowsOptionalFeature ${feature}: done, restart needed = $($r.RestartNeeded)"
} catch { Out "ERROR: Enable-WindowsOptionalFeature failed: $($_.Exception.Message)"; exit 3 }
if ($AddMeToHyperVAdmins) {
    try {
        $already = @(Get-LocalGroupMember -SID 'S-1-5-32-578' -ErrorAction Stop | Where-Object { $_.Name -ieq $User }).Count -gt 0
        if ($already) { Out "Hyper-V Administrators: $User is already a member" }
        else { Add-LocalGroupMember -SID 'S-1-5-32-578' -Member $User -ErrorAction Stop; Out "Hyper-V Administrators: added $User (effective at the next sign-in)" }
    } catch { Out "Hyper-V Administrators: could not add $User ($($_.Exception.Message)). Virtual machines then need an elevated PowerShell." }
}
Out 'After (features show as enabled only after the restart):'
Show-State
exit 0
