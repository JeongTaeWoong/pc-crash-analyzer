# 덤프 1개 → 골든 셋 케이스 폴더 (docs/conflict-test-plan.md 3.2 포맷)
#   cases\<dump-id>\engine-snapshot.json   수집기 산출(이미 %USERPROFILE% 치환)
#   cases\<dump-id>\engine-output.json     crash_engine diagnose --json 결과
#   cases\<dump-id>\ground_truth.json      라벨 템플릿 — confidence 를 official / user / guess 중 하나로 채운다
# 사용: powershell -ExecutionPolicy Bypass -File tools\export-case.ps1 -Dump <dumps\20261029-...-crash-1000> [-Out cases] [-Symptom slow]
# 주의: cases\ 는 개인 PC 정보가 들어갈 수 있어 커밋하지 않는다. FaultLab 합성 케이스만 검토 후 수동으로 올린다.
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [string]$Out = "cases",
    [string]$Symptom = "",          # 수동 마킹 덤프의 symptom.type 을 채울 때 (예: slow, freeze)
    [string]$Engine = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Engine) { $Engine = Join-Path $root "engine\build\Release\crash_engine.exe" }
if (-not (Test-Path (Join-Path $Dump "engine-snapshot.json"))) { throw "engine-snapshot.json 이 없습니다: $Dump" }

$id = Split-Path -Leaf $Dump
$outDir = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
$dest = Join-Path $outDir $id
New-Item -ItemType Directory -Force $dest | Out-Null

# 1) 스냅샷 복사 (+ 선택: 증상 타입 채우기 — UI 가 없는 콘솔 흐름에서 수동 마킹은 unknown 으로 남기 때문)
$snapRaw = Get-Content (Join-Path $Dump "engine-snapshot.json") -Raw -Encoding UTF8
$snap = $snapRaw | ConvertFrom-Json
if ($Symptom) { $snap.symptom.type = $Symptom }
$snapOut = Join-Path $dest "engine-snapshot.json"
[System.IO.File]::WriteAllText($snapOut, ($snap | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))

# 2) 엔진 실행 (있으면)
$engineOut = Join-Path $dest "engine-output.json"
if (Test-Path $Engine) {
    & $Engine diagnose $snapOut --json $engineOut | Out-Null
} else {
    Write-Warning "엔진 없음($Engine) — engine-output.json 생략. 빌드: cmake --build engine/build --config Release"
}

# 3) 라벨 템플릿 (이미 있으면 덮어쓰지 않는다)
$gt = Join-Path $dest "ground_truth.json"
if (-not (Test-Path $gt)) {
    $meta = Get-Content (Join-Path $Dump "meta.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $tpl = [ordered]@{
        symptom        = if ($Symptom) { $Symptom } else { $snap.symptom.type }
        cause_category = "unknown"       # M1 hook | M2 driver | M3 filter_scan | M4 gpu_driver | A app_memory | B vram | C thermal | E interference | unknown
        culprit        = $null           # 모듈/프로그램 이름 또는 null
        confidence     = "guess"         # official(벤더 KB) | user(설문) | guess
        what_user_did  = ""
        what_fixed_it  = ""
        source         = ""              # KB URL 또는 '설문 #n'
        collector_kind = $meta.kind
        significance   = if ($meta.significance) { $meta.significance.Score } else { $null }
        exported_at    = (Get-Date).ToString("s")
    }
    [System.IO.File]::WriteAllText($gt, ($tpl | ConvertTo-Json -Depth 5), (New-Object System.Text.UTF8Encoding $false))
}
Write-Host "[case] $dest"
Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Host
