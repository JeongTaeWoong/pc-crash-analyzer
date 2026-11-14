# Self-test of lab/vm base.ps1, clone.ps1 and guest.ps1 without a guest OS: makes throwaway VMs under <vm root>/_test and removes them. ASCII only.
#   powershell -ExecutionPolicy Bypass -File lab/vm/test-vm-scripts.ps1      (about 2 minutes; refuses when a VM named pca-base or pca-a to pca-d exists)
$ErrorActionPreference = 'Continue'
$vmDir = $PSScriptRoot
$root = Join-Path ((Get-Content (Join-Path $PSScriptRoot 'vm.json') -Raw -Encoding UTF8 | ConvertFrom-Json).root) '_test'
if (Get-VM -ErrorAction SilentlyContinue | Where-Object { $_.Name -in 'pca-base', 'pca-a', 'pca-b', 'pca-c', 'pca-d' }) { Write-Host 'Refusing: a VM named pca-base or pca-a to pca-d exists (this test would delete it).'; exit 9 }
$iso = Join-Path $env:TEMP 'pca-dummy.iso'
$pass = 0; $fail = 0
function Run($file, [string[]]$a) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $vmDir $file) @a -Root $root 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out.Trim() }
}
function Check($label, $cond, $detail) {
    if ($cond) { $script:pass++; Write-Host "[PASS] $label" } else { $script:fail++; Write-Host "[FAIL] $label :: $detail" }
}
function New-TestIso([string]$path) {
    # a small but valid disc image (UDF) built with the Windows imaging COM API; not bootable, which is enough to start a VM
    Add-Type -TypeDefinition @'
public class PcaIsoWriter {
    public static void Write(string path, object stream, int blockSize, int totalBlocks) {
        var s = (System.Runtime.InteropServices.ComTypes.IStream)stream;
        var buf = new byte[blockSize];
        using (var o = System.IO.File.Create(path)) {
            for (int i = 0; i < totalBlocks; i++) { s.Read(buf, blockSize, System.IntPtr.Zero); o.Write(buf, 0, blockSize); }
        }
    }
}
'@
    $src = Join-Path $env:TEMP 'pca-iso-src'
    New-Item -ItemType Directory -Force -Path $src | Out-Null
    'lab vm test' | Set-Content (Join-Path $src 'readme.txt')
    $fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
    $fsi.FileSystemsToCreate = 4
    $fsi.VolumeName = 'PCATEST'
    $fsi.Root.AddTree($src, $false)
    $res = $fsi.CreateResultImage()
    [PcaIsoWriter]::Write($path, $res.ImageStream, $res.BlockSize, $res.TotalBlocks)
    Remove-Item $src -Recurse -Force
}
try {
    if (Test-Path $root) { throw "test root already exists: $root" }
    $bad = Join-Path $env:TEMP 'pca-bad.iso'; [IO.File]::WriteAllBytes($bad, (New-Object byte[] (2MB)))
    New-TestIso $iso
    $r = Run 'base.ps1' @('-Create', '-Iso', $bad, '-NoConnect'); Check 'unreadable ISO: create fails and leaves nothing' ($r.Code -eq 3 -and -not (Get-VM pca-base -ErrorAction SilentlyContinue) -and -not (Test-Path (Join-Path $root 'base'))) $r.Out
    Remove-Item $bad -Force

    $r = Run 'base.ps1' @('-Status');                         Check 'status with nothing' ($r.Code -eq 0 -and $r.Out -match 'base disk\s+none') $r.Out
    $r = Run 'clone.ps1' @('-New', 'a');                      Check 'clone refused before base is sealed' ($r.Code -ne 0 -and $r.Out -match 'not sealed') $r.Out
    $r = Run 'base.ps1' @('-Create', '-Iso', $iso, '-NoConnect'); Check 'base created and started' ($r.Code -eq 0 -and (Get-VM pca-base -ErrorAction SilentlyContinue).State -eq 'Running') $r.Out
    $r = Run 'base.ps1' @('-Create', '-Iso', $iso, '-NoConnect'); Check 'second create refused' ($r.Code -ne 0 -and $r.Out -match 'already exists') $r.Out
    $v = Get-VM pca-base -ErrorAction SilentlyContinue
    Check 'base hardware' ($v -and $v.Generation -eq 2 -and $v.ProcessorCount -eq 4 -and $v.MemoryStartup -eq 8GB -and (Get-VMSecurity -VMName pca-base).TpmEnabled -and (Get-VMFirmware -VMName pca-base).SecureBoot -eq 'On' -and -not $v.AutomaticCheckpointsEnabled) "gen $($v.Generation) cpu $($v.ProcessorCount)"
    Check 'base boots from DVD first' ((Get-VMFirmware -VMName pca-base).BootOrder[0].BootType -eq 'Drive' -and (Get-VMFirmware -VMName pca-base).BootOrder[0].Device -is [Microsoft.HyperV.PowerShell.DvdDrive]) ((Get-VMFirmware -VMName pca-base).BootOrder | Out-String)
    Stop-VM -Name pca-base -TurnOff -Force
    $r = Run 'base.ps1' @('-Seal');                           Check 'sealed' ($r.Code -eq 0 -and (Test-Path "$root\base\base-info.json") -and -not (Get-VM pca-base -ErrorAction SilentlyContinue) -and (Get-Item "$root\base\base.vhdx").IsReadOnly) $r.Out
    $r = Run 'base.ps1' @('-Discard');                        Check 'discard refused when sealed' ($r.Code -ne 0) $r.Out

    $r = Run 'clone.ps1' @('-New', 'a');                      Check 'clone a created with checkpoint clean' ($r.Code -eq 0 -and (Get-VMSnapshot -VMName pca-a -Name clean -ErrorAction SilentlyContinue)) $r.Out
    $r = Run 'clone.ps1' @('-New', 'b', '-Boot');             Check 'clone b created and started' ($r.Code -eq 0 -and (Get-VM pca-b).State -eq 'Running') $r.Out
    $r = Run 'clone.ps1' @('-New', 'c', '-Spec', 'office-old', '-Boot'); $vc = Get-VM pca-c -ErrorAction SilentlyContinue
    Check 'profile office-old: 2 CPUs capped at 50%, 4 GB, disk 3000 IOPS, starts' ($r.Code -eq 0 -and $vc.ProcessorCount -eq 2 -and $vc.MemoryStartup -eq 4GB -and (Get-VMProcessor -VMName pca-c).Maximum -eq 50 -and (Get-VMHardDiskDrive -VMName pca-c).MaximumIOPS -eq 3000 -and $vc.State -eq 'Running') $r.Out
    $r = Run 'clone.ps1' @('-New', 'd', '-Spec', 'nope');   Check 'unknown profile refused, nothing made' ($r.Code -ne 0 -and -not (Get-VM pca-d -ErrorAction SilentlyContinue)) $r.Out
    $r = Run 'clone.ps1' @('-Remove', 'c');                 Check 'remove c' ($r.Code -eq 0 -and -not (Get-VM pca-c -ErrorAction SilentlyContinue)) $r.Out
    $r = Run 'clone.ps1' @('-New', 'Bad Name');               Check 'bad name refused' ($r.Code -ne 0 -and $r.Out -match 'Bad name') $r.Out
    $r = Run 'clone.ps1' @('-Start', 'a');                    Check 'two clones run at once' ($r.Code -eq 0 -and @(Get-VM pca-a, pca-b | Where-Object State -eq 'Running').Count -eq 2) $r.Out
    Check 'clone disk is differencing on the base' ((Get-VHD "$root\clones\a\disk.vhdx" -ErrorAction SilentlyContinue).ParentPath -eq "$root\base\base.vhdx" -or (Get-VMHardDiskDrive -VMName pca-a | Get-VHD).VhdType -eq 'Differencing') ''
    $r = Run 'clone.ps1' @('-Save', 'a', '-Label', 'installed'); Check 'checkpoint while running' ($r.Code -eq 0 -and @(Get-VMSnapshot -VMName pca-a).Count -eq 2) $r.Out
    $r = Run 'clone.ps1' @('-Save', 'a', '-Label', 'installed'); Check 'duplicate label refused' ($r.Code -ne 0) $r.Out
    $r = Run 'clone.ps1' @('-Restore', 'a', '-Label', 'clean'); $st = (Get-VM pca-a).State; Check 'restore to clean turns it off' ($r.Code -eq 0 -and "$st" -eq 'Off') "code $($r.Code) state $st :: $($r.Out)"
    $r = Run 'clone.ps1' @('-Restore', 'a', '-Label', 'installed', '-Boot'); Check 'restore to a running checkpoint' ($r.Code -eq 0 -and (Get-VM pca-a).State -eq 'Running') $r.Out
    $r = Run 'clone.ps1' @('-List');                          Check 'list shows both' ($r.Code -eq 0 -and $r.Out -match '(?m)^a\s' -and $r.Out -match '(?m)^b\s' -and $r.Out -match 'clean, installed') $r.Out
    $r = Run 'guest.ps1' @('-Name', 'a', '-Wait', '-TimeoutSec', '5'); Check 'guest.ps1 without credential explains what to do' ($r.Code -ne 0 -and ($r.Out -match 'SetCredential' -or $r.Out -match 'did not answer')) $r.Out
    $r = Run 'clone.ps1' @('-Stop', 'b', '-TurnOff');         Check 'turn off b' ($r.Code -eq 0 -and (Get-VM pca-b).State -eq 'Off') $r.Out
    $r = Run 'clone.ps1' @('-Remove', 'a');                   Check 'remove running clone a' ($r.Code -eq 0 -and -not (Get-VM pca-a -ErrorAction SilentlyContinue) -and -not (Test-Path "$root\clones\a")) $r.Out
    $r = Run 'clone.ps1' @('-Remove', 'b');                   Check 'remove b' ($r.Code -eq 0 -and -not (Get-VM pca-b -ErrorAction SilentlyContinue) -and -not (Test-Path "$root\clones\b")) $r.Out
    $r = Run 'clone.ps1' @('-List');                          Check 'list empty' ($r.Code -eq 0 -and $r.Out -match 'No case VMs') $r.Out
}
catch { $fail++; Write-Host "[ERROR] $($_.Exception.Message)" }
finally {
    foreach ($n in 'pca-a', 'pca-b', 'pca-c', 'pca-d', 'pca-base') { try { $v = Get-VM $n -ErrorAction SilentlyContinue; if ($v) { if ($v.State -ne 'Off') { Stop-VM -Name $n -TurnOff -Force }; Remove-VM -Name $n -Force } } catch { Write-Host "cleanup $n : $($_.Exception.Message)" } }
    Start-Sleep 3
    if (Test-Path $root) { Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.IsReadOnly = $false }; Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item $iso -Force -ErrorAction SilentlyContinue
    $rootLeft = Test-Path $root
    Write-Host ("left over: lab VMs {0}, test root {1}" -f @(Get-VM | Where-Object Name -like 'pca-*').Count, $rootLeft)
}
Write-Host "RESULT: $pass passed, $fail failed"
exit $fail
