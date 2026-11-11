# Opens the given pages one by one in the lab's own Chrome profile, every -IntervalSec, until -StopFile appears.
# Only navigates; never touches page content.
param([string]$Chrome, [string]$ProfileDir, [string]$Urls, [int]$IntervalSec = 20, [string]$StopFile)
$list = @($Urls -split '\|' | Where-Object { $_ })
$i = 0
while (-not (Test-Path -LiteralPath $StopFile)) {
    for ($s = 0; $s -lt $IntervalSec * 2; $s++) { if (Test-Path -LiteralPath $StopFile) { exit 0 }; Start-Sleep -Milliseconds 500 }
    if (-not $list.Count) { continue }
    Start-Process -FilePath $Chrome -ArgumentList ('--user-data-dir="{0}" "{1}"' -f $ProfileDir, $list[$i % $list.Count])
    $i++
}
