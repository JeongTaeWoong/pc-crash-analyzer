# 수집기 오버헤드 측정 — 관리자 터미널에서 실행하면 ETW(커널 프로세스·모듈 추적) 포함 조건, 일반 터미널이면 비관리자 조건.
# 사용: powershell -ExecutionPolicy Bypass -File tools\measure-overhead.ps1 [-Minutes 30] [-Exe <BlackBox.exe 경로>] [-Out <결과 폴더>]
# 결과: <Out>\overhead-<조건>-<시각>.csv (10초 간격 CPU%·Private MB·핸들) + 화면 요약. 수집기 덤프는 <Out>\run 아래(커밋 금지 폴더).
# 측정 중에는 빌드·게임 등 무거운 작업을 하지 않는 "조용한 조건"을 권장한다(10/29 비관리자 30분 기준값: CPU 평균 0.26%).
param(
    [int]$Minutes = 30,
    [string]$Exe = "",
    [string]$Out = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Exe) { $Exe = Join-Path $root "collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe" }
if (-not (Test-Path $Exe)) { throw "BlackBox.exe 없음: $Exe (먼저 dotnet build collector/BlackBox -c Release)" }
if (-not $Out) { $Out = Join-Path $root "logs\overhead" }   # logs/ 는 .gitignore 대상
$runDir = Join-Path $Out "run"
New-Item -ItemType Directory -Force $runDir | Out-Null

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$cond = if ($isAdmin) { "admin-etw" } else { "nonadmin" }
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$csv = Join-Path $Out "overhead-$cond-$stamp.csv"
$cpus = [Environment]::ProcessorCount
Write-Host "[measure] 조건=$cond, $Minutes 분, 논리 CPU $cpus, exe=$Exe"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = $runDir
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$proc = [System.Diagnostics.Process]::Start($psi)
$logPath = Join-Path $runDir "blackbox-$stamp.log"
$logWriter = [System.IO.StreamWriter]::new($logPath, $false, [System.Text.Encoding]::UTF8)
$null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action { if ($EventArgs.Data) { $Event.MessageData.WriteLine($EventArgs.Data) } } -MessageData $logWriter
$proc.BeginOutputReadLine()
Start-Sleep -Seconds 5

# 같은 이름의 다른 BlackBox 가 있으면 인스턴스 이름이 BlackBox#1 이 될 수 있어 PID 로 인스턴스를 찾는다
function Get-InstanceName([int]$procId) {
    $cat = New-Object System.Diagnostics.PerformanceCounterCategory("Process")
    foreach ($n in $cat.GetInstanceNames()) {
        if ($n -notlike "BlackBox*") { continue }
        try { $c = New-Object System.Diagnostics.PerformanceCounter("Process", "ID Process", $n, $true); if ([int]$c.RawValue -eq $procId) { return $n } } catch {}
    }
    return "BlackBox"
}
$inst = Get-InstanceName $proc.Id
$cCpu = New-Object System.Diagnostics.PerformanceCounter("Process", "% Processor Time", $inst, $true)
$cPriv = New-Object System.Diagnostics.PerformanceCounter("Process", "Private Bytes", $inst, $true)
$cHnd = New-Object System.Diagnostics.PerformanceCounter("Process", "Handle Count", $inst, $true)
$null = $cCpu.NextValue()

$rows = New-Object System.Collections.Generic.List[object]
$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end -and -not $proc.HasExited) {
    Start-Sleep -Seconds 10
    $rows.Add([pscustomobject]@{
        t       = (Get-Date).ToString("HH:mm:ss")
        cpuPct  = [math]::Round($cCpu.NextValue() / $cpus, 3)
        privMB  = [math]::Round($cPriv.NextValue() / 1MB, 1)
        handles = [int]$cHnd.NextValue()
    })
}
$proc.StandardInput.WriteLine("s"); Start-Sleep -Seconds 2
$proc.StandardInput.WriteLine("q"); $proc.WaitForExit(90000) | Out-Null
Start-Sleep -Seconds 1
$logWriter.Flush(); $logWriter.Close()

$rows | Export-Csv -NoTypeInformation -Encoding UTF8 $csv
$steady = $rows | Select-Object -Skip 12
$cpu = $rows | Measure-Object cpuPct -Average -Maximum
$cpuS = $steady | Measure-Object cpuPct -Average
$priv = $rows | Measure-Object privMB -Average -Maximum
$sources = Select-String -Path $logPath -Pattern '^\[source\]' | ForEach-Object { $_.Line }
Write-Host ""
Write-Host ("[result] 조건={0} 샘플={1} CPU평균={2:N3}% (기동 2분 제외 {3:N3}%) 최대={4:N2}% Private평균={5:N1}MB 최대={6:N1}MB" -f `
    $cond, $rows.Count, $cpu.Average, $cpuS.Average, $cpu.Maximum, $priv.Average, $priv.Maximum)
Write-Host "[result] CSV: $csv"
Write-Host "[result] 소스 상태:"; $sources | ForEach-Object { Write-Host "  $_" }
