# 모든 스파이크를 순서대로 실행하는 읽기 전용 실행기. 시스템을 변경하지 않는다.
# 사용법: powershell -File run-all.ps1
$ErrorActionPreference = "Continue"
Get-ChildItem $PSScriptRoot -Directory | Sort-Object Name | ForEach-Object {
    Write-Host "=== $($_.Name) ===" -ForegroundColor Cyan
    dotnet run -c Release --project $_.FullName
}
