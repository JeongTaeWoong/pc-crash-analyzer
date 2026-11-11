# 참여자판 배포 빌드 — 지인 PC 에 .NET 설치 없이 풀어서 바로 쓰는 폴더(self-contained, win-x64)와 zip 을 만든다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\publish-participant.ps1 [-Version 0.9.0] [-Notes "바뀐 점"] [-Config participant\study.local.json] [-AllowLoopback]
# 결과: dist\PcBlackBox-<Version>-win-x64.zip (+ .sha256.txt). dist\ 는 커밋하지 않는다.
# 안전장치:
#  - 설정 검증(StudyTools check): https 수신 주소, 3072비트 이상 공개키, keyId 일치, 개인키가 섞여 있으면 거부
#  - 127.0.0.1 시험 수신기 설정은 -AllowLoopback 일 때만(시험 빌드) — 실수로 시험 설정을 지인에게 보내지 않게
#  - 배포 폴더에 *.pem·*.pdb·study.local.json 이 들어가면 실패
#  - 시험 빌드는 zip 안 폴더 이름도 "PC블랙박스-팀시험용" — 지인에게 잘못 보내도 이름으로 알아보게(10/30)
#
# 자동 업데이트(0.9~): 업데이트 서명 키(%USERPROFILE%\.pca-study\update-signing.private.pem)가 있으면 업데이트 묶음도 만든다.
#   dist\update\<이름>\  PcBlackBox-<Version>-win-x64-update.zip · update.json · update.json.sig  → GitHub Releases 에 올리는 세 파일
#   묶음에는 연구 설정(study.json)이 없다 — 수신 주소·토큰·문의처가 공개되지 않는다. 설치된 PC 는 자기 study.json 을 이어 쓴다.
#   참여자 zip(study.json 포함)은 지금처럼 새 참여자에게 한 번만 직접 건넨다. 이것은 Releases 에 올리지 않는다.
#   시험 빌드는 -UpdateBaseUrl http://127.0.0.1:8788/ 를 줄 때만 묶음을 만든다(tools\test-update.ps1 이 쓴다).
param(
    [string]$Version = "0.9.0",
    [string]$Config = "participant\study.local.json",
    [switch]$AllowLoopback,
    [string]$Notes = "",
    [string]$UpdateBaseUrl = "",
    [string]$UpdateKey = (Join-Path $env:USERPROFILE ".pca-study\update-signing.private.pem"),
    [switch]$NoUpdatePackage
)
$ErrorActionPreference = "Stop"
if (-not $env:DOTNET_ROLL_FORWARD) { $env:DOTNET_ROLL_FORWARD = "Major" }   # 팀 PC 에 .NET 8 런타임이 없어도 StudyTools 가 돌게(11/10)
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$configPath = if ([System.IO.Path]::IsPathRooted($Config)) { $Config } else { Join-Path $root $Config }
if (-not (Test-Path $configPath)) { throw "설정 없음: $configPath — 먼저 StudyTools keygen 과 Apps Script 배포(docs\study-operations.md)" }

$raw = Get-Content $configPath -Raw -Encoding UTF8
if ($raw -match "PRIVATE KEY") { throw "설정 파일에 개인키가 들어 있다 — 배포 중단" }
$cfg = $raw | ConvertFrom-Json
$uri = [Uri]$cfg.endpoint
$loopback = $uri.IsLoopback -or $uri.Host -eq "localhost"
if ($loopback -and -not $AllowLoopback) { throw "수신 주소가 127.0.0.1 시험 수신기다. 지인 배포본이면 Apps Script /exec 주소로 바꿀 것 (시험 빌드는 -AllowLoopback)" }

# 1) 설정 검증 (팀 도구로 — 참여자 앱과 같은 검증 코드)
$tools = Join-Path $root "participant\StudyTools\StudyTools.csproj"
dotnet build $tools -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "StudyTools 빌드 실패" }
$toolsExe = Join-Path $root "participant\StudyTools\bin\Release\net8.0\StudyTools.exe"
& $toolsExe check --config $configPath
if ($LASTEXITCODE -ne 0) { throw "설정 검증 실패" }

# 2) 게시
$suffix = if ($loopback) { "-LOCALTEST" } else { "" }
$name = "PcBlackBox-$Version-win-x64$suffix"
$stage = Join-Path $root "dist\$name"
$appDir = Join-Path $stage $(if ($loopback) { "PC블랙박스-팀시험용" } else { "PC블랙박스" })
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $appDir | Out-Null
$proj = Join-Path $root "participant\BlackBox.Participant\BlackBox.Participant.csproj"
Write-Host "[publish] $proj -> $appDir"
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $appDir `
    -p:PublishSingleFile=false -p:DebugType=none -p:Version=$Version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 ($LASTEXITCODE)" }

# 3) 연구 설정(공개키·수신 주소·토큰·문의처) — 알려진 필드만 옮긴다
$study = [ordered]@{
    studyId        = $cfg.studyId
    endpoint       = $cfg.endpoint
    token          = $cfg.token
    publicKeyPem   = $cfg.publicKeyPem
    keyId          = $cfg.keyId
    contact        = $cfg.contact
    retentionUntil = $cfg.retentionUntil
    collectUntil   = $(if ($cfg.collectUntil) { $cfg.collectUntil } else { "" })   # 0.8.0 까지 빠져 있었음 — 수집 종료일이 배포본에 안 실림
}
$json = $study | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText((Join-Path $appDir "study.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

# 4) 안내문 (메모장으로 바로 열리게 .txt, UTF-8 BOM)
$guide = Get-Content (Join-Path $root "docs\participant-guide.md") -Raw -Encoding UTF8
[System.IO.File]::WriteAllText((Join-Path $appDir "읽어주세요.txt"), $guide, (New-Object System.Text.UTF8Encoding($true)))

# 5) 들어가면 안 되는 것 점검
$bad = Get-ChildItem $appDir -Recurse -File | Where-Object { $_.Name -like "*.pem" -or $_.Name -like "*.pdb" -or $_.Name -eq "study.local.json" }
if ($bad) { throw "배포 폴더에 들어가면 안 되는 파일: $($bad.FullName -join ', ')" }
if ((Get-Content (Join-Path $appDir "study.json") -Raw) -match "PRIVATE") { throw "study.json 에 개인키" }

# 6) zip + SHA-256
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $appDir -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -Encoding ASCII (Join-Path $root "dist\$name.zip.sha256.txt")
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$files = (Get-ChildItem $appDir -Recurse -File | Measure-Object).Count
Write-Host "[done] $zip  ($size MB, $files files)"
Write-Host "       SHA-256 $hash"
Write-Host "       받는 곳 $($uri.Host) · 키 $($cfg.keyId)$(if ($loopback) { ' · 시험 빌드(지인 배포 금지)' })"

# 7) 업데이트 묶음 (연구 설정 없음) + 서명한 안내문
if ($NoUpdatePackage) { return }
if ($loopback -and -not $UpdateBaseUrl) { Write-Host "[update] 시험 빌드 — 업데이트 묶음은 -UpdateBaseUrl 을 줄 때만 만든다"; return }
if (-not (Test-Path $UpdateKey)) {
    Write-Host "[update] 서명 키 없음($UpdateKey) — 업데이트 묶음을 만들지 않았다. 처음이면 StudyTools update-keygen"
    return
}
$updDir = Join-Path $root "dist\update\$name"
if (Test-Path $updDir) { Remove-Item -Recurse -Force $updDir }
$packArgs = @("update-pack", "--app", $appDir, "--version", $Version, "--out", $updDir, "--key", $UpdateKey)
if ($Notes) { $packArgs += @("--notes", $Notes) }
if ($UpdateBaseUrl) { $packArgs += @("--base-url", $UpdateBaseUrl) }
& $toolsExe @packArgs
if ($LASTEXITCODE -ne 0) { throw "업데이트 묶음 만들기 실패" }
$verifyArgs = @("update-verify", "--dir", $updDir)
if ($loopback) { $verifyArgs += "--allow-loopback" }
& $toolsExe @verifyArgs
if ($LASTEXITCODE -ne 0) { throw "업데이트 묶음 확인 실패" }
