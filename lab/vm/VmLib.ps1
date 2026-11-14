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
    # 16:9 screen for the plain (console) connection, which is what unattended runs and screen.ps1 see
    try { Set-VMVideo -VMName $vmName -HorizontalResolution 1920 -VerticalResolution 1080 -ResolutionType Single } catch { Write-Host "note: could not set the video resolution ($($_.Exception.Message))" }
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

# Applies a hardware profile from vm.json to a VM that is off, and records the profile name in the VM notes.
function Set-LabVmProfile($cfg, [string]$vmName, [string]$profileName) {
    $p = $cfg.profiles.$profileName
    if (-not $p) { throw "Unknown profile '$profileName'. Known: $(($cfg.profiles.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')" }
    Set-VMProcessor -VMName $vmName -Count ([int]$p.cpu) -Maximum ([int]$p.cpu_cap_percent)
    Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false -StartupBytes ([int64]$p.memory_gb * 1GB)
    Get-VMHardDiskDrive -VMName $vmName | Set-VMHardDiskDrive -MaximumIOPS ([uint64]$p.disk_iops)
    Set-LabVmNote $vmName 'profile' $profileName
}
function Get-LabVmProfileName($vm) { $p = Get-LabVmNote $vm 'profile'; if ($p) { return $p } else { return 'standard' } }
# How much a VM is slowed down on purpose (CPU cap, disk limit), read from the VM itself. factor = by how much lab
# scripts stretch their waits and time limits for it (1 = not slowed down). First run on such a clone, 2026-11-12:
# installs that take 2-5 min elsewhere ran into the 20 min limit and loading a test program took 80 s.
function Get-LabVmSlowdown([string]$vmName, [double]$factorWhenSlow = 3) {
    $cap = [int](Get-VMProcessor -VMName $vmName).Maximum
    $iops = [int64](@(Get-VMHardDiskDrive -VMName $vmName | ForEach-Object { $_.MaximumIOPS }) | Measure-Object -Maximum).Maximum
    $slow = ($cap -lt 100 -or $iops -gt 0)
    return [pscustomobject]@{ cpu_cap_percent = $cap; disk_iops = $iops; factor = $(if ($slow) { $factorWhenSlow } else { 1 }) }
}

# ---- keyboard and screen of a VM through Hyper-V's WMI provider (root\virtualization\v2), no guest software needed ----
# Used to answer "Press any key to boot from CD or DVD" and to look at what a VM shows (installer windows) without a person.
# Input goes to virtual machines only; the lab never sends input to programs on the host (games).
$script:VirtNs = 'root\virtualization\v2'

function Get-VmWmiSystem([string]$vmName) {
    $vm = Get-VM -Name $vmName
    $cs = Get-CimInstance -Namespace $script:VirtNs -ClassName Msvm_ComputerSystem -Filter "Name='$($vm.Id.ToString().ToUpper())'"
    if (-not $cs) { throw "No WMI object for $vmName." }
    return $cs
}

# $keyCode = Windows virtual-key code (13 = Enter, 32 = Space, 27 = Esc).
function Send-VmKey([string]$vmName, [int]$keyCode) {
    $cs = Get-VmWmiSystem $vmName
    $kb = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_Keyboard | Select-Object -First 1
    if (-not $kb) { throw "No keyboard device on $vmName (is it running?)." }
    $r = Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]$keyCode }
    return [int]$r.ReturnValue
}

# Saves what the VM displays as a PNG. Returns the path.
function Save-VmScreenshot([string]$vmName, [string]$path, [int]$width = 1024, [int]$height = 768) {
    $cs = Get-VmWmiSystem $vmName
    $vssd = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VirtualSystemSettingData |
        Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } | Select-Object -First 1
    if (-not $vssd) { throw "No active settings object for $vmName." }
    $svc = Get-CimInstance -Namespace $script:VirtNs -ClassName Msvm_VirtualSystemManagementService
    $r = Invoke-CimMethod -InputObject $svc -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $vssd; WidthPixels = [uint16]$width; HeightPixels = [uint16]$height }
    if ($r.ReturnValue -ne 0 -or -not $r.ImageData) { throw "GetVirtualSystemThumbnailImage returned $($r.ReturnValue)." }
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    try {
        $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bmp.PixelFormat)
        [System.Runtime.InteropServices.Marshal]::Copy([byte[]]$r.ImageData, 0, $data.Scan0, [Math]::Min($r.ImageData.Length, $data.Stride * $height))
        $bmp.UnlockBits($data)
        New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bmp.Dispose() }
    return $path
}

# ---- VM notes as "key=value key=value" (profile, gpu) ----
function Get-LabVmNote($vm, [string]$key) { if ("$($vm.Notes)" -match "(?:^|\s)$key=([A-Za-z0-9_-]+)") { return $Matches[1] } else { return $null } }
function Set-LabVmNote([string]$vmName, [string]$key, [string]$value) {
    $parts = @("$((Get-VM -Name $vmName).Notes)" -split '\s+' | Where-Object { $_ -and $_ -notlike "$key=*" })
    if ($value) { $parts += "$key=$value" }
    Set-VM -Name $vmName -Notes ($parts -join ' ')
}

# ---- GPU partition (GPU-P): gives a VM a share of a host GPU. Verified on this PC 2026-11-12 without a guest OS:
# the adapter attaches and the VM starts; a checkpoint CANNOT be made while a partition is assigned (off or running);
# restoring a checkpoint taken before the adapter removes it. So clone.ps1 detaches around a checkpoint and re-attaches.
# The guest then needs the host's driver files (gpu.ps1 -InstallDriver): it runs the SAME driver version as the host.
function Test-LabVmGpu([string]$vmName) { return (@(Get-VMGpuPartitionAdapter -VMName $vmName -ErrorAction SilentlyContinue).Count -gt 0) }
function Get-HostGpu($cfg, [string]$gpuKey) {
    $ven = $cfg.gpus.$gpuKey
    if (-not $ven) { throw "Unknown GPU '$gpuKey'. Known: $(($cfg.gpus.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')" }
    $g = Get-VMHostPartitionableGpu | Where-Object { $_.Name -like "*$ven*" } | Select-Object -First 1
    if (-not $g) { throw "The host offers no partitionable GPU matching $ven." }
    return $g
}
function Add-LabVmGpu($cfg, [string]$vmName, [string]$gpuKey) {
    if ((Get-VM -Name $vmName).State -ne 'Off') { throw "$vmName must be off to attach a GPU partition." }
    if (Test-LabVmGpu $vmName) { throw "$vmName already has a GPU partition." }
    $g = Get-HostGpu $cfg $gpuKey
    Add-VMGpuPartitionAdapter -VMName $vmName -InstancePath $g.Name
    Set-VM -Name $vmName -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace 1GB -HighMemoryMappedIoSpace 32GB
    Set-LabVmNote $vmName 'gpu' $gpuKey
}
function Remove-LabVmGpu([string]$vmName) {
    if ((Get-VM -Name $vmName).State -ne 'Off') { throw "$vmName must be off to detach its GPU partition." }
    Get-VMGpuPartitionAdapter -VMName $vmName -ErrorAction SilentlyContinue | Remove-VMGpuPartitionAdapter
    Set-LabVmNote $vmName 'gpu' $null
}

# ---- running things inside a guest ----
# Plain PowerShell Direct commands have no desktop. Installers, browsers and other programs with windows must run in the
# session of the logged-on user: this registers a one-off scheduled task for that user (interactive, highest privileges =
# elevated without a UAC prompt, because the lab account is an administrator), starts it and waits for it to finish.
# Returns an object: ExitCode (null = timed out), Log (text the script wrote), TimedOut.
function Invoke-GuestDesktop($session, [string]$scriptText, [int]$timeoutSec = 900) {
    $id = [Guid]::NewGuid().ToString('N').Substring(0, 12)
    Invoke-Command -Session $session -ArgumentList $id, $scriptText -ScriptBlock {
        param($id, $scriptText)
        $dir = 'C:\lab\jobs'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $job = Join-Path $dir "$id.ps1"; $wrap = Join-Path $dir "$id.wrap.ps1"; $log = Join-Path $dir "$id.log"; $done = Join-Path $dir "$id.done"
        [IO.File]::WriteAllText($job, $scriptText, (New-Object Text.UTF8Encoding($true)))
        $w = @"
`$ErrorActionPreference = 'Continue'
`$code = 0
try { & '$job' *>&1 | Out-File -FilePath '$log' -Encoding UTF8; if (`$LASTEXITCODE) { `$code = `$LASTEXITCODE } }
catch { "ERROR: `$(`$_.Exception.Message)" | Out-File -FilePath '$log' -Encoding UTF8 -Append; `$code = 1 }
"`$code" | Out-File -FilePath '$done' -Encoding ASCII
"@
        [IO.File]::WriteAllText($wrap, $w, (New-Object Text.UTF8Encoding($true)))
        $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$wrap`""
        $p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
        $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 6)
        Register-ScheduledTask -TaskName "pca-job-$id" -Action $a -Principal $p -Settings $s -Force | Out-Null
        Start-ScheduledTask -TaskName "pca-job-$id"
    }
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $code = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $c = Invoke-Command -Session $session -ArgumentList $id -ScriptBlock { param($id) $d = "C:\lab\jobs\$id.done"; if (Test-Path $d) { (Get-Content $d -Raw).Trim() } }
        if ($null -ne $c -and "$c" -ne '') { $code = [int]$c; break }
    }
    $log = Invoke-Command -Session $session -ArgumentList $id -ScriptBlock {
        param($id)
        $l = "C:\lab\jobs\$id.log"
        $text = if (Test-Path $l) { Get-Content $l -Raw -Encoding UTF8 } else { '' }
        Unregister-ScheduledTask -TaskName "pca-job-$id" -Confirm:$false -ErrorAction SilentlyContinue
        $text
    }
    return [pscustomobject]@{ ExitCode = $code; Log = "$log".Trim(); TimedOut = ($null -eq $code) }
}

function New-GuestSession($cfg, [string]$name) {
    $vmName = Get-CloneVmName $cfg $name
    $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
    if (-not $vm) { throw "No VM named $vmName." }
    if ($vm.State -ne 'Running') { throw "$vmName is $($vm.State). Start it:  lab\vm\clone.ps1 -Start $name" }
    return New-PSSession -VMName $vmName -Credential (Get-GuestCredential)
}

# Waits until Windows in the guest answers and the lab user's desktop (explorer) is up.
function Wait-GuestDesktop($cfg, [string]$name, [int]$timeoutSec = 300) {
    $vmName = Get-CloneVmName $cfg $name
    $cred = Get-GuestCredential
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $ok = Invoke-Command -VMName $vmName -Credential $cred -ErrorAction Stop -ScriptBlock { [bool](Get-Process explorer -ErrorAction SilentlyContinue) }
            if ($ok) { return $true }
        } catch { }
        Start-Sleep -Seconds 5
    }
    return $false
}
