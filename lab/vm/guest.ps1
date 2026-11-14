# Run things inside a case VM from the host (PowerShell Direct: no network needed, works over the VM bus). ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -SetCredential
#        asks once for the local administrator account of the base image and keeps it encrypted for this Windows user
#        (%LOCALAPPDATA%\pca-lab\guest-cred.xml, DPAPI). Use an account that exists only inside the VMs, not a real password.
#   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -Wait [-TimeoutSec 300]     # until Windows in the VM answers
#   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -Run "Get-Process | Select-Object -First 5"
#   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -CopyTo C:\host\file.exe -Dest C:\lab\file.exe
#   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -CopyFrom C:\lab\dumps -Dest F:\pca-vm\out\finsec-a
#
# STATUS 2026-11-12: written, NOT yet run against a real guest (no Windows image on this PC yet). PowerShell Direct needs a
# Windows 10+/Server 2016+ guest and a host user in "Hyper-V Administrators"; whether a NOT elevated session is enough on this PC is unverified.
# A command run this way has no desktop: programs with windows (installers, browsers) must be started inside the guest's own
# logon session (a scheduled task in the guest or the lab runner copied into it), not through -Run.
param(
    [switch]$SetCredential,
    [string]$Name,
    [switch]$Wait,
    [int]$TimeoutSec = 300,
    [string]$Run,
    [string]$RunFile,                     # host path of a .ps1 to run in the guest
    [switch]$Desktop,                     # with -RunFile: run in the logged-on desktop session (windows, installers), elevated
    [string]$CopyTo,
    [string]$CopyFrom,
    [string]$Dest,
    [string]$Root
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root

if ($SetCredential) {
    $p = Get-GuestCredentialPath
    New-Item -ItemType Directory -Force -Path (Split-Path $p -Parent) | Out-Null
    $c = Get-Credential -Message 'Local administrator account inside the lab VMs (not your real Windows or Microsoft password)'
    if (-not $c) { Write-Host 'Cancelled. Nothing saved.'; exit 1 }
    $c | Export-Clixml -Path $p
    Write-Host "Saved for this Windows user only: $p"
    exit 0
}

Assert-HyperV
if (-not $Name) { throw 'Give -Name <clone name> (see lab\vm\clone.ps1 -List).' }
$vmName = Get-CloneVmName $cfg $Name
$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
if (-not $vm) { throw "No VM named $vmName." }
if ($vm.State -ne 'Running') { throw "$vmName is $($vm.State). Start it:  lab\vm\clone.ps1 -Start $Name" }
$cred = Get-GuestCredential

function Test-Guest {
    try { Invoke-Command -VMName $vmName -Credential $cred -ScriptBlock { $env:COMPUTERNAME } -ErrorAction Stop | Out-Null; return $true } catch { $script:lastError = $_.Exception.Message; return $false }
}

if ($Wait) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Guest) { Write-Host "$vmName answers."; exit 0 }
        Start-Sleep -Seconds 5
    }
    Write-Host "$vmName did not answer within $TimeoutSec s. Last error: $script:lastError"
    exit 1
}

if ($Run) {
    Invoke-Command -VMName $vmName -Credential $cred -ScriptBlock ([scriptblock]::Create($Run))
    exit 0
}


if ($RunFile) {
    # Runs a script file from the host inside the guest. With -Desktop it runs in the logged-on user's desktop session
    # (needed for anything with a window), elevated; without, as a plain background command.
    if (-not (Test-Path $RunFile)) { throw "Not found on the host: $RunFile" }
    $text = Get-Content $RunFile -Raw -Encoding UTF8
    if ($Desktop) {
        $s = New-PSSession -VMName $vmName -Credential $cred
        try { $r = Invoke-GuestDesktop $s $text $TimeoutSec } finally { Remove-PSSession $s }
        if ($r.Log) { Write-Host $r.Log }
        if ($r.TimedOut) { Write-Host "TIMED OUT after $TimeoutSec s (the job may still be running in the guest)."; exit 124 }
        exit $r.ExitCode
    }
    Invoke-Command -VMName $vmName -Credential $cred -ScriptBlock ([scriptblock]::Create($text))
    exit 0
}

if ($CopyTo -or $CopyFrom) {
    if (-not $Dest) { throw 'Give -Dest.' }
    $s = New-PSSession -VMName $vmName -Credential $cred
    try {
        if ($CopyTo) {
            if (-not (Test-Path $CopyTo)) { throw "Not found on the host: $CopyTo" }
            Copy-Item -Path $CopyTo -Destination $Dest -ToSession $s -Recurse -Force
            Write-Host "host $CopyTo  ->  guest $Dest"
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path $Dest -Parent) -ErrorAction SilentlyContinue | Out-Null
            Copy-Item -Path $CopyFrom -Destination $Dest -FromSession $s -Recurse -Force
            Write-Host "guest $CopyFrom  ->  host $Dest"
        }
    } finally { Remove-PSSession $s }
    exit 0
}

throw 'Nothing to do: give -Wait, -Run, -RunFile, -CopyTo or -CopyFrom.'
