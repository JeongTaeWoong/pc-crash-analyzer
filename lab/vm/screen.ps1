# Look at a lab VM or press a key in it, from the host, without opening its window. ASCII only.
#
#   powershell -ExecutionPolicy Bypass -File lab\vm\screen.ps1 -Vm pca-base -Out F:\pca-vm\shots\base.png     # what the VM shows now
#   powershell -ExecutionPolicy Bypass -File lab\vm\screen.ps1 -Vm pca-finsec-a -Key 13                       # press Enter (virtual-key code)
#
# Works for any VM whose name starts with the lab prefix (vm.json). Keys go to the virtual machine only.
param(
    [Parameter(Mandatory = $true)][string]$Vm,
    [string]$Out,
    [int]$Key = 0,
    [int]$Width = 1024,
    [int]$Height = 768
)
. (Join-Path $PSScriptRoot 'VmLib.ps1')
$cfg = Get-VmConfig
Assert-HyperV
if ($Vm -notlike "$($cfg.prefix)*") { throw "Only lab VMs (names starting with $($cfg.prefix))." }
$v = Get-VM -Name $Vm -ErrorAction SilentlyContinue
if (-not $v) { throw "No VM named $Vm." }
if ($v.State -ne 'Running') { throw "$Vm is $($v.State)." }
if ($Key) { $rv = Send-VmKey $Vm $Key; Write-Host "key $Key sent to $Vm (return $rv)" }
if ($Out) { Write-Host ("screenshot: " + (Save-VmScreenshot $Vm $Out $Width $Height)) }
if (-not $Key -and -not $Out) { throw 'Give -Out <file.png> and/or -Key <virtual-key code>.' }
