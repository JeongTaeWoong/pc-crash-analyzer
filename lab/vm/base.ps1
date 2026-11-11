# The base image: one clean Windows installation that every case VM is cloned from. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Status
#   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Create -Iso "D:\path\Win11_Enterprise_Eval.iso"
#        makes the VM "pca-base" (settings in vm.json), attaches the ISO, starts it and opens its window.
#        YOU install Windows by hand in that window, once (steps: lab\README.md, section on virtual machines).
#   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Seal
#        after Windows is installed and prepared: shuts the guest down, detaches the ISO, deletes the VM but keeps its disk,
#        marks the disk read-only. From then on the disk is only a parent for clones (lab\vm\clone.ps1) and must never change:
#        a changed parent breaks every clone. To change the base, remove the clones and build a new base.
#   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Discard
#        deletes an unsealed base (VM and disk) to start over. Refuses when the base is sealed or clones exist.
#
# Changes nothing on the host besides files under the VM root (vm.json "root") and Hyper-V's own VM list.
param(
    [switch]$Status,
    [switch]$Create,
    [string]$Iso,
    [switch]$Seal,
    [switch]$Discard,
    [switch]$NoConnect,                    # do not open the VM window (tests)
    [string]$Root                          # other VM root (tests)
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
Assert-HyperV
$baseDir = Get-BaseDir $cfg
$disk = Get-BaseDisk $cfg
$info = Get-BaseInfoPath $cfg
$baseVm = $cfg.base_vm

function Show-Status {
    Write-Host ("VM root        {0}" -f $cfg.root)
    $sealed = Test-Path $info
    if (Test-Path $disk) {
        Write-Host ("base disk      {0}  {1:N1} GB on disk, {2}" -f $disk, ((Get-Item $disk).Length / 1GB), $(if ($sealed) { 'sealed' } else { 'NOT sealed (installation in progress)' }))
    } else { Write-Host 'base disk      none (run -Create -Iso ...)' }
    $vms = Get-LabVms $cfg
    if (-not $vms.Count) { Write-Host 'virtual machines  none' }
    foreach ($v in $vms) {
        $snaps = @(Get-VMSnapshot -VMName $v.Name -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', '
        Write-Host ("  {0,-28} {1,-8} cpu {2} mem {3:N0} GB  checkpoints: {4}" -f $v.Name, $v.State, $v.ProcessorCount, ($v.MemoryStartup / 1GB), $(if ($snaps) { $snaps } else { '-' }))
    }
    $drive = Get-PSDrive -Name ($cfg.root.Substring(0, 1)) -ErrorAction SilentlyContinue
    if ($drive) { Write-Host ("free on {0}:     {1:N0} GB" -f $drive.Name, ($drive.Free / 1GB)) }
    Write-RunningWarning $cfg
}

if ($Status -or -not ($Create -or $Seal -or $Discard)) { Show-Status; exit 0 }

if ($Create) {
    Assert-NoLabSession
    if (-not $Iso) { throw 'Give the Windows ISO: -Iso "path\to\file.iso"' }
    if (-not (Test-Path $Iso)) { throw "ISO not found: $Iso" }
    if (Test-Path $disk) { throw "A base disk already exists: $disk  (see -Status; -Discard removes an unsealed base)" }
    if (Get-VM -Name $baseVm -ErrorAction SilentlyContinue) { throw "A VM named $baseVm already exists." }
    if (Test-Path $baseDir) { throw "Folder already exists: $baseDir" }
    New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
    try {
        New-VHD -Path $disk -SizeBytes ([int64]$cfg.disk_gb * 1GB) -Dynamic | Out-Null
        New-LabVm $cfg $baseVm $disk $baseDir | Out-Null
        $dvd = Add-VMDvdDrive -VMName $baseVm -Path (Resolve-Path $Iso).Path -Passthru
        Set-VMFirmware -VMName $baseVm -FirstBootDevice $dvd
        Start-VM -Name $baseVm
    } catch {
        # leave nothing half-made behind (for example when the ISO file is not a readable disc image)
        $why = $_.Exception.Message
        $vmGone = Remove-LabVm $baseVm
        $dirGone = $false
        if ($vmGone) { $dirGone = Remove-DirWithRetry $baseDir }
        if ($vmGone -and $dirGone) { Write-Host "Could not create or start the base VM; nothing was kept. Reason: $why" }
        else { Write-Host "Could not create or start the base VM, and the clean-up was incomplete (VM removed: $vmGone, folder removed: $dirGone). Run -Discard. Reason: $why" }
        exit 3
    }
    Write-Host "Created and started $baseVm ($($cfg.cpu) CPUs, $($cfg.memory_gb) GB, disk up to $($cfg.disk_gb) GB at $disk)."
    Write-Host 'In the VM window: press a key at "Press any key to boot from CD or DVD", then install Windows (steps in lab\README.md).'
    Write-Host 'When Windows is installed and prepared, run:  lab\vm\base.ps1 -Seal'
    if (-not $NoConnect) { Start-Process vmconnect.exe -ArgumentList 'localhost', $baseVm }
    exit 0
}

if ($Discard) {
    if (Test-Path $info) { throw 'The base is sealed. Remove the clones first (clone.ps1 -Remove), then delete the base folder by hand if you really mean it.' }
    $vm = Get-VM -Name $baseVm -ErrorAction SilentlyContinue
    if ($vm) {
        if (-not (Remove-LabVm $baseVm)) { throw "Could not remove $baseVm from Hyper-V." }
    }
    if (-not (Remove-DirWithRetry $baseDir)) { throw "Could not delete $baseDir" }
    Write-Host 'Unsealed base discarded.'
    exit 0
}

if ($Seal) {
    if (Test-Path $info) { throw 'The base is already sealed.' }
    $vm = Get-VM -Name $baseVm -ErrorAction SilentlyContinue
    if (-not $vm) { throw "No VM named $baseVm. Run -Create first." }
    if ($vm.State -ne 'Off') {
        Write-Host 'Asking the guest to shut down (up to 5 minutes)...'
        Stop-VM -Name $baseVm -Force
        $deadline = (Get-Date).AddMinutes(5)
        while ((Get-VM -Name $baseVm).State -ne 'Off' -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
        if ((Get-VM -Name $baseVm).State -ne 'Off') { throw 'The guest did not shut down. Shut Windows down inside the VM, then run -Seal again.' }
    }
    if (@(Get-VMSnapshot -VMName $baseVm -ErrorAction SilentlyContinue).Count) { throw 'The base VM has checkpoints. Delete them in Hyper-V Manager (they merge into the disk), wait for the merge, then run -Seal again.' }
    $attached = @(Get-VMHardDiskDrive -VMName $baseVm | ForEach-Object { $_.Path })
    if ($attached.Count -ne 1 -or $attached[0] -ne $disk) { throw "Unexpected disks on ${baseVm}: $($attached -join ', ')" }
    Get-VMDvdDrive -VMName $baseVm | Remove-VMDvdDrive
    Remove-VM -Name $baseVm -Force
    $item = Get-Item $disk
    $item.IsReadOnly = $true
    [pscustomobject]@{
        schema = 1; sealed_at = (Get-Date).ToString('o'); disk = $disk; disk_bytes = $item.Length
        cpu = $cfg.cpu; memory_gb = $cfg.memory_gb; note = 'Parent of every clone. Never attach it to a VM directly and never modify it.'
    } | ConvertTo-Json | Set-Content -Path $info -Encoding UTF8
    Write-Host ("Sealed: {0} ({1:N1} GB). Make case VMs with:  lab\vm\clone.ps1 -New <name>" -f $disk, ($item.Length / 1GB))
    exit 0
}
