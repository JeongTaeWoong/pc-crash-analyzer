# Starts the console collector (dumps stay local), runs one reproduction N times, waits for each dump, quits the collector cleanly.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ArgLine = "",
    [int]$Times = 3,
    [int]$AliveSec = 45,
    [int]$DumpWaitSec = 75,
    [string]$WorkDir = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\experiments\repro-runs',
    [string]$Collector = 'F:\2026CAU\Capstone2\pc-crash-analyzer\collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe',
    [string]$Log = "$env:TEMP\repro-collector.log"
)
$ErrorActionPreference = 'Stop'
if (Get-Process PcBlackBox -ErrorAction SilentlyContinue) { throw 'tray PcBlackBox is running; it would upload these crashes' }
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$dumps = Join-Path $WorkDir 'dumps'
New-Item -ItemType Directory -Force $dumps | Out-Null
$env:DOTNET_ROLL_FORWARD = 'Major'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "$env:SystemRoot\System32\cmd.exe"
$psi.Arguments = "/c `"`"$Collector`" > `"$Log`" 2>&1`""
$psi.WorkingDirectory = $WorkDir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.CreateNoWindow = $true
$c = [System.Diagnostics.Process]::Start($psi)
try {
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
        Start-Sleep -Seconds 1
        if (Test-Path $Log) { $ready = [bool](Select-String -Path $Log -Pattern '\[ready\]' -Quiet -ErrorAction SilentlyContinue) }
    }
    if (-not $ready) { throw 'collector did not become ready' }
    Write-Host "[run] collector ready; warming up 20s"
    Start-Sleep -Seconds 20

    for ($n = 1; $n -le $Times; $n++) {
        $before = @(Get-ChildItem $dumps -Directory | ForEach-Object Name)
        $t = Start-Process $Exe -ArgumentList $ArgLine -PassThru -WindowStyle Hidden -RedirectStandardOutput "$env:TEMP\repro-target.out" -RedirectStandardError "$env:TEMP\repro-target.err"
        [void]$t.Handle
        if (-not $t.WaitForExit($AliveSec * 1000)) {
            Stop-Process -Id $t.Id -Force -ErrorAction SilentlyContinue
            Write-Host ("[run {0}] target still alive after {1}s: killed (no crash)" -f $n, $AliveSec)
        } else {
            Write-Host ("[run {0}] target exit 0x{1:X8} after {2:N1}s" -f $n, $t.ExitCode, ($t.ExitTime - $t.StartTime).TotalSeconds)
        }
        if ($t.HasExited -and $t.ExitCode -eq 0) { Write-Host ("[run {0}] clean exit: no crash, skipping dump wait" -f $n); continue }
        $new = $null
        for ($w = 0; $w -lt $DumpWaitSec -and -not $new; $w++) {
            Start-Sleep -Seconds 1
            $new = Get-ChildItem $dumps -Directory | Where-Object { $before -notcontains $_.Name -and (Test-Path (Join-Path $_.FullName 'engine-snapshot.json')) } | Select-Object -First 1
        }
        if ($new) { Write-Host ("[run {0}] dump {1}" -f $n, $new.Name) } else { Write-Host ("[run {0}] no dump within {1}s" -f $n, $DumpWaitSec) }
    }
}
finally {
    # .NET may prepend a BOM to the first write on redirected stdin; give it an empty line to land on
    try { $c.StandardInput.WriteLine(''); $c.StandardInput.WriteLine('q'); $c.StandardInput.Flush() } catch { }
    if (-not $c.WaitForExit(90000)) { Write-Host '[run] collector did not stop in time'; Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue }
    Get-Process BlackBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Collector } | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "[run] collector stopped"
}
