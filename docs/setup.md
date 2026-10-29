# 팀 PC 세팅 가이드 (2026-10-28)

남건호 PC에서 **.NET 8 런타임이 없어 수집기 실행이 실패**한 사례(`exp/collector-autoDetect-test`)를 계기로 정리한다.

## 필수

| 구성 요소 | 용도 | 확인 명령 |
|---|---|---|
| .NET 8 SDK (8.0.x) — **런타임 포함** | 수집기·WPF UI·FaultLab 빌드/실행. SDK 10만 있으면 빌드는 되지만 `net8.0` 실행에 8.0 런타임이 필요 | `dotnet --list-runtimes` 에 `Microsoft.NETCore.App 8.0.x` 와 `Microsoft.WindowsDesktop.App 8.0.x` 가 보여야 함 |
| Visual Studio 2022 (C++ 데스크톱 워크로드 + CMake) | 추론 엔진, FaultPlugin.dll | `cmake --version` (VS 내장: `Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin`) |
| Python 3.10+ (tkinter 포함, python.org 설치본) | UI 시안·에이전트 시안 | `python -c "import tkinter"` |
| Git | | |

## 빌드 순서

```powershell
git clone https://github.com/JeongTaeWoong/pc-crash-analyzer.git
cd pc-crash-analyzer

# 1) 엔진
cmake -S engine -B engine/build -G "Visual Studio 17 2022" -A x64
cmake --build engine/build --config Release
engine\build\Release\ce_tests.exe engine\data            # 144 통과 확인

# 2) 수집기 + 테스트
dotnet build collector/BlackBox -c Release
dotnet test  collector/BlackBox.Tests -c Release           # 트리거 분류·유의성 점수 단위 테스트

# 3) 테스트 프로그램 (골든 셋 생성용)
dotnet build collector/experiments/CrashDummy -c Release
dotnet build collector/experiments/FaultLab -c Release
cmake -S collector/experiments/FaultLab/plugin -B collector/experiments/FaultLab/plugin/build -G "Visual Studio 17 2022" -A x64
cmake --build collector/experiments/FaultLab/plugin/build --config Release   # FaultPlugin.dll

# 4) UI
dotnet build ui/wpf/CrashAnalyzer.UI -c Release            # 또는 ui\prototype-python\run_ui.bat
```

## 실행 권한

- **관리자 권장**: ETW(프로세스·모듈 실시간 추적), 미니필터 목록, 모든 프로세스의 모듈 열거가 열린다.
- 비관리자도 동작하지만 해당 신호는 `unavailable`로 기록되고, 수명이 짧은 프로세스의 모듈 목록은 확보되지 않는다.

## 수집기 설정 (선택)

`collector/BlackBox/blackbox.config.json` (실행 폴더 또는 exe 폴더):

```json
{ "testArtifacts": ["CrashDummy.exe", "FaultLab.exe"], "denylist": [] }
```

- `testArtifacts`: 트리거는 되지만 유의성 점수가 10 이하로 눌려 UI에서 기본 숨김 (데모·리허설 스크립트는 그대로 동작)
- `denylist`: 아예 트리거하지 않을 프로세스

## 골든 셋 만들기 (FaultLab)

| 명령 | 재현하는 것 | 기대 결과 |
|---|---|---|
| `FaultLab plugin` | 네이티브 호스트(FaultHost.exe)가 플러그인 DLL 안에서 크래시 (s6 패턴) | 1000, 앱 = FaultHost.exe, 오류 모듈 = FaultPlugin.dll(비시스템 경로) |
| `FaultLab plugin-inproc` | 비교용: .NET 프로세스 안에서 같은 DLL 호출 | CLR이 가로채 오류 모듈 = **coreclr.dll**로 기록됨 (10/28 실측 — 그래서 네이티브 호스트가 필요) |
| `FaultLab hang 20` | 창이 응답 없음 → **직접 창을 클릭하고 닫기** | 1002 `hang-1002` |
| `FaultLab hang-report 60 6` | 창을 멈춘 뒤 6초 후 **자기 창에 대해 WerReportHang 호출**(사람 조작 불필요, FaultLab 만 종료됨) | 1002 `hang-1002` — 10/29 실수신 확인(기록 0.5초 뒤 수신) |
| `FaultLab cpu 30` / `disk 30 512` / `leak 2048 60` | CPU·디스크 과부하·메모리 점유 | 크래시 없음 — `slow` 수동 마킹과 함께 사용 |
| `FaultLab crash-a/b/c` | CrashDummy와 동일 | 1000 |

## 지인 PC 배포 빌드 (2026-10-29)

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish-collector.ps1 -Version 0.6.0
# → dist\BlackBox-0.6.0-win-x64.zip (약 36MB, .NET 런타임 포함, 설치 불필요)
```

zip 안에 `읽어주세요.md`(= `docs/participant-guide.md`), `blackbox.config.json`, `수집기 시작.bat` 이 함께 들어간다.
코드 서명이 없어 SmartScreen 경고가 뜨며, 안내문에 그 사실을 적어 두었다. `dist/` 는 커밋하지 않는다.

## 참여자판 — 지인 배포·자동 전송 (v0.7, 2026-10-29)

일반인용 트레이 앱 `PcBlackBox.exe` 와 암호화 자동 전송. 절차 전체는 [`study-operations.md`](study-operations.md).

```powershell
dotnet test participant\StudyCore.Tests                       # 봉투·서명·대기열·수신 규칙 48건
participant\StudyTools\bin\Release\net8.0\StudyTools.exe check --config participant\study.local.json
powershell -ExecutionPolicy Bypass -File tools\publish-participant.ps1 -Version 0.8.0
```

## 수집기 콘솔 명령과 검증 모드 (v0.6.1, 2026-10-29)

| 입력 | 하는 일 |
|---|---|
| `m` | 수동 마킹 (증상·대상 없음 — 전경 창이 초점) |
| `m slow blender` | 증상 `slow`, 대상 `blender.exe` 로 마킹 — 롤업 초점·마지막 모듈 목록을 그 프로그램으로 잡는다. 증상: `slow freeze app_crash fps_drop flicker input_loss launch_fail network_fail unexpected_reboot` |
| `s` | 버퍼·롤업 통계 |
| `BlackBox.exe --verify-eventlog [일수]` | 과거 이벤트 로그를 실시간 구독과 같은 파서·분류기로 다시 읽어 트리거 종류별 건수·표본·해석 누락을 출력(읽기 전용, 덤프 없음). 일수 생략 = 로그 전체 |
| `tools\measure-overhead.ps1 -Minutes 30` | 수집기를 띄워 10초 간격 CPU·메모리를 CSV 로. **관리자 터미널에서 실행하면 ETW 포함 조건**. 결과는 `logs\overhead\`(커밋 안 함) |

시작 시 `[boot] 이번 부팅 이후 기록 재생 N건` 이 찍힌다. Kernel-Power 41·BugCheck·WHEA 는 부팅 초기에 기록돼 로그온 뒤 켜지는 수집기가 실시간으로 받을 수 없어서, 시작할 때 한 번 읽어 같은 경로로 덤프한다(이미 덤프한 레코드는 건너뜀).
