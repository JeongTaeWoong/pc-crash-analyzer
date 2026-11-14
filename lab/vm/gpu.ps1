# GPU for a case VM: a partition of a host GPU (Hyper-V GPU-P). ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Status
#   powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -Attach [-Gpu igpu|nvidia]    # VM must be off
#   powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -InstallDriver                # VM running, guest credential saved
#   powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -Detach                       # VM must be off
#
# Facts checked on this PC (2026-11-12, no guest OS): the partition attaches and the VM starts; a checkpoint cannot be made
# while a partition is assigned, so clone.ps1 -Save / -Restore detach and re-attach it (the VM must be off for -Save).
# NOT yet checked: -InstallDriver and whether Windows in the guest then shows a working adapter. Microsoft does not support
# this arrangement for ordinary VMs on client Windows; treat a failure here as "unsupported", not as a bug to chase for days.
# Limits to keep in mind when writing up a case made with it:
#   - the guest runs the host's driver files (same version): it cannot reproduce "another driver version" problems;
#   - default GPU is the integrated one (igpu) so that a guest fault cannot disturb the GPU the user works and plays on.
param(
    [switch]$Status,
    [string]$Name,
    [switch]$Attach,
    [switch]$Detach,
    [switch]$InstallDriver,
    [string]$Gpu = 'igpu',
    [string]$Root
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig $Root
Assert-HyperV

if ($Status -or -not $Name) {
    Write-Host 'Host GPUs that can be partitioned:'
    foreach ($p in $cfg.gpus.PSObject.Properties) {
        $g = Get-VMHostPartitionableGpu | Where-Object { $_.Name -like "*$($p.Value)*" } | Select-Object -First 1
        Write-Host ("  {0,-8} {1,-10} {2}" -f $p.Name, $p.Value, $(if ($g) { 'available' } else { 'not offered by the host' }))
    }
    Write-Host 'Lab VMs with a GPU partition:'
    $any = $false
    foreach ($v in Get-LabVms $cfg) { if (Test-LabVmGpu $v.Name) { $any = $true; Write-Host ("  {0,-28} {1,-8} gpu={2}" -f $v.Name, $v.State, (Get-LabVmNote $v 'gpu')) } }
    if (-not $any) { Write-Host '  none' }
    exit 0
}

$vmName = Get-CloneVmName $cfg $Name
$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
if (-not $vm) { throw "No VM named $vmName." }

if ($Attach) {
    Add-LabVmGpu $cfg $vmName $Gpu
    Write-Host "${vmName}: GPU partition '$Gpu' attached. Next: start the VM and run gpu.ps1 -Name $Name -InstallDriver (once per clone or once in a checkpoint)."
    exit 0
}

if ($Detach) {
    if (-not (Test-LabVmGpu $vmName)) { Write-Host "${vmName}: no GPU partition."; exit 0 }
    Remove-LabVmGpu $vmName
    Write-Host "${vmName}: GPU partition detached."
    exit 0
}

if ($InstallDriver) {
    # Copies the files of the host's display driver into the guest, where a partitioned GPU expects them:
    #   host  C:\Windows\System32\DriverStore\FileRepository\<package>\...  ->  guest C:\Windows\System32\HostDriverStore\FileRepository\<package>\...
    #   other driver files (System32, SysWOW64)                             ->  the same path in the guest
    $gpuKey = Get-LabVmNote $vm 'gpu'
    if (-not $gpuKey -or -not (Test-LabVmGpu $vmName)) { throw "$vmName has no GPU partition attached by gpu.ps1 (-Attach first)." }
    if ($vm.State -ne 'Running') { throw "$vmName must be running (clone.ps1 -Start $Name)." }
    $cred = Get-GuestCredential
    $ven = $cfg.gpus.$gpuKey
    $drv = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceClass -eq 'DISPLAY' -and $_.DeviceID -like "*$ven*" } | Select-Object -First 1
    if (-not $drv) { throw "No display driver found on the host for $ven." }
    $files = @(Get-CimInstance Win32_PNPSignedDriverCIMDataFile | Where-Object { $_.Antecedent.DeviceID -eq $drv.DeviceID } | ForEach-Object { $_.Dependent.Name })
    if (-not $files.Count) { throw 'The host driver lists no files.' }
    $store = (Join-Path $env:windir 'System32\DriverStore\FileRepository').ToLower()
    $packages = @($files | Where-Object { $_.ToLower().StartsWith($store) } | ForEach-Object { ($_.Substring($store.Length + 1) -split '\\')[0] } | Sort-Object -Unique)
    $loose = @($files | Where-Object { -not $_.ToLower().StartsWith($store) })
    Write-Host ("host driver {0} {1}: {2} driver package folder(s), {3} other file(s)" -f $drv.DeviceName, $drv.DriverVersion, $packages.Count, $loose.Count)
    $s = New-PSSession -VMName $vmName -Credential $cred
    try {
        Invoke-Command -Session $s -ScriptBlock { New-Item -ItemType Directory -Force -Path 'C:\Windows\System32\HostDriverStore\FileRepository' | Out-Null }
        # The running host driver keeps some files open (seen 2026-11-12: a .log in the package). Stage a copy on the host first,
        # skipping what cannot be read, then send the staged folder.
        $stage = Join-Path $env:TEMP 'pca-gpu-stage'
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        foreach ($p in $packages) {
            $dst = Join-Path $stage $p
            & robocopy.exe (Join-Path $store $p) $dst /E /R:0 /W:0 /XF *.log /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 16) { throw "Staging $p failed (robocopy exit $LASTEXITCODE)." }
            $n = @(Get-ChildItem $dst -Recurse -File).Count
            Invoke-Command -Session $s -ArgumentList $p -ScriptBlock { param($p) $d = Join-Path 'C:\Windows\System32\HostDriverStore\FileRepository' $p; if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
            Copy-Item -Path $dst -Destination 'C:\Windows\System32\HostDriverStore\FileRepository' -ToSession $s -Recurse -Force
            Write-Host "  copied package $p ($n files; unreadable and .log files skipped, robocopy exit $LASTEXITCODE)"
        }
        $copied = 0; $skipped = 0
        foreach ($f in $loose) {
            if (-not (Test-Path $f)) { $skipped++; continue }
            try { Copy-Item -Path $f -Destination $f -ToSession $s -Force -ErrorAction Stop; $copied++ } catch { $skipped++ }
        }
        if ($loose.Count) { Write-Host "  other files: $copied copied, $skipped skipped" }
    } finally {
        Remove-PSSession $s
        if ($stage -and (Test-Path $stage)) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host "Done. Restart the guest (clone.ps1 -Stop $Name, then -Start), then check Device Manager in the guest: the adapter should work without a warning sign."
    exit 0
}

throw 'Nothing to do: give -Attach, -Detach, -InstallDriver or -Status.'
