# 수집기 배포 빌드 — 지인 PC에 .NET 설치 없이 풀어서 바로 실행되는 단일 폴더(self-contained, win-x64)를 만든다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\publish-collector.ps1 [-Version 0.6.0]
# 결과: dist\BlackBox-<Version>-win-x64\ 와 dist\BlackBox-<Version>-win-x64.zip (dist\ 는 커밋하지 않는다)
# 원칙: 수집기는 읽기 전용. 이 스크립트는 빌드만 하며 시작 프로그램 등록·설정 변경을 하지 않는다 (CLAUDE.md 3).
param(
    [string]$Version = "0.6.0"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root "collector\BlackBox\BlackBox.csproj"
$name = "BlackBox-$Version-win-x64"
$out  = Join-Path $root "dist\$name"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "[publish] $proj -> $out"
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out `
    -p:PublishSingleFile=false -p:DebugType=none -p:Version=$Version --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 지인용 안내문·설정 예시를 함께 넣는다
Copy-Item (Join-Path $root "docs\participant-guide.md") (Join-Path $out "읽어주세요.md")
@'
{ "testArtifacts": ["CrashDummy.exe", "FaultLab.exe", "FaultHost.exe"], "denylist": [] }
'@ | Set-Content -Encoding UTF8 (Join-Path $out "blackbox.config.json")
@'
@echo off
cd /d "%~dp0"
echo BlackBox 수집기를 시작합니다. 이 창을 닫으면 수집이 멈춥니다.
echo 문제가 생기면 Ctrl+Shift+F12 (또는 이 창에서 m 입력 후 Enter) 를 눌러 그 순간을 저장하세요.
BlackBox.exe
'@ | Set-Content -Encoding Default (Join-Path $out "수집기 시작.bat")

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$files = (Get-ChildItem $out -Recurse -File | Measure-Object).Count
Write-Host "[done] $zip  ($size MB, $files files)"
