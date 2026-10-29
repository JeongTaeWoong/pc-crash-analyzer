# 참여자판(0.8~) 관리자 경로 시험 — 일반 PowerShell 에서 실행하면 UAC 를 한 번 묻고, 끝나면 결과를 출력한다.
# 사용: powershell -ExecutionPolicy Bypass -File tools\test-participant-admin.ps1 [-Exe <LOCALTEST 빌드의 PcBlackBox.exe>]
#   -Exe 를 안 주면 dist\ 의 가장 최근 *-LOCALTEST 빌드를 쓴다(127.0.0.1 시험 수신기 설정이어야 시험 옵션이 열린다).
# 확인하는 것:
#   1) 기록 폴더 보호: 일반 권한으로 C:\ProgramData\PcBlackBox 를 먼저 만들어 두면 멈추는가 → 지운 뒤 새로 만들면 OK 인가 → 일반 권한으로 쓰기가 거부되는가
#   2) 관리자 실행 A: 동의 → 자동 실행(-TEST 예약 작업 + 시험 설치본) → 직접 기록 → 정상 종료, 커널 ETW 세션 정리
#   3) 예약 작업으로 실행(로그인 때와 같은 경로): 설치본이 관리자 토큰으로 뜨는가, 명령줄 인자
#   4) 관리자 실행 B: 참여 그만두기 → 예약 작업·설치본·기록 삭제
# 남기는 것: C:\ProgramData\PcBlackBox\<SID> (빈 폴더 — 실제 실행 때 쓰는 자리). 시험 폴더는 %TEMP%\pcbb-admin-test.
param(
    [string]$Exe,
    [string]$WorkDir = (Join-Path $env:TEMP 'pcbb-admin-test'),
    [switch]$Elevated
)
$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Exe) {
    $d = Get-ChildItem (Join-Path $repo 'dist') -Directory -Filter '*-LOCALTEST' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $d) { throw "dist\ 에 LOCALTEST 빌드가 없습니다. publish-participant.ps1 -AllowLoopback 으로 먼저 만드세요." }
    $Exe = Join-Path $d.FullName 'PC블랙박스\PcBlackBox.exe'
}
$log = Join-Path $WorkDir 'admin-test.log'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = "PcBlackBox\Autostart-$sid-TEST"
$root = Join-Path $env:ProgramData 'PcBlackBox'
$data = Join-Path $WorkDir 'elev data'       # 공백 포함 — 예약 작업 명령 경로의 따옴표 처리 확인
$startup = Join-Path $WorkDir 'elev startup'
function L($m) { Add-Content -Path $log -Value $m -Encoding UTF8 }

if (-not $Elevated) {
    # ── 일반 권한 쪽: 준비 → 관리자 쪽 실행 → 쓰기 거부 확인 → 결과 출력 ──
    New-Item -ItemType Directory -Force $WorkDir | Out-Null
    Remove-Item $log -ErrorAction SilentlyContinue
    $tools = Join-Path $repo 'participant\StudyTools\bin\Release\net8.0\StudyTools.exe'
    $receiver = $null
    try { Invoke-WebRequest -UseBasicParsing http://127.0.0.1:8787/ -TimeoutSec 3 | Out-Null; L "시험 수신기: 이미 실행 중" }
    catch {
        if (Test-Path $tools) {
            $receiver = Start-Process $tools -ArgumentList @('receive', '--config', "`"$(Join-Path (Split-Path $Exe) 'study.json')`"", '--out', "`"$(Join-Path $WorkDir 'inbox')`"", '--port', '8787', '--mimic-apps-script') -WindowStyle Hidden -PassThru
            Start-Sleep -Seconds 2; L "시험 수신기: 새로 띄움 (받은 봉투 → $WorkDir\inbox)"
        } else { L "시험 수신기: 없음 — 전송 단계는 대기열에 남는다" }
    }
    $squatted = $false
    if (-not (Test-Path $root)) { New-Item -ItemType Directory $root | Out-Null; $squatted = $true }
    L "선점 흉내(일반 권한으로 $root 생성): $squatted"
    Write-Host "UAC 창이 뜨면 '예'를 눌러 주세요 (Windows PowerShell)."
    try {
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Exe', "`"$Exe`"", '-WorkDir', "`"$WorkDir`"", '-Elevated')
    } catch { L "관리자 쪽 실행 안 됨: $($_.Exception.Message)" }
    if ($squatted -and (Test-Path $root) -and ((Get-Acl $root).Owner -notmatch 'Administrators|SYSTEM')) { Remove-Item $root -Recurse -Force; L "선점 폴더 정리(관리자 쪽이 돌지 않음)" }
    L "== 6. 일반 권한으로 기록 폴더 쓰기·권한 변경 시도"
    $udir = Join-Path $root $sid
    if (Test-Path $udir) {
        try { Set-Content (Join-Path $udir 'probe.txt') 'x' -ErrorAction Stop; L "  파일 쓰기: 허용됨 ★문제"; Remove-Item (Join-Path $udir 'probe.txt') }
        catch { L "  파일 쓰기: 거부됨 ($($_.Exception.GetType().Name))" }
        try { New-Item -ItemType Directory (Join-Path $root 'probe') -ErrorAction Stop | Out-Null; L "  루트에 폴더 만들기: 허용됨 ★문제" }
        catch { L "  루트에 폴더 만들기: 거부됨" }
        $out = icacls $udir /grant "${env:USERNAME}:(OI)(CI)F" 2>&1 | Out-String
        L ("  권한 변경(icacls /grant): " + $(if ($LASTEXITCODE -eq 0) { "허용됨 ★문제" } else { "거부됨" }))
    } else { L "  $udir 없음 — 관리자 쪽이 만들지 못함" }
    if ($receiver) { Stop-Process -Id $receiver.Id -ErrorAction SilentlyContinue }
    Get-Content $log -Encoding UTF8
    return
}

# ── 관리자 쪽 ──
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Tok {
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool OpenProcessToken(IntPtr h, uint a, out IntPtr t);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool GetTokenInformation(IntPtr t, int c, out int v, int l, out int r);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Elevated(System.Diagnostics.Process p) {
    IntPtr t; if (!OpenProcessToken(p.Handle, 8, out t)) return "?(" + Marshal.GetLastWin32Error() + ")";
    try { int v, r; return GetTokenInformation(t, 20, out v, 4, out r) ? (v != 0 ? "관리자" : "일반") : "?"; } finally { CloseHandle(t); }
  }
}
'@
function Etw { L ("  커널 ETW 세션: " + (((logman query -ets | Select-String 'PcBlackBox|CrashCollector' | ForEach-Object { ($_.Line.Trim() -split '\s+')[0] }) -join ', ') -replace '^$', '(없음)')) }
function Check($label) { L ("  [$label] " + ((& $Exe --check-data-dir 2>&1 | Out-String).Trim() -replace "`r?`n", ' | ') + " (exit $LASTEXITCODE)") }

L "== 1. 기록 폴더 보호 ($(Get-Date -Format s))"
L "  루트 소유자(시작 시): $((Get-Acl $root -ErrorAction SilentlyContinue).Owner)"
Check '선점 상태'
if ((Get-Acl $root -ErrorAction SilentlyContinue).Owner -notmatch 'Administrators|SYSTEM') { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
Check '새로 만듦'
Check '다시 확인'
L "  루트 ACL:"; (icacls $root) | Where-Object { $_ -match '\S' } | ForEach-Object { L "    $_" }

L "== 2. 관리자 화면 캡처"
& $Exe --render-forms (Join-Path $WorkDir 'render-admin') 2>&1 | Out-Null
L "  $((Get-ChildItem (Join-Path $WorkDir 'render-admin') -ErrorAction SilentlyContinue).Name -join ', ')"

L "== 3. 관리자 실행 A: 동의 → 자동 실행 → 직접 기록 → 종료"
Remove-Item $data, $startup -Recurse -Force -ErrorAction SilentlyContinue
$p = Start-Process $Exe -PassThru -ArgumentList @('--data-dir', "`"$data`"", '--startup-dir', "`"$startup`"", '--test-consent', '--test-mark-after', '20', '--test-auto-answer', '--test-exit-after', '80')
Start-Sleep -Seconds 10
L "  실행 중 토큰: $([Tok]::Elevated($p))"; Etw
$p.WaitForExit(180000) | Out-Null
L "  종료 코드 $($p.ExitCode)"
$xml = (schtasks /Query /TN $task /XML 2>&1 | Out-String)
L "  예약 작업 조회 exit $LASTEXITCODE"
foreach ($k in 'Command', 'Arguments', 'RunLevel', 'ExecutionTimeLimit', 'Delay') { L ("    {0}: {1}" -f $k, [regex]::Match($xml, "<$k>(.*?)</$k>").Groups[1].Value) }
L "  설치본 파일 수: $((Get-ChildItem (Join-Path $startup 'install') -Recurse -File -ErrorAction SilentlyContinue).Count) · 작업 XML 남음: $(Test-Path (Join-Path $startup 'install\autostart-task.xml')) · 바로가기: $((Get-ChildItem $startup -Filter *.lnk -ErrorAction SilentlyContinue).Count)개"
L "  덤프: $((Get-ChildItem (Join-Path $data 'dumps') -ErrorAction SilentlyContinue).Name -join ', ')"
Etw

L "== 4. 예약 작업으로 실행(로그인 때와 같은 경로) → 끝내기"
schtasks /Run /TN $task | Out-Null
Start-Sleep -Seconds 15
$q = Get-Process PcBlackBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $startup 'install\*') } | Select-Object -First 1
if ($q) { L "  실행됨: 토큰 $([Tok]::Elevated($q))"; L ("  명령줄: " + (Get-CimInstance Win32_Process -Filter "ProcessId=$($q.Id)").CommandLine) } else { L "  실행 안 됨 ★문제" }
Etw
schtasks /End /TN $task | Out-Null
Start-Sleep -Seconds 3
L "  /End 뒤 남은 프로세스: $((Get-Process PcBlackBox -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $startup 'install\*') }).Count)"
Etw

L "== 5. 관리자 실행 B: 참여 그만두기"
$p = Start-Process $Exe -PassThru -ArgumentList @('--data-dir', "`"$data`"", '--startup-dir', "`"$startup`"", '--test-withdraw-after', '25')
$p.WaitForExit(180000) | Out-Null
schtasks /Query /TN $task 2>&1 | Out-Null
L "  종료 코드 $($p.ExitCode) · 예약 작업 남음: $($LASTEXITCODE -eq 0) · 설치본 남음: $(Test-Path (Join-Path $startup 'install')) · participant.json 남음: $(Test-Path (Join-Path $data 'participant.json'))"
Etw
