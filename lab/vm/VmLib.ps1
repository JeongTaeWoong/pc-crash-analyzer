# Shared helpers for lab\vm\base.ps1, clone.ps1, guest.ps1. ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI).
# Everything here runs from a normal (not elevated) PowerShell of a member of "Hyper-V Administrators" (lab\vm\enable-hyperv.ps1 -AddMeToHyperVAdmins).
$ErrorActionPreference = 'Stop'
$script:VmDir = $PSScriptRoot
$script:LabDir = Split-Path $PSScriptRoot -Parent

function Get-VmConfig([string]$RootOverride) {
    $c = Get-Content (Join-Path $script:VmDir 'vm.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($RootOverride) { $c.root = $RootOverride }
    return $c
}

function Assert-HyperV {
    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw 'The Hyper-V PowerShell module is missing. Run lab\vm\enable-hyperv.ps1 and restart.' }
    try { Get-VMHost | Out-Null } catch { throw "No permission to manage Hyper-V ($($_.Exception.Message)). Run lab\vm\enable-hyperv.ps1 -AddMeToHyperVAdmins, then sign out and in." }
}

# A VM that runs during a measurement session takes CPU from the measurement (lab.json forbidden_processes has vmwp for the other direction).
function Assert-NoLabSession {
    $stateFile = Join-Path $script:LabDir 'runs\state.json'
    if (-not (Test-Path $stateFile)) { return }
    $active = $false
    try { $active = [bool](Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json).session.active } catch { }
    if ($active) { throw 'A measurement session is active (lab\runs\state.json). Virtual machines must stay off until it ends (lab\stop-session.ps1).' }
}

function Get-BaseDir($cfg) { Join-Path $cfg.root 'base' }
function Get-BaseDisk($cfg) { Join-Path (Get-BaseDir $cfg) 'base.vhdx' }
function Get-BaseInfoPath($cfg) { Join-Path (Get-BaseDir $cfg) 'base-info.json' }
function Get-CloneDir($cfg, [string]$name) { Join-Path (Join-Path $cfg.root 'clones') $name }

function Get-CloneVmName($cfg, [string]$name) {
    if ($name -notmatch '^[a-z0-9][a-z0-9-]{0,30}$') { throw "Bad name '$name': use lower-case letters, digits and '-' (at most 31 characters)." }
    $vm = "$($cfg.prefix)$name"
    if ($vm -eq $cfg.base_vm) { throw "The name '$name' is reserved for the base image." }
    return $vm
}

# One place for the hardware of every lab VM: generation 2, secure boot, virtual TPM (Windows 11 needs both), fixed memory,
# no automatic checkpoints, no automatic start with the host.
function New-LabVm($cfg, [string]$vmName, [string]$vhdPath, [string]$dir) {
    $vm = New-VM -Name $vmName -Generation 2 -MemoryStartupBytes ([int64]$cfg.memory_gb * 1GB) -VHDPath $vhdPath -Path $dir -SwitchName $cfg.switch
    Set-VMProcessor -VMName $vmName -Count ([int]$cfg.cpu)
    Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false
    Set-VMFirmware -VMName $vmName -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'
    Set-VMKeyProtector -VMName $vmName -NewLocalKeyProtector
    Enable-VMTPM -VMName $vmName
    Set-VM -Name $vmName -CheckpointType Standard -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction ShutDown
    return $vm
}

function Get-LabVms($cfg) { @(Get-VM | Where-Object { $_.Name -like "$($cfg.prefix)*" } | Sort-Object Name) }

function Write-RunningWarning($cfg) {
    $n = @(Get-VM | Where-Object { $_.State -eq 'Running' }).Count
    if ($n -gt [int]$cfg.warn_running_above) {
        Write-Host ("NOTE: {0} virtual machines are running (more than {1}). Fine for install/reproduction work on an idle host; never for timing." -f $n, $cfg.warn_running_above)
    }
}

# The guest account password never goes into the repository or a command line: it is kept encrypted for this Windows user (DPAPI).
function Get-GuestCredentialPath { Join-Path (Join-Path $env:LOCALAPPDATA 'pca-lab') 'guest-cred.xml' }
function Get-GuestCredential {
    $p = Get-GuestCredentialPath
    if (-not (Test-Path $p)) { throw 'No guest credential saved. Run:  powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -SetCredential' }
    return Import-Clixml $p
}

function Remove-DirWithRetry([string]$dir) {
    for ($i = 0; $i -lt 10; $i++) {
        if (-not (Test-Path $dir)) { return $true }
        try { Remove-Item $dir -Recurse -Force -ErrorAction Stop; return $true } catch { Start-Sleep -Seconds 2 }
    }
    return (-not (Test-Path $dir))
}

# Right after a restore or a failed start Hyper-V can report a state that is still changing (seen 2026-11-12: "Running" for
# about 4 s after a restore to a checkpoint taken while off). Wait until the state stops changing before acting on it.
function Wait-VmSettled([string]$vmName, [int]$TimeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $last = $null; $same = 0
    while ((Get-Date) -lt $deadline) {
        $s = "$((Get-VM -Name $vmName).State)"
        if ($s -eq $last) { $same++ } else { $same = 0; $last = $s }
        if ($same -ge 5) { break }
        Start-Sleep -Seconds 1
    }
    return $last
}

# Turns a VM off (if needed) and deletes it from Hyper-V; retries because a VM that just failed to start can be busy for a moment.
function Remove-LabVm([string]$vmName) {
    for ($i = 0; $i -lt 10; $i++) {
        $v = Get-VM -Name $vmName -ErrorAction SilentlyContinue
        if (-not $v) { return $true }
        try {
            if ($v.State -ne 'Off') { Stop-VM -Name $vmName -TurnOff -Force -ErrorAction Stop }
            Remove-VM -Name $vmName -Force -ErrorAction Stop
        } catch { Start-Sleep -Seconds 2 }
    }
    return (-not (Get-VM -Name $vmName -ErrorAction SilentlyContinue))
}
