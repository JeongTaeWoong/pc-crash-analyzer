# 외부 공개 사례(포럼·이슈 트래커의 로그) → 엔진 스냅샷 형식(engine/docs/snapshot_schema.md v1.2) 변환.
# 결과는 우리가 측정한 데이터가 아니다: 모든 파일에 provenance.type = "external-reconstructed" 와 출처가 붙는다.
# 원칙:
#  - 출처에 없는 값은 만들지 않고 필드를 생략한다(CLAUDE.md 4, snapshot_schema 약속 4). 시각·센서·모듈을 채워 넣지 않는다.
#  - marked_at 은 게시일이 있을 때만 쓰고, 사건 시각이 아니라는 것을 provenance.marked_at_basis 에 적는다.
#  - ground_truth 는 작성자가 해결을 확인했고 원인이 적힌 글에만 붙인다. confidence: 벤더 문서 official / 작성자 확인 user / 그 외 guess.
#  - docs/conflict-test-plan.md 3.4 지표(가설 정확도 등)에 넣지 않는다. 참여자 데이터·cases/ 와 섞지 않는다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\import-external-report.ps1 -In <reports.json> [-Out engine\data\external]
param(
    [Parameter(Mandatory = $true)][string]$In,
    [string]$Out = "engine\data\external",
    # 검토에서 원인 라벨을 믿기 어렵다고 본 id: 케이스는 남기되 ground_truth 를 붙이지 않는다
    [string[]]$LabelHold = @()
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = if ([System.IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $root $Out }
New-Item -ItemType Directory -Force $outDir | Out-Null

$symptoms = 'app_crash', 'freeze', 'fps_drop', 'flicker', 'input_loss', 'launch_fail', 'network_fail', 'unexpected_reboot', 'slow'
$scopes = 'single_app', 'multiple_apps', 'system_wide', 'unknown'
$layerOf = @{
    # engine/src/rules.cpp layerOfCategory 와 같게 유지
    anticheat = 'SW'; antivirus = 'SW'; fin_security = 'SW'; security_firewall = 'SW'; overlay = 'SW'; input_util = 'SW'; other_sw = 'SW'; app_memory = 'SW'
    gpu_driver = 'DRIVER'; display_util = 'DRIVER'; peripheral_driver = 'DRIVER'; network_driver = 'DRIVER'; os_update = 'DRIVER'; other_driver = 'DRIVER'
    thermal = 'HW'; power = 'HW'; connection = 'HW'; storage = 'HW'; memory = 'HW'
}

function Test-HasValue($v) { return ($null -ne $v) -and ("$v".Trim().Length -gt 0) }

# null·빈 값은 필드째 뺀다 — 엔진이 '확인 불가'로 구분하게
function New-Compact($map) {
    $o = [ordered]@{}
    foreach ($k in @($map.Keys)) {
        $v = $map[$k]
        if ($v -is [array]) { if ($v.Count -gt 0) { $o[$k] = $v } }
        elseif ($v -is [System.Collections.IDictionary]) { if ($v.Count -gt 0) { $o[$k] = $v } }
        elseif (Test-HasValue $v) { $o[$k] = $v }
    }
    return $o
}

function ConvertTo-Id($v) {
    $n = 0
    if ([int]::TryParse("$v", [ref]$n)) { return $n }
    return $null
}

function Get-MarkedAt($posted) {
    if (-not (Test-HasValue $posted)) { return $null }
    $d = [DateTime]::MinValue
    if ([DateTime]::TryParse("$posted", [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$d)) {
        return $d.ToString("yyyy-MM-dd") + "T00:00:00+00:00"
    }
    return $null
}

# 글에 남아 있을 수 있는 개인정보를 다시 한번 지운다(JSON 이스케이프된 본문 기준)
function Remove-Personal([string]$json) {
    $json = [regex]::Replace($json, '(?i)[A-Z]:\\\\Users\\\\[^\\"]+', '%USERPROFILE%')
    $json = [regex]::Replace($json, '[\w.+-]+@[\w-]+\.[\w.-]+', '[email]')
    $json = [regex]::Replace($json, '(?i)\b(DESKTOP|LAPTOP)-[A-Z0-9]{5,}\b', '%COMPUTERNAME%')
    return $json
}

# Windows PowerShell 5.1 의 ConvertFrom-Json 은 배열을 한 덩어리로 내보낸다 — 변수에 먼저 받아야 원소별로 돈다
$parsed = Get-Content $In -Raw -Encoding UTF8 | ConvertFrom-Json
$reports = @($parsed)
$written = 0
$skipped = New-Object System.Collections.ArrayList
$byConfidence = @{}

foreach ($r in $reports) {
    $id = ("$($r.id)" -replace '[^A-Za-z0-9_-]', '-') -replace '^ext-', ''
    if (-not (Test-HasValue $id)) { [void]$skipped.Add("(id 없음): id"); continue }
    if (-not (Test-HasValue $r.source_url)) { [void]$skipped.Add("${id}: source_url 없음"); continue }
    if ($symptoms -notcontains $r.symptom_type) { [void]$skipped.Add("${id}: 알 수 없는 증상 '$($r.symptom_type)'"); continue }

    $events = @(foreach ($e in @($r.events)) {
        if ($null -eq $e) { continue }
        New-Compact ([ordered]@{ provider = $e.provider; id = (ConvertTo-Id $e.id); subject = $e.subject; faulting_module = $e.faulting_module; exception_code = $e.exception_code })
    })
    $dumps = @(foreach ($m in @($r.minidumps)) {
        if ($null -eq $m) { continue }
        New-Compact ([ordered]@{ kind = $m.kind; process = $m.process; faulting_module = $m.faulting_module; bugcheck = $m.bugcheck })
    })
    $target = if (Test-HasValue $r.target_process) { "$($r.target_process)" } else { "" }
    $modules = @(foreach ($m in @($r.modules)) {
        if ($null -eq $m -or -not (Test-HasValue $m.name)) { continue }
        New-Compact ([ordered]@{ process = $target; name = $m.name; company = $m.company; version = $m.version })
    })
    $drivers = @(foreach ($d in @($r.drivers)) {
        if ($null -eq $d -or -not (Test-HasValue $d.name)) { continue }
        New-Compact ([ordered]@{ name = $d.name; version = $d.version })
    })
    $changes = @(foreach ($c in @($r.change_history)) {
        if ($null -eq $c -or -not (Test-HasValue $c.subject)) { continue }
        New-Compact ([ordered]@{ kind = $c.kind; subject = $c.subject; version = $c.version })
    })
    $processes = @(@($r.processes) | Where-Object { Test-HasValue $_ } | ForEach-Object { "$_" })
    $realtimeAv = @(@($r.realtime_av) | Where-Object { Test-HasValue $_ } | ForEach-Object { "$_" })
    $keyboardUpper = @(@($r.keyboard_upper) | Where-Object { Test-HasValue $_ } | ForEach-Object { "$_" })
    # 벤더 설명만 있고 관측값이 없는 글은 "로그"가 아니다 — 스냅샷을 만들지 않고 규칙의 근거 자료로만 쓴다
    if (($events.Count + $dumps.Count + $modules.Count + $drivers.Count + $processes.Count + $keyboardUpper.Count) -eq 0) { [void]$skipped.Add("${id}: 관측값 없음($($r.evidence_kind))"); continue }

    $faulting = @(@($events + $dumps) | ForEach-Object { $_['faulting_module'] } | Where-Object { Test-HasValue $_ } | Select-Object -Unique)

    # 트리거 종류: 글에 있는 이벤트로만 고른다
    $system = @($events | Where-Object { "$($_['provider'])" -match 'Kernel-Power|BugCheck|WHEA|Display|nvlddmkm|amdkmdag|disk|Ntfs|SystemErrorReporting' }).Count -gt 0 -or
              @($dumps | Where-Object { $_['kind'] -eq 'kernel' }).Count -gt 0
    $appEvent = @($events | Where-Object { @(1000, 1001, 1002) -contains $_['id'] }).Count -gt 0
    $kind = if ($system) { 'auto_system' } elseif ($appEvent) { 'auto_app_crash' } else { 'hotkey' }
    $trigger = New-Compact ([ordered]@{ kind = $kind; event_id = $(if ($events.Count) { $events[0]['id'] } else { $null }) })

    $scope = if ($scopes -contains $r.scope) { $r.scope } else { 'unknown' }
    $marked = Get-MarkedAt $r.posted_date

    $confirmed = ($null -ne $r.resolution) -and ($r.resolution.confirmed_by_poster -eq $true)
    $held = $LabelHold -contains "$($r.id)"
    $confidence = if ($held) { 'guess' } elseif ($r.grade -eq 'A') { 'official' } elseif ($confirmed) { 'user' } else { 'guess' }
    $byConfidence[$confidence] = 1 + [int]$byConfidence[$confidence]

    $snapshot = New-Compact ([ordered]@{
        schema_version = '1.2'
        snapshot_id    = "ext-$id"
        marked_at      = $marked
        trigger        = $trigger
        host           = (New-Compact ([ordered]@{ os = $r.os }))
        symptom        = (New-Compact ([ordered]@{ type = $r.symptom_type; target_process = $target; scope = $scope; faulting_modules = $faulting }))
        processes      = $processes
        modules        = $modules
        drivers        = $drivers
        device_filters = (New-Compact ([ordered]@{ keyboard_upper = $keyboardUpper }))
        security       = (New-Compact ([ordered]@{ realtime_av = $realtimeAv }))
        change_history = $changes
        events         = $events
        minidumps      = $dumps
    })
    if ($snapshot['symptom'].Contains('target_process') -eq $false) { $snapshot['symptom']['target_process'] = '' }
    if ($confirmed -and -not $held -and (Test-HasValue $r.culprit) -and $layerOf.ContainsKey("$($r.category)")) {
        $truth = [ordered]@{ layer = $layerOf["$($r.category)"]; category = "$($r.category)" }
        # 글의 원인 표현에는 설명이 섞여 있다 — 파일 이름이 있을 때만 subject 로 쓰고, 없으면 범주만 맞춘다
        $named = [regex]::Match("$($r.culprit)", '[\w.-]+\.(sys|dll|exe)', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if ($named.Success) { $truth['subject'] = $named.Value }
        $snapshot['ground_truth'] = $truth
    }
    $snapshot['provenance'] = New-Compact ([ordered]@{
        type                = 'external-reconstructed'
        measured_by_team    = 'false'
        source_url          = $r.source_url
        site                = $r.site
        posted_date         = $r.posted_date
        grade               = $r.grade
        confidence          = $confidence
        confirmed_by_poster = "$confirmed".ToLowerInvariant()
        label_withheld      = $(if ($held) { 'true' } else { $null })
        culprit_reported    = $r.culprit
        what_fixed_it       = $(if ($null -ne $r.resolution) { $r.resolution.what_fixed_it } else { $null })
        category_reported   = $r.category
        program             = $r.program
        conflict_with       = $r.conflict_with
        mechanism           = $r.mechanism
        evidence_kind       = $r.evidence_kind
        # 글에 적힌 자원 수치는 여기에만 둔다 — resource_trends 는 60분 측정값이라 글의 숫자로 채우면 안 된다
        perf_evidence       = @($r.perf_evidence | Where-Object { $null -ne $_ })
        marked_at_basis     = $(if ($marked) { 'posted_date, not the time of the incident' } else { 'none: source gave no date' })
        verbatim_excerpt    = $r.verbatim_excerpt
        absent_from_source  = @($r.missing)
    })

    $json = Remove-Personal ($snapshot | ConvertTo-Json -Depth 12)
    [System.IO.File]::WriteAllText((Join-Path $outDir "ext-$id.json"), $json, (New-Object System.Text.UTF8Encoding($false)))
    $written++
}

Write-Host "[import] 변환 $written 건 → $outDir"
Write-Host ("[import] 신뢰 등급: " + (($byConfidence.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '))
if ($skipped.Count) { Write-Host "[import] 건너뜀 $($skipped.Count) 건:"; $skipped | ForEach-Object { Write-Host "  - $_" } }
