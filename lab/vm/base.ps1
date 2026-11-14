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
    [switch]$Open,                         # start the unsealed base VM (if off) and open its window
    [switch]$EditSession,                  # with -Open: show the connection settings dialog
    [switch]$Prepare,                      # before -Seal: guest settings every clone inherits (needs guest.ps1 -SetCredential)
    [switch]$NoAutoLogon,                  # with -Prepare: do not set automatic logon
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

if ($Status -or -not ($Create -or $Seal -or $Discard -or $Prepare -or $Open)) { Show-Status; exit 0 }

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
    # The firmware asks "Press any key to boot from CD or DVD" for a few seconds; answer it so nobody has to be quick.
    $sent = 0
    try { for ($i = 0; $i -lt 16; $i++) { if ((Send-VmKey $baseVm 13) -eq 0) { $sent++ }; Start-Sleep -Milliseconds 500 } } catch { Write-Host "Could not press the key for you ($($_.Exception.Message))." }
    if ($sent) { Write-Host "Pressed Enter $sent times during the first 8 s so the VM boots from the ISO." }
    Write-Host 'In the VM window: install Windows (steps in lab\README.md). If it shows a boot failure instead of Windows Setup: menu Action > Reset, then press a key at once.'
    Write-Host 'When Windows is installed and prepared, run:  lab\vm\base.ps1 -Seal'
    if (-not $NoConnect) { Start-Process vmconnect.exe -ArgumentList 'localhost', $baseVm }
    exit 0
}



if ($Open) {
    # Start the (unsealed) base VM if it is off and open its window. -EditSession opens the connection settings dialog
    # (display size, clipboard, printers, USB devices) even when a saved choice would skip it.
    if (Test-Path $info) { throw 'The base is sealed; it is no longer a VM. Use clone.ps1 -Start <name> -Connect.' }
    $vm = Get-VM -Name $baseVm -ErrorAction SilentlyContinue
    if (-not $vm) { throw "No VM named $baseVm." }
    Assert-NoLabSession
    if ($vm.State -eq 'Off') {
        # Windows is installed: boot from the disk first, so the "Press any key to boot from CD or DVD" wait is skipped
        $hd = Get-VMHardDiskDrive -VMName $baseVm | Select-Object -First 1
        if ($hd) { Set-VMFirmware -VMName $baseVm -FirstBootDevice $hd }
        Start-VM -Name $baseVm
        Write-Host "$baseVm started."
    } else { Write-Host "$baseVm is $($vm.State)." }
    $a = @('localhost', $baseVm)
    if ($EditSession) { $a += '/edit' }
    Start-Process vmconnect.exe -ArgumentList $a
    exit 0
}

if ($Prepare) {
    # Settings every clone should inherit, done through PowerShell Direct while the base VM runs (before -Seal).
    # NOT yet run against a real guest when written (2026-11-12): each step reports what the guest answered.
    if (Test-Path $info) { throw 'The base is already sealed.' }
    $vm = Get-VM -Name $baseVm -ErrorAction SilentlyContinue
    if (-not $vm -or $vm.State -ne 'Running') { throw "$baseVm must be running with Windows installed." }
    $cred = Get-GuestCredential
    $user = $cred.UserName
    $plain = $cred.GetNetworkCredential().Password            # stays inside this process and the guest; never printed
    $noAuto = [bool]$NoAutoLogon
    $result = Invoke-Command -VMName $baseVm -Credential $cred -ArgumentList $user, $plain, $noAuto -ScriptBlock {
        param($user, $plain, $noAutoLogon)
        $out = New-Object System.Collections.Generic.List[string]
        $out.Add("guest: $env:COMPUTERNAME, $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber), user $(whoami)")
        # 1) never sleep, never turn the display off, no hibernation: an unattended run must not stop by itself
        foreach ($a in @('-change standby-timeout-ac 0', '-change monitor-timeout-ac 0', '-change hibernate-timeout-ac 0', '-change disk-timeout-ac 0')) { & powercfg.exe $a.Split(' ') | Out-Null }
        & powercfg.exe -h off 2>$null | Out-Null
        $out.Add("power: sleep/display/hibernate timeouts set to never (powercfg exit $LASTEXITCODE)")
        # 2) work folder for copied tools and collected dumps
        New-Item -ItemType Directory -Force -Path 'C:\lab' | Out-Null
        $out.Add("folder: C:\lab exists = $(Test-Path 'C:\lab')")
        # 3) automatic logon of the lab account on the console, so that programs with windows can be driven without a person.
        #    Documented Winlogon values (Microsoft Learn: "Configure Windows to automate logon"). The password is stored readable
        #    in the guest registry: acceptable only because this account exists in lab VMs alone.
        if (-not $noAutoLogon) {
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
            Set-ItemProperty -Path $k -Name AutoAdminLogon -Value '1' -Type String
            Set-ItemProperty -Path $k -Name DefaultUserName -Value ($user.Split([char]92)[-1]) -Type String
            Set-ItemProperty -Path $k -Name DefaultDomainName -Value $env:COMPUTERNAME -Type String
            Set-ItemProperty -Path $k -Name DefaultPassword -Value $plain -Type String
            $out.Add("autologon: AutoAdminLogon = $((Get-ItemProperty $k).AutoAdminLogon) for $((Get-ItemProperty $k).DefaultUserName)")
        } else { $out.Add('autologon: skipped (-NoAutoLogon)') }
        # 3b) no automatic Windows updates in lab VMs (policy "Configure Automatic Updates" = disabled). The pause set in
        #     Settings ends after a few weeks; a clone that starts updating in the middle of a case spoils the case.
        $au = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
        if (-not (Test-Path $au)) { New-Item -Path $au -Force | Out-Null }
        Set-ItemProperty -Path $au -Name NoAutoUpdate -Value 1 -Type DWord
        $ux = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction SilentlyContinue
        $out.Add("updates: automatic updates policy off (NoAutoUpdate = $((Get-ItemProperty $au).NoAutoUpdate)); pause set in Settings ends $($ux.PauseUpdatesExpiryTime)")
        # 3c) a clone gets a NEW virtual TPM, so the base disk must not depend on the TPM of the base VM: turn the automatic
        #     device encryption off (documented value PreventDeviceEncryption) and decrypt C: (runs in the background).
        $bk = 'HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker'
        if (-not (Test-Path $bk)) { New-Item -Path $bk | Out-Null }      # never -Force on an existing key: it would drop its values
        Set-ItemProperty -Path $bk -Name PreventDeviceEncryption -Value 1 -Type DWord
        try { $v = Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop; if ($v.VolumeStatus -ne 'FullyDecrypted' -and $v.VolumeStatus -ne 'DecryptionInProgress') { Disable-BitLocker -MountPoint 'C:' -ErrorAction Stop | Out-Null; $out.Add('encryption: decryption of C: started') } } catch { $out.Add("encryption: could not start decryption ($($_.Exception.Message))") }
        # 4) facts worth knowing before sealing (read only)
        $bl = try { (Get-BitLockerVolume -MountPoint 'C:' -ErrorAction Stop | ForEach-Object { "$($_.VolumeStatus)/$($_.ProtectionStatus) $($_.EncryptionPercentage)% encrypted" }) } catch { "unknown ($($_.Exception.Message))" }
        $out.Add("disk encryption on C: $bl   (a clone gets a NEW virtual TPM: an encrypted, protected base disk would not unlock there)")
        $out.Add("pending restart: $(Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')")
        $out.Add("display: $((Get-CimInstance Win32_VideoController | ForEach-Object { '{0} {1}x{2}' -f $_.Name, $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution }) -join '; ')")
        return $out.ToArray()
    }
    $plain = $null
    $result | ForEach-Object { Write-Host "  $_" }
    Write-Host 'If "disk encryption" above is not FullyDecrypted, wait for it (run -Prepare again to see) before -Seal.'
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
