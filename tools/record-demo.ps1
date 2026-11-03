# 자동 데모 촬영: 실행하면 알아서 "수집기 로그 → 15초 지점 크래시 → 자동 진단"을 mp4로 녹화한다.
#   powershell -ExecutionPolicy Bypass -File tools\record-demo.ps1
# 주의: 녹화 중 왼쪽 위 1600x900 영역에 앱 창(최상위)이 약 80초간 떠 있는다.
#       화면 배율 100% 기준(창 좌표=픽셀). 배율이 다르면 -Scale 값(예: 1.25)을 지정.
param(
    [string]$Out = "",
    [int]$HoldSeconds = 18,   # 진단 화면 유지 시간
    [double]$Scale = 1.0      # Windows 디스플레이 배율 (125% = 1.25)
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
if ($Out -eq "") {
    $recDir = Join-Path $root "recordings"
    New-Item -ItemType Directory -Force $recDir | Out-Null
    $Out = Join-Path $recDir ("demo_{0:yyyyMMdd_HHmmss}.mp4" -f (Get-Date))
}

# ─ 0) 준비: 빌드(없을 때만) · 기존 덤프 보관 이동 · 이전 인스턴스 정리 ─
$ui    = Join-Path $root "ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe"
$bb    = Join-Path $root "collector\BlackBox\bin\Release\net8.0-windows\BlackBox.exe"
$dummy = Join-Path $root "collector\experiments\CrashDummy\bin\Release\net8.0\CrashDummy.exe"
if (-not (Test-Path $ui))    { dotnet build (Join-Path $root "ui\wpf\CrashAnalyzer.UI")          -c Release -v q | Out-Null }
if (-not (Test-Path $bb))    { dotnet build (Join-Path $root "collector\BlackBox")               -c Release -v q | Out-Null }
if (-not (Test-Path $dummy)) { dotnet build (Join-Path $root "collector\experiments\CrashDummy") -c Release -v q | Out-Null }

Get-Process CrashAnalyzerUI,BlackBox -ErrorAction SilentlyContinue | ForEach-Object {
    $_.CloseMainWindow() | Out-Null; if (-not $_.WaitForExit(4000)) { $_.Kill() }
}

$dumps = Join-Path $root "collector\BlackBox\dumps"
if ((Test-Path $dumps) -and (Get-ChildItem $dumps -ErrorAction SilentlyContinue)) {
    $arch = Join-Path $root ("collector\BlackBox\dumps_archive\{0:yyyyMMdd_HHmmss}" -f (Get-Date))
    New-Item -ItemType Directory -Force $arch | Out-Null
    Move-Item "$dumps\*" $arch
    Write-Host "기존 덤프를 보관 이동: $arch"
}

$marker = Join-Path $env:TEMP "crashui_demo_marker.txt"
Remove-Item $marker -ErrorAction SilentlyContinue

# ─ 1) ffmpeg: 앱 창 영역(60,60~1600x900)만 녹화 — 화면 전체를 캡처하지 않는다 ─
$px = [int](60 * $Scale); $py = [int](60 * $Scale)
$pw = [int](1600 * $Scale); $ph = [int](900 * $Scale)
if ($pw % 2) { $pw++ }; if ($ph % 2) { $ph++ }
$ffArgs = "-y -f gdigrab -framerate 30 -offset_x $px -offset_y $py -video_size ${pw}x${ph} -draw_mouse 0 -i desktop " +
          "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -loglevel warning `"$Out`""
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "ffmpeg"; $psi.Arguments = $ffArgs
$psi.RedirectStandardInput = $true; $psi.UseShellExecute = $false
$ff = [System.Diagnostics.Process]::Start($psi)
Start-Sleep -Milliseconds 1200

# ─ 2) 앱(데모 모드) 기동: 수집 시작 → 로그 탭 자동 ─
$app = Start-Process $ui -ArgumentList "--record-demo","`"$marker`"" -PassThru
Write-Host "녹화 시작 ($(Get-Date -Format HH:mm:ss)) → $Out"

# ─ 3) 영상 ~15초 지점에 크래시가 떨어지도록 CrashDummy c(15초 후 크래시)를 즉시 발사 ─
Start-Sleep -Seconds 3
Start-Process $dummy -ArgumentList "c" -WindowStyle Minimized | Out-Null
Write-Host "CrashDummy 발사 — 15초 뒤 크래시 예정"

# ─ 4) 자동 진단 완료 대기 (marker) → 진단 화면 유지 ─
$deadline = (Get-Date).AddSeconds(120)
while (-not (Test-Path $marker) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (Test-Path $marker) {
    Write-Host "자동 진단 완료 ($(Get-Content $marker)) — ${HoldSeconds}초 유지 후 종료"
    Start-Sleep -Seconds $HoldSeconds
} else {
    Write-Warning "120초 내 진단 마커가 없음 — 이 테이크는 실패일 수 있음"
}

# ─ 5) 정리: 앱 정상 종료(수집기 드레인 포함) → ffmpeg 'q'로 mp4 마무리 ─
if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(10000)) { $app.Kill() } }
$ff.StandardInput.Write("q"); $ff.StandardInput.Flush()
if (-not $ff.WaitForExit(15000)) { $ff.Kill() }

$info = Get-Item $Out
Write-Host ""
Write-Host "완료: $($info.FullName)  ($([Math]::Round($info.Length/1MB,1)) MB)"
Write-Host "테이크가 마음에 안 들면 그냥 다시 실행하면 된다 (덤프는 매회 자동 보관 이동)."
