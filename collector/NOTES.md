# collector NOTES

## 환경 (2026-10-08 확인, 읽기 전용 PowerShell로 조사)

| 항목 | 값 | 비고 |
|---|---|---|
| OS | Windows 10 Education 22H2 (10.0.19045) x64 | Win11 아님 — Win11 전용 API 주의 |
| .NET SDK | 8.0.300 (런타임 8.0.5 / 8.0.14) | 프로젝트 타깃 .NET 8과 일치 |
| 터미널 권한 | 10-08 낮까지 비관리자 → **10-08 밤부터 VS Code 관리자 실행으로 승격됨** | 이후 세션은 관리자 전제. 비관리자 배포 시나리오 테스트는 별도 터미널 필요 |
| CPU | AMD Ryzen 7 9800X3D (8C/16T, Zen 5) | 최신 아키텍처 — LHM 센서 지원 버전 확인 필요 |
| GPU (외장) | NVIDIA GeForce RTX 4070 Ti SUPER (드라이버 32.0.16.1692) | NVAPI 경로 비관리자 센서 조회 **검증됨** (스파이크 E) |
| GPU (내장) | AMD Radeon Graphics (Ryzen iGPU) | 활성 상태 — 센서도 나옴 (스파이크 E) |
| 메인보드 | ASUS TUF GAMING B850M-PLUS (BIOS AMI 1022) | B850 신형 칩셋 — LHM SuperIO 지원 미확인 |
| RAM | 총 93.6 GiB 가용 (96GB 설치 추정) | |
| 보안 소프트웨어 | SecurityCenter2 등록: Windows Defender만. **단, 커널 드라이버 열거 결과 한국 금융보안 모듈 다수 상주**: AhnLab Safe Transaction(드라이버 5종), ImageSAFER(ISMgr), TouchEn(TKFWFV), JRSUKD25 | SecurityCenter2만 보면 놓친다 — 드라이버 열거가 필수 신호인 근거 |
| 안티치트 드라이버 | vgk/vgc/BEDaisy/EasyAntiCheat*.sys 미존재, 서비스 미등록. **단 10-08 저녁 재검증에서 xhunter2.sys(Wellbia/XIGNCODE 계열, `C:\Windows\xhunter2.sys`) Running 발견** — 아침 조사엔 없던 드라이버 | 안티치트 상주가 포착됨 — "드라이버 목록은 시점에 따라 변한다"는 수집 설계 근거. Vanguard/EAC/BattlEye 충돌은 여전히 재현 불가 |
| 키보드/마우스 UpperFilters | `kbdclass` / `mouclass` (기본값만) | 서드파티 입력 필터 없음 |
| WER LocalDumps | 전역 미설정 (루트 값 없음). 벤더 등록 앱별 하위 키 21개 존재 (NVIDIA, Razer, LOSTARK, STOVE 등) | 전역 덤프를 원하면 **설정 필요** (수집기는 켜지 않음) |

잔존 흔적: Razer Synapse용 WER 키·CrashDumps 경로가 남아있으나 Razer 서비스/필터 드라이버는 현재 미등록 (제거 추정).

### 조사 중 확인된 함정 (코드 작성 시 주의)

- `Win32_VideoController.AdapterRAM`은 uint32라 4GB 오버플로 — VRAM은 WMI로 읽지 말 것 (NVML 또는 레지스트리 `HardwareInformation.qwMemorySize`).
- `Get-Service`로 없는 서비스 조회 시 `-ErrorAction SilentlyContinue`여도 비정상 종료 코드 반환.
- `fltmc`는 비관리자에서 exit 1, `0x80070005 Access is denied` — 미니필터 목록은 권한 필요.
- LHM 비관리자: CPU는 하드웨어로 감지되지만 센서가 null이 아니라 **아예 열거되지 않음** (0개). "null = 미지원" 판정 로직만으론 부족, "센서 0개"도 구분해 기록해야 함.
- WUA COM(`Microsoft.Update.Session`)은 비관리자로 동작. 이력 2,225건 — 전수 조회하지 말고 기간 제한 필수.

## 신호별 상태 (스파이크 실행 결과, 2026-10-08, 비관리자)

스파이크 코드: `collector/spikes/A-*` ~ `H-*`, 실행기 `run-all.ps1`. 각각 JSON 한 줄 출력.

| 신호 | 상태 | 비고 |
|---|---|---|
| A. 이벤트 로그 실시간 구독 (Application 1000/1001, System 41/4101/WHEA 17~20) | **검증됨** | EventLogWatcher 구독 Application/System 모두 성공. 7일 내 실데이터: WER 1001 ×17, AppError 1000 ×4, **Kernel-Power 41 ×1 (10-05 비정상 종료 실기록)**. 4101(TDR)/WHEA는 7일 내 0건 — 코드 경로는 동작, 실이벤트 대기 |
| B. ETW 커널 (ProcessStart/Stop, ImageLoad) | **검증됨 (관리자 한정, 본 코드 구현 완료)** | BlackBox v0.3에 실구현 — 비시스템 경로 필터 포함, 비관리자는 스텁(unavailable) 유지. 유휴 10분 실측은 "ETW 실구현" 절 |
| C. 프로세스 모듈 열거 | **검증됨** | notepad(직접 띄움) 55개 모듈, 시스템 경로 밖 0개. 타 사용자/보호 프로세스(안티치트 보호 게임 등)는 **미확인** |
| D-1. 키보드/마우스 UpperFilters | **검증됨** | kbdclass/mouclass 반환 |
| D-2. fltmc 미니필터 목록 | **검증됨 (관리자 한정)** | 관리자에서 17종 확인 (WdFilter, AhnLab Safe Transaction 4종, gameflt, cbfltfs4 등). 비관리자는 0x80070005 |
| D-3. 커널 드라이버 열거 (Win32_SystemDriver) | **검증됨** | 전체 462 / 실행 중 189. 표준 경로 밖 실행 드라이버 10종 — 전부 한국 금융보안·NVIDIA 계열 (아래 샘플) |
| E-1. GPU 센서 (NVIDIA + AMD iGPU 온도/클럭/전력/팬/TDP%) | **검증됨 (v0.4 본 코드 반영)** | 비관리자로도 성공(NVAPI/ADL). v0.4에서 LHM 0.9.7 + GPU 기준 전환 — "SensorSource GPU 전환" 절 참고 |
| E-2. CPU 온도/클럭 (Zen 5) | **미지원 (현 구성, 원인 확정)** | 0.9.3: Defender가 WinRing0 드라이버 차단(SCM 7000 실측). 0.9.7-pre749: PawnIO 백엔드로 전환됐으나 PawnIO 미설치 → 센서는 열거되나 전부 0값. 상세는 "CPU 센서 원인 확정" 절 |
| E-3. 메인보드 SuperIO (팬/전압) | **미지원 (현 구성, 원인 확정)** | 동일 — 0.9.7에서도 보드 센서 0개 |
| F. 설치 이력 (Uninstall 레지스트리 64/32/HKCU + WUA 이력) | **검증됨** | 30일 내 설치 29건+ (InstallDate 없는 항목 127건은 별도 집계 필요), WU 이력 조회 성공 |
| G. 방화벽 로그 (pfirewall.log) | **설정 필요** (읽기 코드는 검증됨) | 전 프로필 로깅 비활성, 로그 파일 없음. 원칙대로 켜지 않음 — 수집기는 "꺼져 있음"을 데이터로 기록 |
| H. 안정성 모니터 (Win32_ReliabilityRecords) | **검증됨** | 30일 내 104건: AppError 9, AppHang 3, WU 68, MsiInstaller 17, EventLog 7 |
| WMI 하드웨어 인벤토리 / SecurityCenter2 / UpperFilters·WER 레지스트리 읽기 | 검증됨 | 환경 조사 단계에서 비관리자 성공 |
| 이벤트 로그 Security 구독 | 권한 필요 | 미시도 — Event Log Readers 그룹/관리자 필요 (예상) |
| 안티치트 드라이버 충돌 관찰 | 미지원 | 이 PC에 안티치트 미설치. 존재 탐지 코드만 가능 |
| 서드파티 입력 필터(UpperFilters 이상) 관찰 | 미지원 | 기본값뿐 — 이상 사례 재현 불가 |
| 배터리/전원(노트북) | 미지원 | 데스크톱 |
| Win11 전용 API | 미지원 | Win10 22H2 |

## 검증된 신호 출력 샘플 (실제 스파이크 출력, 일부 생략)

### A. 이벤트 로그 — Kernel-Power 41 실기록 포함
```json
{"signal":"A-EventLog","subscribe":{"Application":"ok","System":"ok"},
 "last7days":{"Application/Windows Error Reporting/1001":17,"Application/Application Error/1000":4,"System/Microsoft-Windows-Kernel-Power/41":1},
 "recentSamplePerLog":{"System":{"id":41,"provider":"Microsoft-Windows-Kernel-Power","time":"2026-10-05T07:15:44",
   "message":"시스템이 비정상적으로 종료된 후 다시 부팅되었습니다. ..."}},"status":"ok"}
```

### C. 모듈 열거
```json
{"signal":"C-Modules","processName":"notepad","pid":34312,"spawnedForTest":true,"moduleCount":55,"nonSystemCount":0,"nonSystemModules":[],"status":"ok"}
```

### D. 커널 드라이버 — 표준 경로 밖 실행 중 드라이버 (크래시 용의자 후보군)
```json
{"kernelDrivers":{"total":462,"running":189,"nonStandardPathRunning":[
  {"Name":"AntiStealth_SafeTransaction","path":"\\??\\c:\\program files\\ahnlab\\safe transaction\\ahawkent.sys"},
  {"Name":"ISMgr","path":"\\??\\c:\\windows\\system32\\imagesaferdrv64.sys"},
  {"Name":"JRSUKD25","path":"\\??\\c:\\windows\\system32\\jrsukd25.sys"},
  {"Name":"TKFWFV","path":"c:\\windows\\system32\\tkfwfv64.sys"},
  {"Name":"FvKMDSvc","path":"\\??\\c:\\windows\\system32\\vrfcat-plugins\\frameviewkm_x64.sys"} ]}}
```
(AhnLab Safe Transaction 5종 중 1종만 표기. 전체는 스파이크 재실행으로 확인)

### E. 센서 — 비관리자 GPU 성공분
```json
{"sensorValues":[
  {"hardware":"NVIDIA GeForce RTX 4070 Ti SUPER","type":"Temperature","name":"GPU Core","value":42},
  {"hardware":"NVIDIA GeForce RTX 4070 Ti SUPER","type":"Temperature","name":"GPU Hot Spot","value":52.4},
  {"hardware":"NVIDIA GeForce RTX 4070 Ti SUPER","type":"Clock","name":"GPU Core","value":2685},
  {"hardware":"NVIDIA GeForce RTX 4070 Ti SUPER","type":"Power","name":"GPU Package","value":46.2},
  {"hardware":"AMD Radeon(TM) Graphics","type":"Temperature","name":"GPU VR SoC","value":60}],
 "isElevated":false,"status":"ok"}
```

### F. 설치 이력 (발췌)
```json
{"recentInstalls_30d":[
  {"name":"Copilot","date":"2026-10-07","version":"154.0.4258.62","hive":"HKLM32"},
  {"name":"NVIDIA 그래픽 드라이버 616.92","date":"2026-09-11","version":"616.92","hive":"HKLM64"}],
 "entriesWithoutInstallDate":127,"updateHistoryTotalCount":2225,"status":"ok"}
```

### H. 안정성 레코드 — 실제 크래시 샘플
```json
{"total_30d":104,"bySource":{"Application Error":9,"Application Hang":3,"Microsoft-Windows-WindowsUpdateClient":68,"MsiInstaller":17,"EventLog":7},
 "recentSamples":[{"source":"Application Error","eventId":1000,"product":"NexonPlug.exe","time":"2026-10-07T07:51:38",
   "message":"오류 있는 응용 프로그램 이름: NexonPlug.exe, ... 오류 있는 모듈 이름: nexonlink-listener.dll, ... 예외 코드: 0xc0000409 ..."}]}
```

## 이 PC가 테스트베드로 좋은 이유 (실데이터 보유)

- 10-05 Kernel-Power 41 (비정상 종료) 1건 — 블랙박스가 잡아야 할 바로 그 시나리오의 실기록
- 7일 내 WER 1001 ×17, 30일 내 AppError 9건·AppHang 3건 (NexonPlug 크래시 등)
- 금융보안 커널 드라이버 8종+ 상주 — "SecurityCenter2에 안 보이는 커널 상주물" 수집 근거 확보

## BlackBox v0.1 (2026-10-08, 검증된 신호만 통합)

`collector/BlackBox/` — ISignalSource 4종(eventlog, sensors 2s, processes 10s, etw-kernel 스텁) → 시간 기반 RingBuffer(메모리 전용) → 마킹(Ctrl+Shift+F12 또는 m+Enter) 시 [T-5분, T+30초]를 `dumps/<ts>/{meta,events,snapshot}.json`으로 덤프. 디스크 쓰기는 덤프가 유일.

실측 (비관리자, 65초 가동 후 마킹 1회):

| 항목 | 값 |
|---|---|
| 소스 상태 | eventlog ok / sensors ok(12값) / processes ok / etw-kernel unavailable(스텁) / hotkey ok |
| 덤프 크기 | events.json 136KB + snapshot.json 155KB + meta.json 1KB ≈ **292KB** |
| 덤프 이벤트 | 32건 (sensors 26, processes 6 — 이벤트로그는 윈도 내 발생 없음) |
| 버퍼 메모리 | 65초 시점 38건 ≈ 308KB (근사). 5.5분 보존 가득 차면 **~1.6MB 추정** |
| 프로세스 GC 힙 | 18.2MB |
| 마킹 스냅샷 | 프로세스 390개(접근 거부 204 — 비관리자 정상), 비시스템 모듈 보유 156개, 소요 8.0초 |
| 치환 검증 | 덤프 내 원시 사용자명 0건, %USERPROFILE% 150건 |

구현 중 확정된 사실:
- RegisterHotKey(Ctrl+Shift+F12)는 hWnd=NULL(스레드 큐)+GetMessage 루프로 콘솔에서 **검증됨**.
- 버퍼 보존 기간은 pre(5분)가 아니라 **pre+post+여유**여야 함 — T+30초 덤프 시점엔 T-5분 이벤트가 5.5분 전이라 5분 보존이면 이미 밀려남.
- 마킹 스냅샷 8초 중 대부분이 전체 프로세스 모듈 열거. 상시 부하는 없음(마킹 시에만).

## 자동 감지 마킹 — BlackBox v0.2 (2026-10-08)

트리거: Application 1000/1001, System 41/4101/WHEA 17~20 수신 → 즉시 자동 마킹.
덤프 폴더: `dumps/<ts>-<kind>` (manual / crash-1000 / crash-1001 / power-41 / display-4101 / whea).
meta.json `trigger`에 발동 이벤트 원문 포함. 쿨다운 10초: 같은 크래시의 1000→1001 연쇄는 첫 덤프에 병합.

### 모듈 스냅샷 전략 벤치 (60초씩, 16LP 머신)

| 전략 | CPU | 1코어 기준 | 머신 전체 | 판정 |
|---|---|---|---|---|
| FullSweepEvery60s (60초 전체 스윕 + 10초 새 프로세스) | 3,859ms | 7.05% | 0.44% | 탈락 |
| **ForegroundPlusRecent (10초마다 전경+최근 60초 시작)** | **344ms** | **0.69%** | **0.043%** | **채택** |

채택 트레이드오프: 오래 떠 있던 백그라운드 프로세스(전경 아님)의 모듈은 버퍼에 없음.
크래시 덤프의 모듈 매칭은 "최근 시작했거나 전경이었던 프로세스"에 한정됨 — 중간발표 때 한계로 명시.

### CrashDummy 9회 자동 감지 테스트 (a=즉시 예외, b=5초 후 FailFast, c=15초 후 예외)

| 회차 | 유형 | 이벤트 1000 수신 | 자동 덤프 | 크래시→이벤트 지연 | 크래시→덤프 완료 |
|---|---|---|---|---|---|
| 1 | a | O | O | 122ms | 30.18s |
| 2 | a | O | O | 83ms | 30.10s |
| 3 | a | O | O | 90ms | 30.11s |
| 4 | b | O | O | 81ms | 30.10s |
| 5 | b | O | O | 81ms | 30.10s |
| 6 | b | O | O | 83ms | 30.10s |
| 7 | c | O | O | 90ms | 30.10s |
| 8 | c | O | O | 87ms | 30.11s |
| 9 | c | O | O | 89ms | 30.11s |

**자동 감지 성공률: 9/9.** 덤프 지연 30.1s는 설계값(post 윈도 30초) — 순수 오버헤드는 ~0.1s.
(c) 모듈 포함: 1차 2/3 → 아래 버그 2건 수정 후 재검증 **3/3 (전부 PID 일치, 크래시 2.8~4.5초 전 스냅샷)**.

### 테스트로 찾은 버그 2건 (수정 완료)

1. **이벤트 1000의 P9(프로세스 ID)는 `0x` 접두사 없는 16진수 문자열.** 메시지 렌더링에서만 "0x"가 붙는다.
   십진 파싱하면 "6880"(=0x6880=26752)이 6880으로 읽혀 **실존하는 엉뚱한 PID와 오매칭**(9회차에서 실제 발생).
2. **이름 폴백 매칭이 크래시 이후 시작된 동명 프로세스를 집어옴.** 모듈 검색을 마킹 시점 이전 스냅샷으로 제한해 수정.

### 부수 발견

- **WER이 리포트 수집 동안 크래시 프로세스를 1초+ 잡아둔다** — 수명 90ms짜리 case (a)도 3회차에선 모듈 스냅샷에 잡혔다(죽는 중인 프로세스가 틱에 걸림). 즉시 사망 크래시도 운 좋으면 모듈 확보 가능.
- 이벤트 1001(WER 보고 완료)은 보통 1000의 1~2초 뒤에 오지만 **35초 뒤에 온 사례도 있음** → 쿨다운(10초) 밖이라 crash-1001 단독 덤프 생성. 엔진에서 ReportId로 1000 덤프와 연결하면 됨.
- b(FailFast)·a(즉시) 케이스의 모듈 확보는 확률적(스냅샷 틱 10초 주기와 수명의 교차에 달림). 보장되는 건 수명 ≥10초인 c 유형뿐.

## 관리자 권한 재검증 (2026-10-08 저녁, UAC 승격 러너로 실행)

| 신호 | 비관리자 | 관리자 | 관리자가 추가로 열어주는 것 |
|---|---|---|---|
| B. ETW 커널 (Process/ImageLoad) | unavailable (NT Kernel Logger 거부) | **ok — 10초간 ImageLoad 28건** | DLL 로드 실시간 추적, 프로세스 생성/종료 즉시 감지 (10초 폴링 → 이벤트 단위) |
| D-2. fltmc 미니필터 | unavailable (0x80070005) | **ok — 17종** (WdFilter, AhnLab 4종, gameflt, cbfltfs4, storqosflt 등) | 파일시스템 필터 스택 가시성 — AV·보안SW 충돌 분석의 핵심 신호 |
| E. GPU 센서 | ok (12값) | ok (12값, 동일) | 없음 — GPU는 비관리자로 충분 |
| E. CPU/보드 센서 | 0개 | **0개 (동일)** | **없음 — 권한 문제가 아니었다.** LHM 0.9.3의 Zen5/B850 미지원 또는 WinRing0 차단 추정 (미확인) |

부수 발견: 커널 드라이버 열거에서 **xhunter2.sys (Wellbia/XIGNCODE 계열 안티치트) Running** — 아침 조사(189개)엔 없다가 저녁(191개)에 등장. 드라이버 상주 목록은 시점 의존적이므로 수집기는 "마킹 시점 목록"을 떠야 한다는 설계가 맞다.

### ETW ImageLoad를 링버퍼에 넣을 때 메모리 추정

- 실측: 유휴 상태 2.8건/초 (28건/10초). ProcessStart/Stop은 유휴 10초간 0건.
- 이벤트당 payload(JSON: pid, 프로세스명, 이미지 경로) ≈ 150자 → SignalEvent 근사 **~400B**.
- 버퍼 보존 6분 기준: 2.8 × 360 ≈ 1,008건 × 400B ≈ **유휴 ~0.4MB** (현재 버퍼 ~1.6MB 추정에 +25%).
- 주의: 앱/게임 실행 순간 수백 건 버스트 (프로세스 하나가 DLL 200~500개 로드). 부하 시 수 MB까지 가능 —
  실구현 시 비시스템 경로만 남기는 필터링으로 80%+ 절감 권장.
- fltmc 출력 파싱 버그 수정(2026-10-08): 구분선 `---`이 필터명으로 집계되던 것 — 스파이크 D·BlackBox 둘 다 수정.

## CPU 센서 0개 원인 확정 (2026-10-08 밤, 읽기 전용 조사 + LHM 0.9.7-pre749 재시도)

조사 결과 (전부 읽기만):

| 확인 항목 | 결과 | 판정 |
|---|---|---|
| HVCI(코어 격리 메모리 무결성) | 레지스트리 키 자체 없음, Win32_DeviceGuard: VBS 상태 0(꺼짐) | 원인 아님 |
| 취약 드라이버 차단 목록 | `VulnerableDriverBlocklistEnable` 값 없음(기본), CodeIntegrity 3077/3078 이벤트 0건 | 원인 아님 |
| SCM 7000 이벤트 | **"R0E-Sensors 서비스 시작 실패 — 파일에 바이러스/PUA 포함" 6건** (10-08 19:08 ×4, 20:54 ×2 — LHM 0.9.3 실행 시각과 일치) | **원인: Defender가 WinRing0 드라이버 파일을 차단** |

LHM 0.9.7-pre749 재시도 (관리자, 21:06):

- CPU 센서가 **열거는 됨** (Tctl/Tdie, 코어별 클럭/전력 33개) — 그러나 **전부 값 0**, 클럭 8개는 null. 보드 SuperIO는 여전히 0개.
- 이번엔 SCM 7000 차단 이벤트 **없음** → 신버전은 WinRing0을 시도조차 안 함. 최신 LHM은 백엔드가 **PawnIO**(서명 드라이버, 별도 설치)로 바뀌었고, 이 PC엔 PawnIO가 **미설치** (폴더·드라이버 파일·서비스 모두 없음 확인).
- GPU는 0.9.7에서 센서가 오히려 늘었음 (NVIDIA Memory Junction 추가, 총 12→13값).

**결론 (확정): 현 보안 구성에서 CPU/보드 센서는 미지원. 수집기는 GPU 센서 기준으로 간다.**
수집기는 보안 설정을 변경하지 않는다는 원칙(CLAUDE.md 3번)에 따라 차단을 우회하지 않는다. PawnIO(LHM 공식 서명 드라이버 백엔드)를 별도 설치하면 열릴 가능성이 있으나, 설치 여부는 사용자 결정 사항으로 남긴다.

새 함정: **LHM 0.9.7은 백엔드가 없으면 CPU 센서를 null이 아니라 "값 0"으로 반환** — "null = 미지원" 판정 로직으로 못 잡는다. Tctl/Tdie=0 같은 물리적 불가능 값은 무효로 처리해야 함 (BlackBox LHM 버전업 시 필수 반영).

## ETW 실구현 + 유휴 10분 실측 — BlackBox v0.3 (2026-10-08 밤, 관리자)

구현: TraceEvent 3.1.8 추가. EtwKernelSource가 관리자면 Kernel Process+ImageLoad 실구독,
ImageLoad는 비시스템 경로(System32·SysWOW64·WinSxS·SystemApps 제외)만 버퍼 적재.
비관리자면 기존 스텁(unavailable) 유지. RingBuffer에 소스별 통계(StatsBySource) 추가.

실측 (60초 간격 stats 샘플링, 버퍼 보존 6분):

| 구간 | ImageLoad 전체 | 필터 통과(적재) | ETW 버퍼 기여 |
|---|---|---|---|
| 유휴 6분 (21:31~21:37) | 4.5건/초 | 1.4건/초 (**필터 69% 컷**) | 정상 상태 569건 ≈ **0.18MB** |
| 부하 버스트 ~2분 (21:37~21:39, 시스템 활동 원인 불특정) | ~320건/초 | ~42건/초 (**필터 87% 컷**) | 최대 7,712건 ≈ **2.8MB** (전체 버퍼 4.8MB) |

추정치(0.4MB) 대비:
- 0.4MB는 **무필터** 가정(2.8건/초 × 400B)이었다. 실측 무필터 환산: 4.5건/초 × 360초 × 평균 332B ≈ **0.53MB** — 추정과 같은 자릿수(+33%). 평균 payload 332B도 추정 400B와 근사.
- 필터 덕에 **실제 기여는 0.18MB** — 추정의 절반 이하. "비시스템 경로 필터로 80%+ 절감" 권장은 유휴 기준 69%로 실측됨 (부하 시 87%).
- 부하 버스트에도 링버퍼 시간창(6분) 때문에 메모리는 상한이 잡힌다 — 2분간 초당 320건을 맞아도 ETW 기여 2.8MB에서 멈췄고, 버스트 종료 후 보존 기간이 지나면 자연 감소.
- ProcessStart/Stop: 유휴 ~0.2건/초, 버스트 시 ~20건/초. CommandLine은 200자 절단 저장(덤프 시 %USERPROFILE% 치환 적용됨).

### ETW 1차 크래시 모듈 매칭 — BlackBox v0.3.1 (2026-10-09 새벽)

변경: 크래시 프로세스 모듈 매칭을 **ETW ImageLoad 누적(PID 기준) 1차 → 주기 스냅샷 폴백** 순서로.
- PID 재사용 가드: 그 PID의 마지막 processStart(마킹 이전) 이후 ImageLoad만 누적
- **ETW 실시간 전달 지연 1~3초 실측** → ImageLoad는 마킹 +5초까지 유예 수용 (PID 일치 조건이라 오염 없음).
  유예 전엔 수명 100ms급 크래시의 로드 이벤트가 "마킹 이후 도착"으로 버려져 a 케이스 매칭 실패했음
- 주기 스냅샷의 모듈 수집은 관리자에서도 유지 — ETW는 BlackBox 시작 이후 로드만 보므로,
  먼저 떠 있던 프로세스의 크래시는 스냅샷 폴백이 유일한 커버리지 (비용 분당 344ms라 유지 부담 없음)

관리자+ETW 재테스트 (CrashDummy 9회 + 유예 수정 후 a 3회 추가):

| 항목 | 결과 |
|---|---|
| 자동 감지 (이벤트 수신 + 자동 덤프) | **9/9** (지연: a 77~108ms, b 70~74ms, c 84~92ms, 덤프 완료 ~30.1s) |
| PID 파싱 (16진수 수정 후) | 9/9 정확 |
| 모듈 매칭 — b·c | 6/6 `etw-pid` |
| 모듈 매칭 — a (수명 90ms) | 유예 수정 전 0/3 → **수정 후 3/3 `etw-pid` (17개)** — 주기 스냅샷으론 원리적으로 불가능했던 케이스가 ETW로 확정적 확보 |

v0.2의 "a/b 모듈 확보는 확률적" 한계는 관리자+ETW에선 해소됨 (비관리자는 여전히 확률적 — 스냅샷 폴백).

실전 검증 덤: 테스트 도중 서드파티 앱 실제 크래시 1건이 자동 포착됨. 단 BlackBox 시작 전부터 떠 있던
비전경 프로세스라 모듈은 unavailable — "기동 전 장수 프로세스" 커버리지 공백의 실증. 또한 1001이
1000보다 20초 늦게 도착해 쿨다운 밖 crash-1001 단독 덤프가 생기는 기존 관찰이 재현됨.

## 구현 중 발견

### 수집기만으로 공존 상관을 통해 원인을 짚은 첫 사례 (2026-10-09, BlackBox v0.3.2)

경위: 10-08 유휴 실측 중 ImageLoad ~320건/초 버스트(21:37~21:39)가 있었으나, 당시 마킹이 없어
링버퍼가 휘발돼 **사후 분석이 불가능했다**. "마킹 없으면 증거도 없다"는 링버퍼 설계의 양면이 실증된 셈.

대응: 덤프 시 `snapshot.json`에 `etwImageLoadTopPids` 추가 — 덤프 구간의 ImageLoad를 PID별 집계(상위 10)하고,
같은 구간 processStart와 매칭해 시작 시각·부모 PID·명령줄을 붙인다. 로드 폭주의 유발자를 덤프만으로 짚는 용도.

재현 검증 (10-09 03:17, 수동 마킹): `dotnet build --no-incremental -nr:false` 루프(9프로젝트×5회전)로
ImageLoad 총 174건/초(필터 통과 110건/초)를 유발 → 수동 마킹 → 덤프 집계 결과 **창 내 5,659건/62 PID,
상위 10개 전원이 `dotnet build` 프로세스**(각 120로드, 전부 startedInWindow=true, 부모 PID 동일 = 루프를 돌린 셸).
"무엇이 이 부하를 만들었나"를 외부 도구 없이 수집기 덤프의 공존 상관만으로 특정했다.

함정 (실측): ImageLoad 이벤트의 프로세스 이름은 **PID 재사용 시 직전 프로세스 이름이 남을 수 있다** —
상위 1개 행의 이름이 몇 분 전 종료된 다른 앱으로 표기됐지만 명령줄은 `dotnet build`로 정확했다.
원인 판정은 이름이 아니라 processStart의 명령줄·부모 PID를 기준으로 할 것 (엔진 규칙으로 승격 필요).

## SensorSource GPU 전환 — BlackBox v0.4 (2026-10-09 오후)

**HW 시나리오 변경: "GPU 온도 상승 → GPU 클럭 하락 → 프레임 저하".** CPU/보드 센서는 현 보안 구성에서
미지원 확정(Defender의 WinRing0 차단 + PawnIO 미설치)이므로 과열·스로틀 신호는 GPU 기준으로 간다.
프레임 저하 계측(PresentMon 연동)은 **중간발표 뒤로 연기**.

- LHM 0.9.3 → **0.9.7-pre749** (System.Management 10.0.12 동반 상향 — NU1605 다운그레이드 충돌 때문)
- 수집 목록(2초 주기, 60초 검증 완료): 온도 5(NV Core/Hot Spot/Memory Junction + iGPU 2) · 클럭 5 · 전력 4 ·
  팬 RPM 2 · GPU Load 7 (**TDP% = Load "GPU Power"/"GPU Board Power" 포함**, D3D* 노이즈 제외)
- **0값 억제 규칙**: 온도/클럭/전력의 0값은 물리적으로 불가능 → 값으로 기록하지 않고
  `zeroAsUnavailableCount`로 집계 (검증: CPU 0값 20개 전부 억제, values 오염 0건).
  단 **Fan 0 RPM과 Load 0%는 실값으로 기록** — 세미패시브 팬 정지가 실제로 0 RPM으로 잡힘(검증됨)
- **스로틀 사유(NVML throttle reason) 직접 센서는 LHM 0.9.7에 없음** (전체 센서 덤프로 확인) —
  대체: TDP% 95%↑ + 클럭 하락 공존 = 전력 제한, Hot Spot 고온 + 클럭 하락 공존 = 온도 제한으로 추론.
  NVML 직접 호출(nvml.dll)은 PresentMon과 함께 중간발표 후 후보

## 오버헤드 30분 실측 (2026-10-09 12:01~12:31 run / 12:33~13:02 기준선, 관리자+ETW, CSV: collector/experiments/overhead.csv)

10분 시점 게임 실행 포함. 마킹 3회(12:06 게임 전, 12:16/12:26 게임 중).

| 항목 | 값 |
|---|---|
| BlackBox CPU (머신 전체, 16LP) | 평상시 평균 **0.13%** (0.09~0.21%), 마킹 분만 0.58~0.63% — **전 구간 1% 미만** |
| BlackBox CPU (1코어 환산) | 평상시 평균 2.0%, 마킹 분 9.4~10.1% |
| Working Set | 유휴 186MB → 게임 후 267MB 안정 (증가 추세 없음) |
| 게임 시작 시 ETW | ImageLoad 분당 ~180 → ~2,100건 점프 후 안정 |
| 버퍼 (6분 상한) | 게임 중에도 총 ~2.1MB 고정, ETW 몫 175~321KB — **상한 작동 확인** |
| 마킹 스냅샷 소요 | 게임 전 7.7s → 게임 중 7.1s / 7.0s — **게임 중에도 늘지 않음** (관리자라 접근 거부 204→19) |
| 시스템 전체 CPU | run 구간 평균 8.0% (게임 구간 ~11%), 기준선 평균 **4.7%** (오염 5샘플 제외*) |

\* 기준선 측정(12:33~13:02) 중 12:44~12:48에 별도 작업(SensorSource GPU 전환 빌드·검증)이 겹쳐 5샘플 제외.

**1% 판정**: 작업관리자 기준(머신 전체)으로는 마킹 포함 전 구간 1% 미만. 1코어 기준으로는 평상시 2.0%로 초과 —
귀속 분해(벤치 실측): **센서 LHM 2초 폴링 1.82%**(틱당 87ms, 지배 요인), 모듈 스냅샷 0.69%, 나머지(ETW·직렬화) ~0.3% 미만.
프롬프트 3에서 택한 **ForegroundPlusRecent 모듈 스냅샷 자체는 1코어 0.69%로 1% 안 — 최종 확인 완료.**

**조정안** (1코어 기준도 1% 안에 넣으려면):
1. 센서 간격 2초 → **5초**: 센서 몫 1.82%→~0.73%, 전체 평상시 ~0.9% 예상. 버퍼 6분에 72샘플 — 과열 추세 판단에 충분
2. 추가 후보: LHM Update를 GPU 하드웨어만 수행 (CPU/보드는 어차피 0값인데 Update 비용은 지불 중) — 틱당 87ms 단축 여지

## 저장소 통합 (2026-10-10~12)

- 작업 위치가 이 저장소(pc-crash-analyzer)로 이동. `E:\2026CAU\Capstone2\collector`는 이식 시점의 사본 — 이후 수정은 여기서만.
- `integrate/collector-engine` = collector v0.4 + 어댑터 + engine(2·3차 범위, 테스트 144/144) + ui(Python 시안+에이전트).
- 덤프마다 `engine-snapshot.json`(engine/docs/snapshot_schema.md v1.1) 생성 — `crash_engine diagnose`로 바로 진단.
  E2E 검증: CrashDummy 크래시 → 자동 덤프 → 진단 출력 (docs/run-pipeline.md). UI 시안 [최근 덤프 열기]와 경로 호환 확인.
- **최종 UI는 C# WPF — 중간발표(10/16) 후 우리 쪽에서 포팅 착수로 결정 (2026-10-12).** 발표 시연은 Python 시안.

## 다음 단계 후보

1. **(발표 후) C# WPF UI 포팅** — Python 시안(ui/prototype-python) 기능 동등 + 수집기 상주(트레이) 통합 방향
2. PresentMon 연동(프레임타임) + NVML 직접 호출(스로틀 사유) — 중간발표 뒤
3. 센서 간격 2초→5초 조정 또는 LHM GPU-only Update 적용 (위 오버헤드 절 조정안)
4. C를 보호 프로세스/타 세션 프로세스 대상으로 확장 테스트 (실패 양상 기록)
5. F의 InstallDate 없는 127건 보완책 검토 (키 LastWriteTime은 Win32 API 필요 — 미확인)

## 중간발표 이후 — 트리거 재분류·느려짐 스파이크 (2026-10-28, 관리자)

상세 계획은 `docs/post-midterm-plan.md`. 여기엔 실측만 기록한다.

### 30일 Application 로그 재분류 (이 PC)

| 구분 | 건수 | 비고 |
|---|---|---|
| 1000 앱 크래시 | 42 | CrashDummy 30 제외 시 실제 12건(8개 프로그램) — **전부 수집기 OFF 때 발생, 덤프 0** |
| 1001 WER 보고 | 79 | APPCRASH 38 · RADAR_PRE_LEAK_64 21 · CLR20r3 6 · crashpad_log 4 · StoreAgentScan 4 · LiveKernelEvent 2 · AppHangB1 2 · FTH 1 · BEX64 1 |
| 1002 응답 없음 | 6 | NVIDIA Overlay 4 · Hearthstone 1 · STOVE 1 — **현재 미구독** |

- 현 트리거(1000∨1001)는 30일 121회 중 테스트 61회 제외 60회, 그중 비크래시 30회(50%) → 1001은 유형별 분기 필수
- 누락: 1002(응답 없음), LiveKernelEvent(커널 라이브 덤프), System BugCheck 1001(BSOD) → 추가 예정
- 재설계 역산(추정): 121회 → 약 21회, 전부 사용자 체감 사건
- 2번째 PC(남건호, 비관리자): 감지 4ms·덤프 정상, **.NET 8 런타임 없으면 실행 실패** — 세팅 가이드 항목

### 느려짐 진단 신호 스파이크 (읽기 전용)

| 신호 | 상태 | 실측 |
|---|---|---|
| 프로세스별 CPU/WS/IO (PerfProc) | 검증됨 | 유휴 중 SignKoreaWD 22% · Goji 22% · SKBspmtmon 13% (금융보안 상주물) |
| 메모리 압박 (PerfOS_Memory) | 검증됨 | 가용 48GB, 커밋 72% |
| 디스크 부하 (PerfDisk _Total) | 검증됨 | busy 0% |
| 디스크 건강 (Get-PhysicalDisk) | 검증됨 | 4개 Healthy |
| SMART FailurePredict (WMI) | **미지원** | 관리자에서도 액세스 거부 (NVMe 추정) → HealthStatus 대체 |
| 시작 프로그램 (Run 키 + 로그온 예약 작업) | 검증됨 | Run 17 · 활성 예약 작업 32 |
| 백신 자원 (MsMpEng) | 검증됨(PerfProc) | `Get-Process` CPU는 보호 프로세스에서 0 → 신뢰 불가 |

함정: `Get-Process`의 CPU/TotalProcessorTime은 보호 프로세스(MsMpEng)에서 0을 돌려준다 — 자원 사용량은 PerfProc 카운터로만.

## 트리거 재설계·유의성 점수 구현 — BlackBox v0.5 (2026-10-28)

구현 (`docs/post-midterm-plan.md` 3절):
- `TriggerClassifier`(1단계, 순수 규칙): 1001을 WER 유형별로 분기 — RADAR_PRE_LEAK·StoreAgentScan·crashpad_log·FTH 등은 **트리거 안 함(버퍼만)**,
  LiveKernelEvent → `kernel-live`(90), AppHang → `hang-1001`, APPCRASH/CLR20r3/BEX → `crash-1001`. **1002 응답 없음 → `hang-1002`(freeze) 신규**,
  System BugCheck 1001 → `bsod-1001` 신규(이 PC에 사례 없어 **미확인**). 미분류 WER 유형은 보수적으로 트리거하되 기본점 20.
- 중복 제거: 같은 프로그램의 1000 뒤 180초 내 APPCRASH/CLR20r3 1001은 억제 (실측: 2.0초 뒤 도착한 APPCRASH 억제 확인), hang-1001↔1002 동일.
- `SignificanceScorer`(2단계): 기본점 + 사용자 대면 30 + 서드파티 오류 모듈 20(1000 [11] 경로 기준; 런타임 모듈명·`\dotnet\` 경로는 제외) + 7일 재발 15 +
  수명(>30s +15 / <5s −20) + 네이티브 예외 10 / 브레이크포인트 −10, 테스트 아티팩트는 ≤10. 등급 major≥60 / minor≥30 / hidden. **덤프는 남기고 UI 기본 숨김(10/28 결정)**.
- 이벤트 속성 추출 확정: 1000 [0]앱 [3]모듈 [6]예외코드 [8]PID(16진) [11]모듈 경로 [12]ReportId · 1001 [2]WER 유형 [5]P1 앱 [8]P4 모듈 · 1002 [0]앱 [2]PID(16진) [5]경로 [6]ReportId.
- 프로세스 스냅샷에 `win`(보이는 창 보유, EnumWindows 1회)·`fgPid` 추가 — 사용자 대면 판정용.
- 엔진 스냅샷: `significance` 필드 추가(엔진이 미지 필드를 무시함을 10/28 확인), `modules[].company/version/microsoft_signed`(FileVersionInfo) 추가 → 엔진의 Microsoft 추정 폴백 작동.
- 설정 `blackbox.config.json`: testArtifacts(기본 CrashDummy/FaultLab/FaultHost) · denylist.
- xUnit `collector/BlackBox.Tests` 29건 통과 (분류기·점수기).

FaultLab(`collector/experiments/FaultLab`): crash-a/b/c · plugin(네이티브 호스트) · plugin-inproc · hang · cpu · disk · leak. 네이티브 `plugin/`(CMake): FaultPlugin.dll + FaultHost.exe.

### 실측 (E2E, 비관리자 셸)

| 케이스 | 결과 |
|---|---|
| FaultLab plugin-inproc (.NET 프로세스 안에서 DLL 널 쓰기) | 1000 오류 모듈 = **coreclr.dll** — CLR이 접근 위반을 가로채 FailFast. s6 재현 **실패** → 네이티브 호스트 필요 |
| FaultLab plugin (FaultHost.exe → FaultPlugin.dll) | 1000 앱 FaultHost.exe, 오류 모듈 = **FaultPlugin.dll**(비시스템 경로), c0000005 — **s6 패턴 재현 성공**. 요소: thirdPartyModule 20 + nativeFault 10 |
| 1001 APPCRASH 후속 보고 | 1000 2.0초 뒤 도착 → "중복(WER 후속 보고)"로 억제 |
| 이벤트 수신 지연 | **네이티브 크래시 ~3.5초**(WER 보고 생성 후 1000 기록) vs .NET 크래시 ~90ms — 지연 지표는 크래시 종류별로 따로 보고해야 함 |
| 유의성 | 테스트 아티팩트라 10/hidden (설계대로) — 비테스트였다면 60(major) |

함정:
- PowerShell 5.1 `Get-Content`(인코딩 미지정)로 UTF-8 소스를 읽어 되쓰면 한글 주석이 깨진다 — 소스 편집은 Write/Edit 도구나 `-Encoding UTF8`로.
- 1002(응답 없음)는 사용자가 멈춘 창을 클릭·닫아야 기록되므로 자동 재현 불가 → FaultLab hang은 수동 단계 포함. 구독 자체는 쿼리 등록 성공으로만 확인(**미확인**: 실이벤트 수신).

## 설치 프로그램 인벤토리 + 충돌 검증 계획 (2026-10-28, 레지스트리 Uninstall 키·Win32_SystemDriver 읽기 전용)

`docs/conflict-test-plan.md`의 1절 매트릭스 근거. 이 PC에서 재현 가능한 셀을 정하기 위해 조사.

| 종류 | 설치됨 | 비고 |
|---|---|---|
| 영상·그래픽 편집 | Adobe Bridge 2023, Creative Cloud 데스크톱 | **Photoshop·Premiere 미설치** — Blender(무료)로 대체 검증 예정 |
| 코드 에디터·IDE | VS Code, VS 2022, Unity 2021.3/6000.4/6000.7, Android Studio, CUDA·Nsight | M3(Defender vs 빌드) 재현 가능 |
| 게임 런처·게임 | Steam, Epic, Battle.net, GOG, STOVE, Nexon Launcher, LOST ARK(+TEST) | M1 오버레이 재현 가능 |
| 오버레이·캡처 | OBS Studio, Discord, NVIDIA App | Afterburner/RTSS·RGB 소프트웨어 **미설치** |
| 보안·백신 | Defender(기본) | 서드파티 백신 없음 |
| 금융·인증·사내 보안 | AhnLab Safe Transaction, nProtect Online Security, TouchEn nxKey, Veraport, Delfino G3, IPinside ×2, AnySign4PC, SignKorea, CrossCert UniCRSV3, WIZVERA Process Manager (11종) | M3 실측 대상(10/28 유휴 CPU 13–22% 관찰분과 동일 계열) |
| 안티치트 드라이버 | **xhunter1/xhunter2**(Wellbia, Stopped), **ACE-BASE**(Tencent ACE, **Running**), ACE-ADVT(Stopped) | 유휴 상태에서 ACE-BASE가 실행 중 — 어느 게임이 올렸는지 **미확인** |

벤더 공식 "알려진 충돌" 출처(등급 A)로 확인된 것: OBS↔오버레이 후킹, JetBrains↔백신 실시간 검사, Adobe↔NVIDIA 드라이버 범위, Riot Vanguard↔요건 미달 드라이버.
**한국 금융보안 모듈↔게임 충돌은 공식·학술·보도 출처 없음** → "알려진 충돌"이 아닌 "우리가 측정한 자원 점유"로만 기술.

상태 분류: 검증됨(인벤토리 수집 자체) / 미확인(ACE-BASE 출처, Vanguard·EAC 재현, 1002·BugCheck 실수신) / 미지원(Photoshop·Premiere 재현 — 미설치).

## 느려짐 메커니즘 스파이크 (2026-10-28, 비관리자, 읽기 전용 5초 — `docs/slowdown-mechanisms.md`)

| 신호 | 결과 | 분류 |
|---|---|---|
| `\Memory\Committed Bytes`·`Commit Limit`·`% Committed Bytes In Use`·`Available MBytes`·`Pages/sec` | 전부 값 반환 | 검증됨 |
| `\Process(*)\Private Bytes`·`Handle Count` | 값 반환 (Unity 4,697 MB, NVIDIA Overlay 3,308 MB) | 검증됨 |
| `\GPU Adapter Memory(*)\Dedicated Usage` | 3 인스턴스, 주 어댑터 2,796 MB | 검증됨 |
| `\GPU Process Memory(*)\Dedicated Usage` | 한 PID가 8,646,276 MB — Microsoft가 인정한 카운터 오류(Win10 1709+) | **미지원**(어댑터 총량만 사용) |
| `\GPU Engine(*)\Utilization Percentage` | 값 반환 | 검증됨 |
| System Kernel-Processor-Power **Event 37**(CPU 속도 제한) | 구독 가능, 30일 0건 | 검증됨(수신은 미확인) |
| System 4101 | 30일 0건; TdrDelay/TdrDdiDelay/TdrLevel 레지스트리 값 없음 = 기본 2/5/3 | 실수신 미확인 |
| LHM GPU Memory·스로틀 사유 센서 | 스파이크 E 미빌드 | 미확인 |
| 앱 자체 크래시 로그 | Unity `%TMP%\Unity\Editor\Crashes\Crash_2026-10-10_072938424` (crash.dmp 1.9 MB + Editor.log) ↔ Application 1000 10/10 16:29 Unity.exe 6000.4.9 / 오류 모듈 Unity.dll **1:1 대응 확인**. Blender crash.txt 0, WER ReportArchive 0 | 검증됨 |

발견: 점수기가 `\Windows\System32\DriverStore\` 아래 GPU 드라이버 DLL을 시스템 모듈로 취급해 가산이 없음 → `gpu-driver-module` 범주 신설 필요(S5).

## v0.6 — 느려짐 추세 수집 (2026-10-29, 비관리자)

결정(10/29): 목표를 "사용자 친화 컴퓨터 관리 프로그램"으로 올리되 **수집기는 읽기 전용 유지**, 조치는 사용자가 고른 수준에서 별도 실행기(CLAUDE.md 원칙 3 개정). 우선순위는 데이터 품질.

### 스파이크 I — .NET PerformanceCounter (collector/spikes/I-PerfCounters, 5초, ko-KR)

| 항목 | 결과 | 분류 |
|---|---|---|
| 영어 카운터 이름(`Memory`, `Process`, `GPU Adapter Memory`)이 한국어 Windows에서 동작 | 전부 true | 검증됨 |
| `Memory\Committed Bytes` 등 5종 | 값 반환 (커밋 74.3%, 가용 54,486MB) | 검증됨 |
| `GPU Adapter Memory(*)\Dedicated/Shared Usage` | 3 인스턴스, 주 어댑터 3,186/482MB | 검증됨 |
| `Process` 카테고리 `ReadCategory()` 1회 | 553 인스턴스 **~260ms** — CPU%·Private Bytes·Handle Count·ID Process·IO를 접근 거부 없이 한 번에 | 검증됨 → 10초마다는 비싸서 **30초마다** |
| `Process.GetProcesses()` 전수에서 `TotalProcessorTime` | 373개 중 180개 접근 거부(비관리자) | 미지원(비관리자) → PerfCounter 경로 사용 |

### 구현

- `Sources/ResourceSource.cs`(10초): 커밋/가용/페이징, GPU 어댑터 메모리(프로세스별 카운터는 MS 인정 오류라 미사용), 전체 CPU, 프로세스 CPU 상위 10 + IO 상위 3(30초마다).
- `Core/MinuteRollup.cs`: 24시간 분 단위 롤업(메모리에만). 분당 1줄 = 시스템 수치 + 프로세스 ≤22행(Private Bytes 상위 12 + 전경 + CPU 상위 10). 덤프 시 `snapshot.json.rollup24h`(전체) + `engine-snapshot.json.resource_trends`(60분 기울기 요약: 초점 프로세스 기울기/R²·커밋 기울기·GPU 온도 최고/클럭 최저·메모리 증가 상위·CPU 상위).
- `Core/Trend.cs`: 최소제곱 기울기·R². `SensorSource.LatestGpu`, `ProcessListSource.LatestSummary`(privMB·handles 추가), `StateSnapshotter.fgPidAtMark`(수동 마킹의 초점).
- `SignificanceScorer`: GPU 드라이버 DLL(nvwgf2umx 등 NVIDIA/AMD/Intel 목록) → `gpu-driver-module`(+20), `\DriverStore\` 경로 → `driver-store-module`(+20). Event 37(Kernel-Processor-Power) 구독 — 버퍼만.
- xUnit 42건 통과 (Trend 4, MinuteRollup 5, 점수기 +3, 분류기 +1).
- 엔진: `Symptom::Slow`("slow") 추가, `resource_trends` 파싱, `addFromResourceTrends`(A 메모리 증가 → 그 프로그램 / C GPU 열 → thermal / E 배경 CPU ≥10% → 상주물 / 이벤트 37 → power), rules.json에 slow priors·사례·한국 금융보안 힌트 4종(signkorea·anysign·wizvera·crosscert), 샘플 `s12_slow_app_memory.json`.

### 실측 (라이브 3분, 비관리자)

| 항목 | 값 |
|---|---|
| 소스 상태 | eventlog·sensors·processes·**resources** ok, etw-kernel unavailable(비관리자), hotkey ok |
| 버퍼 2분 20초 시점 | 94건 1,702KB — processes 1,418KB(14건), sensors 298KB(67건), **resources 27KB(13건, 건당 ~2KB)** |
| 롤업 | 2분 2.4KB → 24시간 추정 ~1.7MB(테스트 상한 2MB 확인) |
| 덤프(수동) | engine-snapshot 19KB · snapshot 322KB · events 1,059KB. `resource_trends` 생성됨(2분치라 기울기는 null, 초점 null — 10샘플 미만 설계대로), `fgPidAtMark`=전경 게임 PID 일치 |
| resources 샘플 | scanMs 274(ReadCategory 틱), 커밋 75.2%, GPU 전용 3,213MB, 전체 CPU 27.8%, CPU 상위에 BlackBox 자신 1.3% 포함(측정 교차 확인용) |

함정: 덤프 JSON은 UTF-8 **BOM** 포함(`File.WriteAllText(…, Encoding.UTF8)`) — Python은 `utf-8-sig`로 읽어야 함. 엔진은 문제없이 읽음(기존 E2E).
병렬 셸 호출 시 `cd`가 서로 간섭함 — 스크립트는 절대 경로로.

### 오버헤드 재측정 (v0.6, 10분, 비관리자, Get-Counter 10초 × 57 샘플, 16 논리 CPU 기준 머신 %)

| 항목 | v0.5 이전(10/09, 30분, 관리자+ETW) | **v0.6(10/29, 10분, 비관리자)** |
|---|---|---|
| BlackBox 프로세스 CPU 평균 | 0.13% | **0.31%** |
| CPU 최대(10초 샘플) | — | 4.25% (기동 직후 카운터 초기화·10분 시점 수동 덤프 포함) |
| Private Bytes 평균/최대 | — | 94.3 / 112.1 MB |

증가분 추정: Process 카테고리 ReadCategory 260ms/30초 ≈ 0.05%p, 메모리·GPU 카운터 5+6개 NextValue 10초 ≈ 미미, 롤업 1분 ≈ 미미.
나머지 차이는 측정 조건(엔진 C++ 빌드가 같은 구간에 돌아 전체 부하↑, 10분 vs 30분, 기동 구간 비중)으로 봄 — **30분 조용한 조건에서 재측정 필요**(미확인). 1% 상한은 여유 있음.

### 코드 리뷰(자체, 10/29) 반영

롤업이 60분 이상 비어 있으면 덤프 전체가 예외로 사라지던 결함(절전 복귀 직후 크래시) → unavailable 로. 멈춘 소스의 동일 샘플 반복 → 빈 칸. 프로세스 시계열 (pid, 이름) 키. 초점 PID 우선. CPU 평균은 상위 밖 분을 0으로.
Process 카운터 인스턴스 재배정(chrome#3) 시 PID 불일치면 차분 생략. GPU 카운터 실패 시 부분합 대신 NaN. 전경 PID P/Invoke 중복 제거.
**인라인 패치의 백슬래시 깨짐**으로 점수기 테스트의 DriverStore 경로 문자열에 줄바꿈이 들어가 있었음(테스트는 우연히 통과) → 파일 재작성 + 회귀 assert.

엔진 발견(미해결, 노이즈): 실덤프 진단에서 Chrome·Edge·WebView2·Copilot 의 **자동 업데이트가 "증상 직전 설치"로 잡혀 1~3순위를 차지**함. 브라우저류 자동 업데이트는 시간 상관 가중을 낮춰야 함 — 엔진 TODO.

### 배포 빌드 (10/29)

`tools/publish-collector.ps1` → `dist/BlackBox-0.6.0-win-x64.zip` 35.9MB, 213 파일(self-contained win-x64). 배포본 단독 기동 20초 확인: 소스 상태 동일(etw-kernel 은 비관리자 unavailable).
함정: PowerShell 5.1 은 BOM 없는 UTF-8 스크립트의 한글을 깨뜨림 → ps1 에 BOM 필요. 참여자 안내문 `docs/participant-guide.md`.
30분 조용한 조건 오버헤드는 배포본으로 측정 중(결과는 아래에 추가).

### 오버헤드 30분 조용한 조건 (10/29 00:51~01:21, 배포본 v0.6.0, 비관리자, 빌드 없음, Get-Counter 10초 × 173)

| 항목 | 값 |
|---|---|
| BlackBox CPU 평균(머신, 16 논리 CPU) | **0.255%** (기동 2분 제외 0.234%) |
| CPU 최대 | 3.98% — 기동 직후 1샘플, 1% 초과는 173 중 1건 |
| Private Bytes | 5분 평균 83 → 115 → 115 → 113 → 114 → 114 MB. 링버퍼(6분)가 차면 **평탄**, 마지막 15분 기울기 0.25MB/분(장기 확인은 지인 PC 2주에서) |
| 버퍼 | 252건 4.3MB — processes 3.55MB(건당 ~99KB, privMB·handles 추가 후에도 동일 수준), sensors 0.8MB, resources 87KB |
| 롤업 | 29분 24.3KB → 24시간 ≈ 1.2MB |
| 핸들 | 최대 596 |

v0.5 이전 0.13%(관리자+ETW, 30분) → v0.6 0.26%(비관리자). 증가분 ≈ Process 카테고리 30초 읽기(0.05%p) + 메모리·GPU 카운터 + 롤업. 1% 상한 대비 여유. 관리자+ETW 조건의 v0.6 수치는 **미측정**.

### S6 앱 자체 크래시 로그 경로 (10/29 구현)

`snapshot.json.appCrashLogs`: Blender `%TEMP%\*.crash.txt`, Unity `%TMP%\Unity\Editor\Crashes`, WER ReportArchive/ReportQueue, Maya `%TEMP%\*.dmp` — 최근 30일, 폴더당 10건, **경로·크기·시각만**(내용 안 읽음, `%USERPROFILE%` 치환 확인). 라이브 확인: unity 1건(10/10 crash.dmp 폴더) 기록, 나머지 0/absent. 스냅샷 캡처 7.4초(fltmc·WMI 포함, 이전과 비교 미측정).


## v0.6.1 — 미확인 항목 로컬 검증 (2026-10-29, 비관리자)

### 스파이크 E 재검증 (LHM 0.9.7-pre749)

| 센서 | 결과 | 분류 |
|---|---|---|
| NVIDIA `GPU Memory Total/Used/Free` | Data 타입, 값은 **바이트**(Total 17,171,480,576) — 문서상 Data=GB 와 다름 | 검증됨 → MB 정규화 |
| `D3D Dedicated/Shared Memory Used` | 값 있음 | 검증됨(미사용) |
| 스로틀 사유(NVML throttle reason) | 센서 없음 | **미지원 확정** |
| `SensorType.SmallData` | 이 버전에 형식 자체가 없음(컴파일 오류로 확인) | — |

**결함 발견·수정**: 하드웨어 열거 순서가 `GpuAmd`(CPU 내장 Radeon) → `GpuNvidia` 라 롤업 GPU 온도·클럭이 **내장 그래픽 값**(클럭 600MHz)이었다. 주 GPU = 메모리 총량 최대 GPU 로 바꾸고, 엔진 스냅샷도 `primaryGpu` 를 써서 AMD 외장 GPU PC 에서도 동작하게 했다.

### 과거 이벤트 로그 재검증 (`--verify-eventlog`, 로그 전체 2025-12-04~)

블루스크린·전원 차단·TDR 을 일부러 일으키지 않고, 이 PC 에 남은 실제 기록을 실시간 구독과 같은 파서·분류기로 다시 읽었다.

| 종류 | 건수 | 분류 |
|---|---|---|
| crash-1000 | 142 (수정 전 144) | 검증됨 |
| crash-1001 / hang-1001 | 248 / 8 | 검증됨 |
| **hang-1002** | 19 | **검증됨(실기록 해석)** |
| **display-4101** | 7 | **검증됨(실기록 해석)** — 공급자 Display, 메시지 "nvlddmkm 응답 중지 후 복구" |
| **kernel-live** | 16 — 코드 0x141 12건, 0x144 4건 | 검증됨 |
| **power-41** | 118 — BugcheckCode 전부 0 | 검증됨 |
| bsod-1001 (BugCheck) | 0 | 미확인(이 PC 에 기록 없음) |
| WHEA | 0 | 미확인(기록 없음) |
| Event 37 | 0 | 미확인(기록 없음) |

발견과 수정:
- **ID 1000 을 다른 공급자도 쓴다**: ".NET Runtime" 1000 2건(ASP.NET Kestrel 경고)이 앱 크래시로 잡혀 있었다 → 구독·분류를 공급자 `Application Error`/`Windows Error Reporting`/`Application Hang` 로 제한. 재검증 후 해석 누락 0건.
- **Kernel-Power 41 118건의 속성**: BugcheckCode 0 이 118건, 그중 SleepInProgress=6 110건·4 1건, 0/0/0 7건, 전원 버튼 0건. 전부 기본점 90 이면 RADAR_PRE_LEAK 같은 노이즈가 된다 → BugcheckCode≠0 `bsod-41`(90) / 전원 버튼 60 / 절전 전환 중 40 / 0·0·0 90 으로 세분. SleepInProgress 값의 의미는 Microsoft 문서에서 확인 못 함(**미확인**), 필드명만 근거.
- **41 은 실시간으로 받을 수 없다**: 부팅 초기에 기록돼 로그온 뒤 켜지는 수집기의 EventLogWatcher 는 못 본다 → 시작 시 "이번 부팅 이후" System 41·BugCheck·WHEA 를 한 번 재생, 기존 덤프 meta.json 의 RecordId 로 중복 방지. 라이브 확인: 10/05 부팅의 41 1건 재생·덤프, 같은 폴더 재실행 시 0건.
- **LiveKernelEvent 의 P1 은 프로세스가 아니라 라이브 덤프 코드**: 0x141 VIDEO_ENGINE_TIMEOUT_DETECTED(12건, 4101 과 같은 시각), 0x144 BUGCODE_USB3_DRIVER(4건) — Microsoft Learn 라이브 덤프 코드 문서. 0x141/0x117 → `flicker`(TDR 계열), 0x144 → USB3. 엔진은 이제 LiveKernelEvent 를 앱 크래시로 세지 않고 0x141/0x117 을 디스플레이 신호로 본다.
- 시스템 범위 사건의 엔진 `target_process` 에 "BugcheckCode 0" 이 들어가던 결함 수정.

### 1002 실수신 (FaultLab hang-report)

`FaultLab hang-report 60 6`: UI 스레드를 멈춘 뒤 같은 프로세스의 배경 스레드가 자기 창에 `WerReportHang`(faultrep.dll, Microsoft Learn 문서화 API) 호출 → 반환 0x10000000, FaultLab 즉시 종료, **Application Hang 1002 기록(02:02:04) → 수집기 수신 0.5초 뒤, PID 346764 해석, hang-1002 덤프**(테스트 아티팩트라 hidden). 사람 조작 없이 반복 가능. Microsoft 는 이 API 를 최상위 창에 쓰지 말라고 권하지만(자식 창용), 대상이 우리 테스트 프로그램 자신뿐이라 시험 도구로만 쓴다. → **1002 검증됨**.

### 마킹 순간 비용 (중간발표 14쪽 "1코어 ~10%" 원인)

항목별 캡처 시간(수동 마킹 2회): 전체 프로세스 모듈 열거 **7,436 / 7,306ms**, 필터 드라이버 230ms, 설치 이력 17ms, 보안 제품 1ms, 앱 크래시 로그 2ms → 원인은 모듈 열거.
스파이크 J(`collector/spikes/J-ModuleSweep`, 546 프로세스, 3회): .NET `Process.Modules` **6.1초** vs Toolhelp32 **0.33초**(18배). 열거되는 프로세스·모듈 집합 동일, 차이는 Toolhelp32 가 WOW64 프로세스의 실행 파일을 한 번 더 내는 중복뿐(중복 제거).
적용 후: 스냅샷 전체 **7.6초 → 0.64초**(모듈 열거 0.39초), 10초 주기 모듈 스냅샷 scanMs 50~60ms.
