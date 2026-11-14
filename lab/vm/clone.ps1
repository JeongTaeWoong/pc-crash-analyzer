# Case VMs: thin copies of the sealed base image (differencing disks). ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -List
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -New finsec-a [-Boot]      # VM "pca-finsec-a", checkpoint "clean" taken while off
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -New office1 -Spec office-low       # 2 CPUs, 4 GB (profiles in vm.json; default "standard")
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Start finsec-a [-Connect]
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Save finsec-a -Label installed
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Restore finsec-a -Label clean [-Boot]
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Stop finsec-a [-TurnOff]   # default: ask the guest to shut down
#   powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Remove finsec-a            # deletes the VM and its files
#
# Several clones may run at once for install / reproduction work. Never time anything while more than one VM runs.
# Refuses to start a VM while a measurement session is active.
param(
    [switch]$List,
    [string]$New,
    [string]$Start,
    [string]$Stop,
    [string]$Save,
    [string]$Restore,
    [string]$Remove,
    [string]$Label,
    [string]$Spec = 'standard',          # with -New: hardware profile from vm.json "profiles"
    [switch]$Boot,                         # with -New or -Restore: start the VM afterwards
    [switch]$Connect,
    [switch]$TurnOff,
    [string]$Root                          # other VM root (tests)
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
Assert-HyperV

function Require-Vm([string]$name) {
    $vmName = Get-CloneVmName $cfg $name
    $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
    if (-not $vm) { throw "No VM named $vmName (see -List)." }
    return $vm
}
function Start-Clone($vm) {
    Assert-NoLabSession
    if ($vm.State -ne 'Running') { Start-VM -Name $vm.Name }
    Write-Host "$($vm.Name): running"
    Write-RunningWarning $cfg
    if ($Connect) { Start-Process vmconnect.exe -ArgumentList 'localhost', $vm.Name }
}
function Check-Label { if ($Label -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$') { throw 'Give -Label (letters, digits, . _ -).' } }

if ($New) {
    $vmName = Get-CloneVmName $cfg $New
    $base = Get-BaseDisk $cfg
    if (-not $cfg.profiles.$Spec) { throw "Unknown profile '$Spec'. Known: $(($cfg.profiles.PSObject.Properties | ForEach-Object { $_.Name }) -join ', ')" }
    if (-not (Test-Path (Get-BaseInfoPath $cfg))) { throw 'The base image is not sealed yet (lab\vm\base.ps1 -Status).' }
    if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) { throw "A VM named $vmName already exists." }
    $dir = Get-CloneDir $cfg $New
    if (Test-Path $dir) { throw "Folder already exists: $dir" }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $child = Join-Path $dir 'disk.vhdx'
    try {
        New-VHD -Path $child -ParentPath $base -Differencing | Out-Null
        $vm = New-LabVm $cfg $vmName $child $dir
        Set-LabVmProfile $cfg $vmName $Spec
        Checkpoint-VM -Name $vmName -SnapshotName 'clean'
    } catch {
        Remove-LabVm $vmName | Out-Null
        Remove-DirWithRetry $dir | Out-Null
        throw
    }
    Write-Host "Created $vmName (profile $Spec, checkpoint 'clean'). Files: $dir"
    if ($Boot -or $Connect) { Start-Clone (Get-VM -Name $vmName) }
    exit 0
}

if ($Start) { Start-Clone (Require-Vm $Start); exit 0 }

if ($Stop) {
    $vm = Require-Vm $Stop
    if ($vm.State -eq 'Off') { Write-Host "$($vm.Name): already off"; exit 0 }
    if ($TurnOff) { Stop-VM -Name $vm.Name -TurnOff -Force } else { Stop-VM -Name $vm.Name -Force }
    Write-Host "$($vm.Name): $((Get-VM -Name $vm.Name).State)"
    exit 0
}

if ($Save) {
    Check-Label
    $vm = Require-Vm $Save
    if (Get-VMSnapshot -VMName $vm.Name -Name $Label -ErrorAction SilentlyContinue) { throw "Checkpoint '$Label' already exists on $($vm.Name)." }
    if (Test-LabVmGpu $vm.Name) {
        # Hyper-V refuses a checkpoint while a GPU partition is assigned: detach, save, attach again. Only possible while off.
        if ($vm.State -ne 'Off') { throw "$($vm.Name) has a GPU partition, so it must be shut down before a checkpoint (clone.ps1 -Stop $Save)." }
        $gpuKey = Get-LabVmNote $vm 'gpu'
        if (-not $gpuKey) { throw "$($vm.Name) has a GPU partition that gpu.ps1 did not attach; detach it by hand first." }
        Remove-LabVmGpu $vm.Name
        try { Checkpoint-VM -Name $vm.Name -SnapshotName $Label } finally { Add-LabVmGpu $cfg $vm.Name $gpuKey }
        Write-Host "$($vm.Name): checkpoint '$Label' saved without the GPU partition; partition '$gpuKey' attached again"
        exit 0
    }
    Checkpoint-VM -Name $vm.Name -SnapshotName $Label
    Write-Host "$($vm.Name): checkpoint '$Label' saved (VM was $($vm.State))"
    exit 0
}

if ($Restore) {
    Check-Label
    $vm = Require-Vm $Restore
    $snap = Get-VMSnapshot -VMName $vm.Name -Name $Label -ErrorAction SilentlyContinue
    if (-not $snap) { throw "No checkpoint '$Label' on $($vm.Name)." }
    $gpuKey = $null
    if (Test-LabVmGpu $vm.Name) { $gpuKey = Get-LabVmNote $vm 'gpu' }
    if ($vm.State -ne 'Off') { Stop-VM -Name $vm.Name -TurnOff -Force }
    Restore-VMSnapshot -VMName $vm.Name -Name $Label -Confirm:$false
    $settled = Wait-VmSettled $vm.Name
    Write-Host "$($vm.Name): restored to '$Label' (everything after it is gone), state $settled"
    if ($gpuKey -and -not (Test-LabVmGpu $vm.Name)) {
        # checkpoints never hold the GPU partition; put it back when the restored VM is off
        if ($settled -eq 'Off') { Add-LabVmGpu $cfg $vm.Name $gpuKey; Write-Host "$($vm.Name): GPU partition '$gpuKey' attached again" }
        else { Write-Host "$($vm.Name): NOTE the GPU partition '$gpuKey' is gone (this checkpoint holds a running state). Shut the VM down and run gpu.ps1 -Name $Restore -Attach." }
    }
    if ($Boot -or $Connect) { Start-Clone (Get-VM -Name $vm.Name) }
    exit 0
}

if ($Remove) {
    $vm = Require-Vm $Remove
    $dir = Get-CloneDir $cfg $Remove
    if (-not (Remove-LabVm $vm.Name)) { throw "Could not remove $($vm.Name) from Hyper-V." }
    if (-not (Remove-DirWithRetry $dir)) { throw "VM removed, but could not delete $dir" }
    Write-Host "$($vm.Name): removed with its files"
    exit 0
}

# -List (default)
$vms = @(Get-LabVms $cfg | Where-Object { $_.Name -ne $cfg.base_vm })
if (-not $vms.Count) { Write-Host 'No case VMs. Make one with -New <name>.'; exit 0 }
foreach ($v in $vms) {
    $short = $v.Name.Substring($cfg.prefix.Length)
    $dir = Get-CloneDir $cfg $short
    $size = 0
    if (Test-Path $dir) { $size = (Get-ChildItem $dir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum }
    $snaps = @(Get-VMSnapshot -VMName $v.Name -ErrorAction SilentlyContinue | Sort-Object CreationTime | ForEach-Object { $_.Name }) -join ', '
    Write-Host ("{0,-22} {1,-8} {2,-11} cpu {3} mem {4:N0} GB  {5,6:N1} GB on disk  checkpoints: {6}" -f $short, $v.State, (Get-LabVmProfileName $v), $v.ProcessorCount, ($v.MemoryStartup / 1GB), ($size / 1GB), $snaps)
}
Write-RunningWarning $cfg
