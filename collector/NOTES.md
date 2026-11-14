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

### 실제 작업 프로그램 실험 1 — Blender 메모리 누적 (A 메커니즘, 10/29 02:16~02:35)

- 설치: Blender 4.5.15 LTS 공식 zip(download.blender.org, 398,649,826 바이트), 공식 `.sha256` 과 SHA-256 **일치** 확인 후 스크래치 폴더에만 압축 해제(레지스트리·시작 메뉴 변경 없음). 사용자 설정은 `BLENDER_USER_RESOURCES` 로 스크래치에 격리.
- 부하: `blender --background --factory-startup --python blender_mem_growth.py -- 17 20` — 20초마다 UV 구(128×64) + Subdivision 1단계 적용 메시를 씬에 추가(저장 안 함).
- 수집기: 17분 뒤 `m slow blender`.

| 항목 | 값 |
|---|---|
| 롤업 초점 | blender, 17샘플, **분당 11.35MB, R² 0.93**(280.6 → 474.9MB), 핸들 분당 1.93 |
| 비교 | chrome 분당 6.11MB 이지만 R² 0.11(들쭉날쭉) — 기울기만 보면 오판, R² 로 걸러짐 |
| 주 GPU | NVIDIA GeForce RTX 4070 Ti SUPER, 클럭 2685MHz, GPU 메모리 최대 19.4%(3,177/16,376MB) — 주 GPU 결함 수정 확인 |
| 엔진 1차 | 1순위 blender(앱 메모리 누적) 12%, 그러나 AhnLab Safe Transaction 커널 드라이버 5개가 각각 9% 후보, 첫 점검이 "클린 부트(65%)" |
| 엔진 수정 후 | 1순위 blender **19%**, AhnLab 은 후보 1개(14%), **첫 점검 "저장 후 프로그램 재시작"** |

발견·수정:
- **작게 시작한 누수는 롤업에 안 담겼다**: 롤업은 Private Bytes 상위 12 만 담아서 처음 280MB 인 Blender 는 빠질 뻔함 → 60초간 증가 상위 8 을 추가하고 한 번 고른 프로세스는 살아 있는 동안 유지(최대 12).
- 엔진: 같은 제품("제품 (구성요소)" 표시명)의 드라이버·DLL 은 후보 하나로 병합. 클린 부트 점검은 서드파티 상주 범주만 덮고 대상 프로그램 자체(`app_memory`)는 덮지 않게.
- 대상 지정 마킹(`m slow blender`)이 없으면 초점이 전경(콘솔)이 되어 실패했을 것 — UI 증상 대화상자에 "대상 프로그램" 선택이 꼭 필요(송민석 인계).

### WMI 공급자 호스트 부하 A/B (10/29 02:37~02:42)

롤업 CPU 상위에 `wmiprvse` 가 평균 2.5% 로 상주 — 수집기 탓이면 오버헤드 과소평가. 150초씩 비교:

| 조건 | wmiprvse 머신 CPU 평균 / 최대 |
|---|---|
| 수집기 없음 | 2.587% / 4.50% (n=24) |
| 수집기 실행 | 2.489% / 3.90% (n=20) |

→ **수집기와 무관**. 이 PC 의 다른 상주 프로그램이 WMI 를 계속 조회 중(누군지는 미확인 — WMI-Activity 로그로 확인 가능, 다음 후보). 느려짐 E 메커니즘의 실관찰 사례.

### 오버헤드 측정 스크립트

`tools/measure-overhead.ps1` 비관리자 2분 검증: 샘플 12, CPU 평균 0.204%, Private 84.9MB, 소스 상태 출력 정상. **관리자+ETW 조건 측정은 사용자가 관리자 터미널에서 실행 필요**(권한 상승은 자동으로 하지 않음).

### 이번에 하지 않은 것과 이유

| 후보 | 하지 않은 이유 |
|---|---|
| BugCheck 1001·WHEA·Event 37 실수신 | 블루스크린·하드웨어 오류·펌웨어 스로틀을 일부러 일으켜야 함. 이 PC 기록도 0건이라 재검증도 불가 → **미확인 유지** |
| TDR(4101) 강제 발생 | 디스플레이 드라이버를 강제 리셋해야 함(사용 중인 앱이 죽을 수 있음). 대신 실기록 7건 재검증으로 해석 확인 |
| MSI Afterburner/RivaTuner 설치 | 커널 드라이버 RTCore64.sys 가 CVE-2019-16098 로 Microsoft 취약 드라이버 차단 목록에 올라 있다는 보고, 이 PC 는 HVCI 꺼짐 → 보안상 설치 안 함 |
| JetBrains Rider / Adobe 체험판 | 계정 로그인·결제 정보 필요 — 사용자가 직접 |
| OBS 게임 캡처 + Discord 오버레이 | 사용자 OBS 프로필에 장면을 만들고 실제 게임을 띄워야 함 — `docs/conflict-test-plan.md` V1 절차로 사용자 실행 |
| Defender 제외 A/B 의 "제외" 쪽 | 보안 설정 변경 — 사용자가 직접(제외 없는 쪽만 자동 측정) |

### 실제 작업 프로그램 실험 2 — 빌드 반복 + Defender 실시간 검사 그대로 (E 메커니즘, 10/29 02:44~02:58)

Defender 설정은 바꾸지 않았다(제외 A/B 의 "제외" 쪽은 사용자 몫). 13분 동안 `dotnet build --no-incremental` 을 작은 프로젝트 2개에 반복(293회, 회당 2~4초), 끝에 `m slow dotnet`.

| 항목 | 값 |
|---|---|
| 기계 전체 CPU(분 평균) | 20~24% |
| MsMpEng(Defender) | 분당 **1.0~1.2%**(기계 기준, 16 논리 CPU → 약 0.18코어), 14분 내내 CPU 상위 10 안 |
| 그 밖의 배경 | wmiprvse 3.0%, esrv_svc 1.5% |
| 엔진 1차 | 근거 없음에도 오늘 자동 업데이트된 Microsoft GameInput·Xbox Service 가 1·2순위 |
| 엔진 수정 후 | Store 자동 업데이트를 노이즈 목록에 추가, **"느려짐 근거 부족" 경고** 표시 — 60분 추세에 뚜렷한 원인이 없으면 후보는 약한 추정임을 명시 |

결론: 이 PC(16스레드, NVMe)에서는 작은 프로젝트 빌드에 대한 Defender 비용이 엔진의 배경 과부하 기준(10%)에 한참 못 미친다 — **음성 결과도 데이터**. JetBrains 공식 문서의 "백신이 빌드를 늦춘다"는 큰 솔루션·느린 디스크 조건에서 다시 볼 것(지인 PC).

### WMI 과다 조회 클라이언트 (신호 추가, 10/29)

스파이크: `Microsoft-Windows-WMI-Activity/Operational` 5858(클라이언트 조회 실패)은 `UserData/Operation_ClientFailure` 아래 `ClientProcessId`·`Operation` 요소로 온다(XML 확인, `User` 필드는 기록하지 않음).
구현: `snapshot.json.wmiClients24h` — 최근 24시간 5858 클라이언트 상위 5(PID·프로세스 이름·건수·조회문 일부). 캡처 0.19초(690건).

| 클라이언트 | 24시간 실패 | 조회 |
|---|---|---|
| **SKBSpeedMeter** | **545** | `ROOT\CIMV2 : select * from Win32_PerfRawData_Tcpip_NetworkInterface` |
| (종료된 프로세스) | 19 | `Win32_ServerFeature` |
| svchost 계열 | 16 | TPM 제조사 |
| nvcontainer | 12 | `Win32_Processor` |

SKB(통신사 속도 측정) 계열은 10/28 유휴 CPU 13~22% 관찰분과 같은 제품군 — wmiprvse 상시 2.5% 의 **유력한 원인(인과 미확인)**. 엔진 반영(배경 WMI 과다 조회 → 해당 상주물 근거)은 다음 단계.

### 단위 테스트 (v0.6.1 종료 시점)

수집기 xUnit 61 · 엔진 ce_tests 161 — 전부 통과.


## v0.7 — 참여자판(지인 배포) + 암호화 자동 전송 (2026-10-29)

요청: 일반인·지인에게 보낼 빌드 — (1) 완성본이 아니어도 블랙박스 감지·문제 상황 인식, (2) 참여자가 파일을 보내지 않아도 팀이 받음, (3) 보안·문제 없음.
운영 절차·보안 모델·검증 표는 `docs/study-operations.md`, 참여자 안내는 `docs/participant-guide.md`.

### 수집기 쪽 변경 (collector/)

- `Hosting/CollectorHost` — 소스·링버퍼·롤업·마킹 조립을 콘솔판에서 떼어 냄. 콘솔판(`Program.cs`)과 참여자판이 같은 조립을 쓴다. 콘솔판 회귀 실행: 부팅 재생 41 1건 + `m slow explorer` 덤프 정상, 종료 정상.
- `MarkService.DumpWritten` 이벤트(덤프 완료 알림 — 수집기 자신은 업로드하지 않는다), `MarkManualDeferred`(마킹 시각·전경 PID 는 지금 고정, 증상·대상·메모는 대화상자 답을 덤프 직전까지 대기. 버퍼 구간은 T+post 에 먼저 잘라 둔다), `NormalizeManual`(대상 .exe, 알려진 증상만, 메모 300자·제어 문자 제거).
- `ManualMark.Memo`, `snapshot.json.userReport`, `meta.json.edition`·`version` 0.7.
- `StateSnapshotter.Capture(fgPidHint)` — 단축키 순간의 전경 PID(질문 창이 초점을 가져가기 전).
- `Sanitizer` — 프로필 경로에 더해 **사용자 이름 → %USERNAME%, PC 이름 → %COMPUTERNAME%** (JSON 문자열 값에서만, 낱말 경계, 3자 미만·흔한 낱말 제외). 콘솔판에도 적용된다.
- `EtwKernelSource(stripCommandLineArgs)` + `CommandLineText.ExecutableOnly` — 참여자판은 프로세스 시작 명령줄의 인자를 지운다(토큰·주소·문서 이름 방지).

**자체 점검에서 잡은 결함**: `Sanitizer` 정적 필드 순서(흔한 낱말 집합이 정규식보다 뒤) → 형식 초기화 예외로 **모든 덤프가 실패**. 단위 시험이 없던 경로라 종단 시험에서야 드러남 → 순서 수정 + `SanitizerTests`(정적 초기화 경로 포함) 추가.

### 참여자판 (participant/)

- `PcBlackBox.exe` — WinForms 트레이 앱, 비관리자(asInvoker), 커널 드라이버 없음(LHM 0.9.7 은 WinRing0 미포함·PawnIO 는 별도 설치 시에만 — 바이너리 검사로 확인).
- 처음 실행: 동의 화면(체크 필수, 거절 시 저장 없음) · 자동 실행 선택(시작 프로그램 폴더 바로가기 1개, 레지스트리 없음) · 별명(선택).
- 자동 감지(기존 이벤트 로그 트리거) → 덤프 → 알림(유의성 숨김 등급은 조용히, 3분 간격) → 알림을 누르면 메모.
- `Ctrl+Shift+F12` / 트레이 "지금 문제 기록하기" → 증상 10개·문제 프로그램(창 있는 프로그램 목록, 전경 미리 선택)·메모. 120초 뒤 자동으로 닫힘. **전체 화면 게임 중에는 창을 띄우지 않는다**.
- 전송: zip → AES-256-GCM(무작위 키) + RSA-3072-OAEP-SHA256(팀 공개키), 머리는 AAD → 디스크 대기열(재시도 1분~6시간, 시간당 30건) → HTTPS POST(Apps Script 302 따라감). 하트비트 8시간(상태·자기 CPU/메모리·운영 로그 80줄, 치환 후).
- 참여 그만두기: 삭제 요청 봉투 + 자동 실행 제거 + (선택) 로컬 삭제. 전송 일시중지. 최근 기록 목록(전송 상태 표시, 나중에 메모).
- 로컬: `%LOCALAPPDATA%\PcCrashAnalyzer` — 전송 끝난 30일 지난 기록 정리, 300건·300MB(소프트)/600MB(하드) 상한.
- 운영 로그는 메모리에만(300줄). zip 안에서 바로 실행·중복 실행 막음.

### 검증 (4분류)

| 신호/기능 | 상태 | 근거 |
|---|---|---|
| 암호 봉투(머리 조작·다른 키·알 수 없는 kind 거부) | 검증됨 | StudyCore.Tests 32/32 (검토 반영 뒤 48/48) |
| 수집기 단위 시험(치환·명령줄·증상 정규화 추가) | 검증됨 | BlackBox.Tests 77/77 (+16, 검토 반영 뒤 84/84) |
| 자체 점검(실제 수집기 → 덤프 → 전송 → 메모 → 하트비트) | 검증됨 | 시험 수신기(302 흉내), 복호화본 4개 파일이 로컬 덤프와 바이트 일치 |
| 트레이 앱 전체 흐름 | 검증됨 | `--test-consent --test-mark-after 15 --test-auto-answer --test-exit-after 100`: 부팅 재생 power-41 + 수동(대상 Code.exe, slow, 메모) 2건 전송, 하트비트, 정상 종료 |
| 자동 실행 바로가기 | 검증됨 | 임시 시작 폴더, 대상·인자 `--autostart` 확인 |
| 사용자·PC 이름 누출 | 검증됨(이 PC) | 덤프·봉투·복호화본 0건 |
| Apps Script 수신 규칙 | 검증됨(Node 흉내) | `participant/receiver/test-receiver.js` 18/18 |
| 엔진 호환 | 검증됨 | 복호화한 참여자판 engine-snapshot 3건 `crash_engine diagnose` 정상 |
| 게시본·탐색기 압축 풀기(한글 이름) | 검증됨(이 PC) | 65.3MB·278파일, 게시본 자체 점검 OK |
| 실제 Apps Script·Drive 저장·메일 | 미확인 | 팀 Google 계정으로 배포 후 `--selftest` |
| 다른 PC 의 SmartScreen·백신 반응 | 미확인 | 시범 배포 1~2명 |
| ETW 경로의 명령줄 인자 삭제 | 권한 필요 | 관리자 실행에서만 |

### 독립 검토 반영 (10/29, 같은 날)

참여자판 코드를 결론 없이 넘겨 별도 검토를 받았다. 12건 중 11건을 코드로 확인하고 고쳤다(1건 — 관리자 실행 시 ETW 세션 잔류 — 은 참여자판 기본이 비관리자라 미확인으로 둠).

| 지적 | 고친 것 |
|---|---|
| 로그오프를 취소하면 아이콘 없이 계속 수집·전송(끌 방법 없음) | SessionEnding → **SessionEnded**(확정된 종료)에서만 정리 |
| 대기열·failed 무한 증가(크래시 반복·일시중지·수신기 중단) | 대기열 100MB·500건·30일 상한, failed 200개·30일, 영수증 90일, 끊긴 `.tmp` 정리. 로컬 기록 200MB(하드 400MB). **같은 대상 자동 덤프 시간당 6건**(MarkService — 콘솔판에도 적용) |
| 복호화 도구가 `..` 이름으로 출력 폴더 밖에 씀 | 이름은 앱 형식 3가지만(점 없음), 쓸 경로가 참여자 폴더 안인지 확인, zip 항목 허용 목록 |
| 배포 zip 의 공개키·토큰으로 누구나 참여자인 척 가능 | **참여자별 서명 키(ECDSA P-256)** — 동의 때 생성, 모든 봉투에 서명, 팀 도구가 참여자별 첫 키 고정(TOFU)·충돌 경고 |
| 철회 요청 뒤에도 업로드가 이어질 수 있음 | 정지 → 요청 → 삭제 순서, 정리 중 끝난 덤프는 전송 안 함, `selftest` 폴더도 삭제 |
| 수신기 상한이 사실상 누적(매 요청마다 만료 연장), Drive 를 채울 수 있음, 오류 상세 노출 | 6시간 **고정 구간** 키, 바이트 상한(참여자 50MB·전체 300MB), 저장 공간 80% 초과 거부, 오류 상세는 실행 기록에만 |
| 따옴표 없는 명령줄이 ".exe" 까지 통째로 남음(`curl -H "Authorization…" -o setup.exe`) | 드라이브 경로이고 옵션이 없을 때만 ".exe" 까지, 아니면 첫 낱말 |
| 이름 치환 빈틈: 두 글자 이름, JSON 실패 시 건너뜀, 8.3 짧은 경로, `C:\Users\kim` 이 `kimberly` 를 망침 | 짧은·흔한 이름은 경로 조각·도메인\이름에서 치환, 실패 시 원문 전체 치환, 짧은 경로 추가, 프로필 뒤 이름 글자가 이어지면 치환 안 함 |
| 전체 화면에서 단축키 연타 → 덤프 반복 | 수동 기록 30초 안 반복 무시 |
| 시험 옵션의 동의가 실제 데이터 폴더에 남을 수 있음, `--data-dir` 오입력 시 삭제 위험, 헤드리스 점검이 조용히 0 반환 | 동의를 연구 ID·받는 곳에 묶음, 시험 설정은 별도 `--data-dir` 필수, 삭제는 participant.json 이 있는 폴더에서만, 중복 실행 시 메시지와 코드 3 |
| 요약 도구가 손상된 파일 하나에 멈춤, 별명이 표를 깨뜨림 | 참여자별 예외 처리, 칸 문자 치환 |
| 안티치트 보호 프로세스에 10초마다 접근 시도 | 모듈 열거 거부된 PID 는 살아 있는 동안 다시 열지 않음(ProcessListSource — 콘솔판에도 적용) |

검토 뒤 다시 확인: StudyCore.Tests 48/48, BlackBox.Tests 84/84, `test-receiver.js` 30/30, 서명 봉투 자체 점검·트레이 흐름·철회 순서·위장 봉투 3건 종단 시험(`docs/study-operations.md` 6절). 하트비트 첫 전송이 다음 2분 깨어남까지 밀리던 문제도 이때 고침.

참여자판 유휴 오버헤드(Release, 8분, 이 PC 16스레드): 기계 CPU **0.204%**(1코어 3.3%), Private **121MB** 일정 — 동의 화면의 메모리 수치를 100MB → 120MB 로 고침.

### 이 PC 실사용 시험 (10/29 저녁, LOCALTEST 빌드 + 127.0.0.1 시험 수신기)

| 관찰 | 분류 | 내용 |
|---|---|---|
| 동의 → 2분 안에 자동 기록·하트비트 도착 | **검증됨** | 시작 시 부팅 이후 이벤트를 훑어 Kernel-Power 41(직전 비정상 종료)을 `power-41` 로 자동 기록, 암호화·서명 봉투로 수신 |
| 시험 실행(`--data-dir`)에서 자동 실행을 켜면 재부팅 때 "시험 수신기 설정…" 경고창 | **고침** | 바로가기 인자가 `--autostart` 뿐이라 로그인 때 기본 폴더로 떠 시험 설정 차단에 걸림. 기본 폴더가 아니면 `--data-dir "<폴더>"` 도 넣고, 시작 때 인자가 다르면 바로가기를 고친다(`StartupShortcut.ArgsFor`). 참여자 실행(기본 폴더)은 인자 그대로 `--autostart` — 기존 바로가기 변화 없음. 임시 시작 폴더로 동의→바로가기→로그인 실행 재현, 옛 인자 바로가기 자동 고침 확인 |
| 시험 수신기(StudyTools)가 저절로 꺼짐(코드 4) | 미확인(원인 추정) | 같은 시각 .NET 10.0.9 설치가 Restart Manager 로 ".NET Host" 사용 앱을 닫음 — 프레임워크 의존 StudyTools 가 걸렸을 가능성. 참여자판은 self-contained 라 해당 없음 |
| 다시 시작이 막힘(17:39 요청 → 17:40 실패, 18:12 재시도 때 pythonw.exe 가 거부) | 우리 앱 아님 | PcBlackBox 는 종료를 거부하지 않는다(SessionEnded 에서 아이콘만 숨기고 반환). Winsrv 10001 거부 기록은 pythonw.exe 뿐 |
| 7.4시간 연속 실행(10/29 18:47 → 10/30 02:12, 비관리자, 평소 사용) | **검증됨** | 기계 CPU **0.115%**, Private **135MB**(8분 측정 121MB 대비 +14MB). 동의문의 "약 120MB" 는 2주 실행 뒤 다시 맞출 것 — 2주 안정성은 미확인 |
| 동의문·안내문·exe 회사 정보의 팀 번호가 "5조" | **고침(0.7.1)** | README·발표 자료·엔진 문서대로 "7조". 참여자판 버전 0.7.1 |

## 참여자판 0.8.0 — 관리자 권한 기본 (10/30, 정태웅 결정)

결정: "기본적으로 관리자 권한을 얻는 프로그램". 매니페스트 `asInvoker` → **`highestAvailable`** — 관리자 계정이면 켤 때 UAC, 표준 사용자 계정이면 UAC 없이 0.7 과 같이(관리자 전용 항목 `unavailable`, CLAUDE.md 5).

관리자로 열리는 것: 커널 ETW(ProcessStart/Stop·ImageLoad 실시간 — 10초 안에 죽는 프로그램의 DLL 도 잡힘), fltmc 보안 필터 드라이버 목록(엔진 `Minifilters` 사실), 비관리자에서 접근 거부되던 프로세스(이 PC 기준 약 절반)의 모듈. **CPU 온도는 여전히 없음** — 권한 문제가 아니라 LHM 0.9.7 의 PawnIO 드라이버 미설치(위 "CPU 센서 원인 확정" 절). 관리자여도 커널 드라이버는 올라가지 않는다.

관리자 권한이 악용되지 않게 바꾼 것:

| 위험 | 대응 |
|---|---|
| 자동 실행이 사용자 폴더(다운로드 등)의 exe 를 관리자 권한으로 띄우면, 일반 권한 악성 프로그램이 exe·DLL 을 바꿔치기해 로그인 때 관리자 권한을 얻는다 | 시작 프로그램 바로가기 대신 **작업 스케줄러**(로그인 트리거 30초 지연, HighestAvailable, 실행 시간 제한 없음 — 기본 72시간이면 사흘 뒤 꺼짐, 배터리여도 실행, 중복 실행 안 함) + **`Program Files\PcBlackBox` 복사본**만 가리킴. 작업 XML 도 설치 폴더 안에서 만들어 넘김(사용자 임시 폴더면 등록 직전 바꿔치기 가능). 0.7 바로가기가 있으면 참여자가 켜 둔 것으로 보고 옮김 |
| 관리자 프로세스가 사용자 쓰기 가능 폴더(%LOCALAPPDATA%)에 쓰면, 미리 심은 정션·마운트 지점을 따라 관리자 권한으로 엉뚱한 곳에 쓰거나 지울 수 있다 | 기록 폴더를 **`C:\ProgramData\PcBlackBox\<SID>`** 로. 루트: 상속 끊음, 관리자·SYSTEM 만 쓰기, 소유자 Administrators, **OWNER RIGHTS(S-1-3-4) 읽기 전용 상속**(관리자 프로세스가 만든 파일도 소유자는 "만든 사용자"라 소유자의 암묵적 권한 변경으로 쓰기를 되찾는 길을 막음), 본인은 읽기만. 루트가 이미 있는데 관리자·SYSTEM 소유가 아니거나 연결이거나 다른 계정 쓰기 권한이 있으면 **쓰지 않고 멈춤**(ProgramData 는 일반 사용자도 폴더를 만들 수 있어 선점 가능). `--check-data-dir` 로 확인 |
| 콘솔판과 참여자판이 같은 커널 ETW 세션 이름을 쓰면 나중에 켠 쪽이 세션을 빼앗는다 | 세션 이름 분리: `PcBlackBox-Kernel` / 자체 점검 `PcBlackBox-Kernel-SelfTest` / 콘솔판 `CrashCollector-BlackBox-Kernel`(`CollectorHostOptions.EtwSessionName`) |
| 강제 종료 뒤 커널 ETW 세션 잔류(독립 검토 미확인 1건) | 로그오프 확정(SessionEnded) 때 세션부터 정지. 강제 종료(작업 관리자) 뒤에는 다음 실행이 같은 이름으로 대체 — 재부팅까지 남는 것은 그대로 |

| 시험 | 분류 | 결과 |
|---|---|---|
| 빌드·단위 시험 | **검증됨** | 경고 0, BlackBox.Tests 84/84, StudyCore.Tests 48/48, exe 매니페스트 `highestAvailable` 확인 |
| 관리자 아닌 경로(`__COMPAT_LAYER=RunAsInvoker` — 표준 사용자와 같은 경로) | **검증됨** | `--check-data-dir` SKIP, `--selftest` 4단계 통과(etw-kernel "권한 필요"), 트레이 흐름: 동의 → 바로가기(`--data-dir` 포함) → 직접 기록 → 종료, 예약 작업 안 만듦 |
| 관리자 경로 1차 시험(10/30 02:55, `tools/test-participant-admin.ps1`) | **검증됨(아래 결함 전까지)** | 선점된 루트(일반 권한으로 먼저 만든 폴더) 거부 · 루트 ACL = OWNER RIGHTS RX, SYSTEM F, Administrators F(상속 끊김) · 관리자 토큰으로 실행, 커널 ETW `PcBlackBox-Kernel` 실행 중 있음·정상 종료 뒤 없음 · 예약 작업 Command 따옴표(공백 경로)·인자·HighestAvailable·PT0S·30초 지연, 설치본 278개, 작업 XML 삭제됨 · 예약 작업으로 띄운 설치본이 관리자 토큰 · 그만두기 뒤 예약 작업·설치본·participant.json 모두 없음 · 일반 권한으로 기록 폴더 파일 쓰기·루트에 폴더 만들기·icacls 권한 변경 모두 거부 |
| **결함(1차 시험에서 발견) → 고침**: 사용자 폴더(`<SID>`)를 권한 목록을 넘겨 만들면 Windows 가 상위 권한을 상속해 주지 않아 "본인 읽기" 한 줄만 남음 — 관리자도 쓰기 불가, 검사가 "소유자 권한 제한 없음"으로 멈춤(관리자 계정 참여자 전원 실행 불가) | **고침 · 관리자 재시험 대기** | 사용자 폴더도 루트처럼 상속 끊고 전부 직접(SYSTEM·Administrators F, OWNER RIGHTS RX, 본인 RX). 관리자 소유인데 권한이 기대와 다르면 다시 걸어 바로잡음(이 PC 의 잘못 만들어진 폴더도 다음 관리자 실행에서 고쳐짐). 2·3차 시험은 UAC 가 취소돼 돌지 않음 |
| 예약 작업으로 띄운 인스턴스에서 15초 안에 커널 ETW 세션이 안 보임(직접 실행은 10초 안에 보임) | 미확인 | 소스를 차례로 켜고 ETW 가 마지막 — 작업 우선순위 7(낮음) 영향인지 시험 도구가 이제 뜨기까지 시간·우선순위를 잰다 |
| 관리자 실행 오버헤드 | 미확인 | 모듈 열거 대상이 늘어 0.7 의 0.1~0.2% 보다 오를 수 있음 — 관리자 시험 뒤 측정 |
| 시작 프로그램 폴더 바로가기로 관리자 권한 프로그램이 로그인 때 뜨는가 | 미확인 | 기대지 않고 작업 스케줄러를 씀 |

### 보안 검토 반영 (10/30, push 전)

- 검토 범위: `origin/integrate/collector-engine..HEAD` 20커밋 전체(수집기·참여자판·StudyCore·StudyTools·Code.gs·엔진 C++·도구). 독립 검토 1건 → 별도 검증(실재, 신뢰도 7/10, Medium — UAC 는 공식 보안 경계가 아니라서) → **고침**.
- 지적: 관리자로 도는 앱이 자동 실행을 정리하며 **사용자 시작 프로그램 폴더**의 고정 이름 파일을 `File.Exists`/`File.Delete` — 그 폴더는 같은 사용자의 일반 권한 프로그램이 정션(→ `\RPC Control`)+객체 관리자 심볼릭 링크로 돌릴 수 있어, 관리자 권한 임의 파일 삭제(로그인마다 프롬프트 없이)가 된다. 또 그 폴더의 같은 이름 파일을 "참여자가 켜 둔 자동 실행"으로 믿어 동의 없이 예약 작업을 등록했다.
- 고침: 관리자 프로세스는 시작 프로그램 폴더를 **건드리지 않는다**(시험용 `--startup-dir` 만 예외). 자동 실행 점검은 보호된 기록 폴더의 participant.json 에 남은 참여자 선택만 믿고, 예약 작업이 없어졌으면(참여자가 지움) 되살리지 않는다. 0.7 바로가기 이전은 없앰(0.7 은 배포된 적 없음).
- 같이 고침: 설치본 복사(사용자 폴더 → Program Files)에서 원본 폴더 안의 링크(정션·심볼릭 링크)를 따라가지 않는다 — 관리자 권한으로 남의 파일을 누구나 읽을 수 있는 Program Files 로 복사하는 길.
- 그 밖에 안전하다고 본 것(검토 근거): StudyTools decrypt(이름 정규식·`Inside()`·zip 허용 목록·서명 먼저), receive(127.0.0.1), 봉투 암호(AES-GCM+AAD, OAEP-SHA256, ES256 길이 접두), HTTPS 강제, Code.gs 입력 검증, 엔진 C++ 새 파싱 경계 검사, fltmc 절대 경로, 예약 작업 XML 이스케이프·`ArgumentList`.
- push 전 저장소 점검: 올릴 범위에 덤프·키·설정·`.pcae.json`·큰 파일 없음. 운영 문서의 개인키 경로에 Windows 계정 이름이 있어 `%USERPROFILE%` 로 바꾸고, 아직 올리지 않은 20커밋 범위에서만 역사를 다시 씀(filter-branch, 내용 차이는 그 한 줄뿐 확인). 10/28 에 멈춘 리베이스 기록(남은 단계가 개인 PC 덤프가 든 옛 커밋을 다시 적용)은 `git rebase --quit` 로 정리.

## 참여자판 0.8.1 — 실제 수신기 배포, 지인 공유 전 정리 (10/30)

발단: 지인에게 zip 을 보냈더니 "시험 수신기 설정" 경고로 멈춤 — 보낸 것이 127.0.0.1 시험 빌드(`-LOCALTEST`)였다. 실제 수신기를 배포하고 배포용 빌드를 다시 만들었다.

- 수신기: 팀 Google 계정에 Apps Script 웹 앱 배포(실행 사용자 "나", 액세스 "모든 사용자"). 주소·토큰은 `participant/study.local.json` 에만.
- 시험 빌드를 지인 PC 에서 그냥 켜면 "연구팀 내부 시험용이라 이 PC에서는 쓸 수 없어요" 안내로 멈춤(예전엔 `--data-dir` 설명이 나와 참여자가 이해할 수 없었음). zip 안 폴더 이름도 `PC블랙박스-팀시험용`.
- 결함 고침: 배포 스크립트가 `collectUntil`(수집 종료일)을 배포본 study.json 에 옮기지 않았다(0.8.0 까지). 계산 속성(`EndpointUri` 등)이 설정 파일에 저장되던 것도 `[JsonIgnore]`.
- 안내문 메모리 수치 120MB → 140MB(7.4시간 실측 Private 135MB 반영).
- 배포본: `PcBlackBox-0.8.1-win-x64.zip` SHA-256 `752467272a1bc678e1ded479f21d1b9a9d1e2a7a5c85454d6b2b44f494209af1`, 278파일. 문의처·수집 종료일(2026-12-11)·보관 기한(2026-12-31)이 들어 있다.

| 시험 | 분류 | 결과 |
|---|---|---|
| 실제 수신기 자체 점검(배포본, 비관리자, 10/30 20:09) | **검증됨** | 수집 시작 → 덤프 → 봉투 2건 전송(재시도 0·거부 0) → 하트비트, 수신기가 저장 파일 이름으로 응답(`ok:true`). Apps Script 302 → GET 응답 경로 동작 |
| Drive 폴더에 실제 파일이 생기고 팀 키로 풀리는가 | 미확인 | Drive 수신 폴더를 내려받아 `StudyTools decrypt` — 다음 수집 로그 확인 때 |
| 철회 메일(MailApp) | 미확인 | 시험 참여자로 그만두기 한 번 |
| 관리자 재시험(10/30 17:36): 사용자 폴더 ACL 수정 | **검증됨** | `<SID>` 폴더 = OWNER RIGHTS RX · SYSTEM F · Administrators F · 본인 RX, 1차에 잘못 만들어진 폴더도 고쳐짐. 관리자 토큰·보통 우선순위로 실행, 커널 ETW 2.9초 안에 뜨고 종료 뒤 없음, 그만두기 정리·일반 권한 쓰기 거부 재확인 |
| 예약 작업으로 띄운 인스턴스(시험 4단계) | 미확인(시험 무효) | 시험 도구가 설치본을 `%TEMP%` 아래에 둬서 앱의 "압축 파일 안에서 바로 실행" 보호에 걸려 창만 띄우고 멈췄다 — 0.8.0 절의 "ETW 가 15초 안에 안 보임"도 같은 원인이고 우선순위 7 가설은 틀림. 시험 폴더를 `%LOCALAPPDATA%` 로 옮김. 실제 설치본은 Program Files 라 해당 없음 |
| 반디집으로 zip 풀기(한글 폴더 이름) | **검증됨** | 278개 항목 모두 UTF-8 플래그·`/` 구분자 |
| 이 PC 실사용 | 진행 중 | 10/30 20:22 부터 배포본을 관리자로 설치해 예약 작업으로 상시 실행 |

- **분석 제외**: 참여 ID `r7gszmrm2b8p`(이 PC 비관리자 시험 참여, `%LOCALAPPDATA%\pcbb-real-e2e`) — 상태 확인 중 서명 개인키가 작업 기록에 찍혔다. 이 ID 로 들어온 봉투는 위장일 수 있으니 버린다. 자동 실행 바로가기는 지웠다(11/3).


## 참여자판 0.8.2 — 연구 키 교체 (11/10)

발단: 11/4 C드라이브 포맷·Windows 재설치로 `%USERPROFILE%\.pca-study` 의 개인키 `6e88a733c8746f43` 이 사라졌다. C·D·E·F 파일 이름 검색, Capstone2 내용 검색(UTF-8·UTF-16), zip 내부, D·E·F 휴지통에서 찾지 못했다. 그 키로 암호화된 봉투(11/3 내려받은 67개 포함)는 사본이 나오지 않는 한 열 수 없다.

- `StudyTools keygen --rotate` → 새 키 ID `deed820e822d5d7f`. 수신 주소·토큰·연구 ID·수집 종료일 유지. 수집·전송 코드 변경 없음(전송 범위는 11/10 결정으로 유지 — 동의문의 "앞 5분·뒤 30초"보다 실제 덤프가 넓은 점(24시간 롤업, 30일 WER 폴더 이름, 하트비트 운영 로그)은 알려진 불일치로 남아 있다).
- 버전을 올린 이유: `ElevatedAutostart.Install` 은 설치본과 ProductVersion 이 같으면 복사하지 않는다. 같은 버전으로 키만 바꾸면 Program Files 의 옛 study.json 으로 계속 암호화한다.
- 동의는 연구 ID·수신 주소에 묶여 있어 키 교체로 다시 동의받지 않는다. 참여 ID 유지.
- 재설치 후 .NET 8 런타임이 없다(SDK·런타임 10만) → 팀 PC 에서 StudyTools 실행에 `DOTNET_ROLL_FORWARD=Major`.
- 배포본: `PcBlackBox-0.8.2-win-x64.zip` SHA-256 `4f1c759bb5ac62ccd442214702a0e63038241ba0cb89d3668c932181ff9de165`, 278파일.

| 항목 | 분류 | 결과 |
|---|---|---|
| 배포본 자체 점검 → 시험 수신기(302 흉내) → 새 키로 `StudyTools decrypt` | **검증됨** | 11/10, 비관리자·127.0.0.1. dump·annotation·heartbeat 3건 kid `deed820e822d5d7f`, 복호화 실패 0 |
| 실제 Apps Script 수신기 + 새 키 → Drive 내려받아 복호화 | 미확인 | 0.8.2 를 동의 후 실행하고 Drive 수신분을 `decrypt` |
| 새 개인키 백업(USB) | 완료(사용자 확인) | 11/10 오전 사용자가 USB 복사 완료를 알림. 이번 사고의 원인 |
| 이전 키 사본(USB·팀원·클라우드) | 미확인 | 있으면 `.pca-study` 에 함께 두면 옛 봉투도 풀린다 |

## 외부 사례 조사·재현 (11/10)

- 조사 정리: `docs/external-survey.md`. 변환본: `engine/data/external/`(89건, 실측 아님, 라벨 18건). 변환 도구: `tools/import-external-report.ps1`.
- 재현 스크립트: `collector/experiments/repro/`. 트레이 앱을 끄고 콘솔 수집기(비관리자, `DOTNET_ROLL_FORWARD=Major`)로 로컬에만 저장.

| 항목 | 분류 | 결과 |
|---|---|---|
| 실제 프로그램 크래시 → 이벤트 1000·1001 → 덤프 → 엔진 (AutoHotkey 2.0.29 AltTabMenu) | **검증됨** | 11/10, 3/3. 0xC000041D, 유의성 major, 엔진 1순위 AutoHotkey64.exe |
| AutoHotkey 2.0.20 StrGet, CPython 3.13.5 os.execve | 미지원(재현 실패) | 크래시 없음. 이 PC·이 조건에서 재현 안 됨 |
| 비관리자 수집의 모듈 포착 | 권한 필요 | ETW 없음. 10초 스냅샷으로 대상 exe 1개만 잡힘 |
| 실제 Drive 수신분을 새 키로 복호화 | 미확인 | 0.8.2 배포 후 |

### 보안 프로그램 영향 측정 준비 (11/10 밤)

- 스크립트: `collector/experiments/repro/` — `perf-measure.ps1`(유휴·작업 부하), `perf-compare.ps1`, `morning-elevated.ps1`(관리자 1회 실행: 설치마다 변화 기록·측정·수동 마킹), `gui-npp-column.ps1`.
- 일반 권한 설치 시도 10종 모두 설치 안 됨. 받은 파일·해시·서명: `repro-runs/installers/manifest.json`(커밋 제외).

| 항목 | 분류 | 결과 |
|---|---|---|
| Godot 4.3 SVG 임포트, Notepad++ 8.6.7 Column Editor 크래시 → 덤프 | **검증됨** | 각 3/3 |
| 이 PC 유휴 기준선(HVCI 켜짐) | **검증됨** | CPU 20~23%, NVIDIA Overlay·FvContainer·PresentMon 각 약 6.2%. 2회 동일 |
| 측정 재현성 | **검증됨** | 같은 조건 2회: 읽기 14%, 삭제 24% 차이. spawn·build 2% 안쪽 |
| 금융 보안 모듈 설치 후 영향 | 권한 필요 | `morning-elevated.ps1`, 관리자 승인 필요 |
| `morning-elevated.ps1` 관리자 실행 | 미확인 | `-DryRun`(해시·서명 검증)만 통과 |
| Vanguard·EAC 등 안티치트 영향 실측 | 권한 필요 | 게임 설치·관리자·재부팅 필요 |
| 부팅 시간 측정 방법 | 미확인 | 방법 미정, 스파이크 필요 |

### 아침 점검·관리자 실행 전 보강 (11/10 오전, 비관리자)

- `morning-elevated.ps1`: `-Only a,b` 가 `-File` 실행에서 문자열 하나로 들어와 **0개를 고르던 버그** 수정. 상태 기록에 `listening`(TCP 수신 포트|프로세스) 추가. PC 블랙박스 로그온 작업이 켜져 있으면 시작 거부(검사만 하고 끄지는 않음 — 원칙 3).
- 새 `conflict-evidence.ps1`(읽기 전용): 첫 설치 이후 오류 이벤트(System 41·1001·6008·219·7000~7034, Application 1000~1002, CodeIntegrity 3033·3076·3077)를 **직전 같은 길이 구간과 나란히**, 새 서비스·드라이버 파일의 버전·서명·일반 사용자 쓰기 권한(파일·폴더, SID 기준), 제품별 새 포트·루트 인증서·미니필터. 이 PC는 설치 없이도 7일간 서비스 시작 실패 34건(7026 15, 7001 12)이라 비교 구간이 필요하다.
- 공개 취약점과 받은 버전 대조: `docs/external-survey.md` 7절.
- 11/10 오후: 실사용 영향 실험 설계 `docs/security-impact-design.md`(v2), 국내 논문·뉴스·정책·측정 방법 조사 `docs/korea-ksa-research.md`. **설치(`morning-elevated.ps1`)는 S0 측정 뒤로 보류** — S0은 설치 전에만 잴 수 있다.
- 11/10 저녁: 목표 재정의 `docs/capstone-plan-v2.md`, 제품 구조(위험도 점수·지식 팩·Claude API·실행기) `docs/product-architecture-v2.md`, 게임 보안·백신·논문·버그 리포트 조사 `docs/security-software-survey.md`(원자료 `engine/data/external/_reports/bug-reports-20261110.md`). 모두 설계·조사 초안이고 코드 변경은 없다.
- 11/10 밤 결정(정태웅): 목표 재정의 동의 · 측정은 권장판, **무인 자동화 완성 뒤 실행** · 위험도 점수는 C# 라이브러리 · API 키는 빌드에 넣고 테스터·평가자에게만 공유 · 동의문 AI 항목은 다음 배포 때. 최종 목표를 "분야를 가리지 않는 감지·점검·치료·권고 + 프로그램 자체 버그"로 넓힘(`docs/capstone-plan-v2.md` 2절).

| 항목 | 분류 | 결과 |
|---|---|---|
| `Get-State`(관리자 스크립트에서 아직 한 번도 안 돈 부분) | **검증됨** | 비관리자 1.3초, 예외 없음. 미니필터 0개 = 권한 필요 |
| `-Only` 여러 개 | **검증됨** | 수정 전 0개, 수정 후 2개 검증 |
| 수신 포트 신호 | **검증됨** | 스파이크 38개, 0.5초 |
| `conflict-evidence.ps1` | **검증됨**(가짜 diff) | 실제 서비스·드라이버 4개 버전·서명, 없는 경로는 unavailable, Users 쓰기 폴더 양성 대조 잡힘·시스템 파일 음성, `%USERPROFILE%` 노출 없음 |
| `conflict-evidence.ps1` 실제 설치 후 | 미확인 | 관리자 실행 후 |
| 미니필터 목록 | 권한 필요 | `fltmc` |
| PC 블랙박스 자동 실행 | **검증됨** | `\PcBlackBox\Autostart-<SID>` 로그온 트리거·Ready·한 번도 안 돎. Program Files 본은 **0.8.1, keyId 6e88(잃어버린 키)**. 재부팅하면 측정 중 실행됨 |
| NVIDIA Overlay·FvContainer 점유 교차 확인 | **검증됨** | `Get-Process` CPU 초 차이로 각 10.0초/10초(스레드 1개). PresentMon 은 비관리자로 값 안 읽힘(권한 필요) |
| 작업 부하의 `dotnet build` 성공 여부 | **검증됨** | 종료 코드 0, 3.3초 — 기준선 빌드 시간은 실제 빌드 |
| 복원 지점 상태 | 권한 필요 | `Get-ComputerRestorePoint` 비관리자 실패 |
| 드라이버 SHA-256 전수 계산 + LOLDrivers 대조 (11/10 오후 스파이크) | **검증됨** | 등록 드라이버 410개·316.5MB 1.5초, 목록 713건(해시 2,221개) 읽기 0.75초, 못 읽은 파일 0(비관리자). 해시 일치 0개. 이름만 일치 3개 중 2개는 Windows 정상 파일(`afd.sys`, `KslD.sys`) → 이름 대조는 판정에 쓰지 않음 |
| `xhunter2.sys`(XIGNCODE3, 로스트아크) | 미확인 | 이 PC 2026.9.16.465, 실행 중·시작 유형 수동. 공개 취약 버전은 2026.6.1.192(CVE-2026-15430). 수정 여부는 공개 자료로 판정 불가 |
| 프로세스 샘플러 3종 비교 (11/10 밤, `lab/spikes/spike-probes.ps1`, 게임 실행 중) | **검증됨** | `NtQuerySystemInformation` 7.1ms / PerformanceCounter `ReadCategory` 966ms(10/29 유휴 때는 260ms) / WMI PerfProc 645ms. 앞의 것은 비관리자로 SYSTEM 프로세스(MsMpEng·PresentMon)의 CPU 시간도 읽힘, 프로세스 365개 일치. 반공개 API라 수집기 본 코드에 넣을지는 미정(실험 스크립트에서만 사용, 시작 시 구조 자기 점검) |
| UI 응답 탐침(WM_NULL 왕복) | **검증됨** | 300ms 멈춤 6회 중 6회 감지, 평소 0.07ms, 부하 스레드 하나의 0.5% |
| `GetLastInputInfo` | 일부 검증 | `SendInput` 도 값을 바꿈 → 스크립트 입력 구간에서는 사람 입력 감지 불가. 무입력 시 안정성은 미확인 |
| Defender·Windows Update 이벤트(비관리자) | **검증됨** | Defender 검사 시작 6회/7일, 정의 갱신 36회/7일. Windows Update 설치 96건/14일(드라이버 10, 스토어 앱 63, 정의 18) — 업데이트 일시 중지 안 된 상태 |
| 실험용 앱 3종 받기·검증 (11/10 밤) | **검증됨** | Blender 5.2.3 LTS·Godot 4.7.2·Notepad++ 8.9.9 포터블. 공식 체크섬 일치, 서명 Valid. `lab/tools.lock.json` 에 고정 |
| Blender 작업 변동 (`lab/spikes/spike-tasks.ps1`, 같은 부팅 4회) | **검증됨** | 전체 16.0초 변동계수 0.3%, 렌더 13.4초 0.4%, 물리 2.0초 0.8%. 부팅 후 첫 실행은 31초 |
| Godot 작업 변동 (같은 부팅 4회) | **검증됨** | 임포트 3.97초 0.2%, 스크립트 계산 4.0%, 파일 입출력 5.2% |
| Logitech G HUB 자원 사용 (30초, 비관리자) | **검증됨**(이 시점 값) | CPU 합계 0.02% 미만, 전용 메모리 약 270MB, 로컬 포트 3개·서비스 2개·드라이버 4개. 간헐적 급증 여부는 미확인(24시간 기록 필요) |
| Wacom 드라이버, LG 모니터 앱 | 미확인 | 이 PC에 없음 |
| 영향 실험 자동화 `lab/` (11/10 오후, 사용자 외출 중 구현·시험) | 아래 | 사용법 `lab/README.md` |
| ├ 작업 3종(Blender·Godot·Notepad++)과 지표 | **검증됨** | Notepad++ 입력 지연 중앙값 0.68ms·p95 1.04ms(작은 파일). 50MB 파일에서는 앱 자체가 몇 글자마다 0.6초 멈춰 입력 지연 측정에 부적합 → 큰 파일은 열기·저장에만 사용 |
| ├ 배경 3종(유튜브·은행 페이지·문서 순환) | **검증됨** | 은행 페이지는 Chrome 157에서 로컬 네트워크 권한 창이 뜸 → 실험용 프로필에만 `LocalNetworkAccessChecks` 끔 |
| ├ 블록 전체 | **검증됨** | 시험용 15런 5.8분. 실제 대기 시간 리허설 33런 43.8분, 오류 0·제외 0. 같은 부팅 안 흔들림 0.4% 안팎, 배경 브라우저만으로 Blender +3.9%·Godot 임포트 +4~5% |
| ├ 상태 기계(시범→계획→설치 전→설치→적응→설치 후, 정지·재개, 시간 부족, 설치 실패) | **검증됨(모의)** | 재부팅 대신 재호출. 실제 재부팅·자동 로그인·화면 잠금은 미확인 |
| ├ 설치 창 자동 클릭 | 일부 검증 | 시험용 마법사 통과. 실제 설치 창 미확인. 이 PC에서 UIA 는 WinForms 버튼을 Pane 으로만 보여 줘 Win32 `BM_CLICK` 으로 구현 |
| ├ `setup-once.ps1`·관리자 예약 작업 | 검증됨 | 11/10 저녁 사용자가 관리자 창에서 실행. 8개 파일이 `C:\Program Files\PcaLab`에 복사됐고 해시가 원본과 일치, 일반 권한으로는 쓰기 거부, `\PcaLab\Install`은 트리거 없음·최고 권한·실행 이력 없음 |
| ├ `install-elevated.ps1`(설치 단계 본체) | 미확인 | 등록만 됨. `-AllowInstall` 세션에서 처음 동작 |
| ├ 앱 패키지 설치·업데이트 감지 (AppXDeploymentServer/Operational 400) | **검증됨** | 비관리자로 읽힘, 한 번 확인에 14ms. `DeploymentOperation` 값(이 PC 14일 271건에서 메시지와 대조): 1 Add·2 Remove·4 Stage·5 DeStage·6 Register·27 OnDemandRegister·29 Provision·33 Deprovision. Store 앱 업데이트는 `svchost.exe,wuauserv`가 Stage로 수행(73건). 1·2·4·5가 런 구간에 있으면 제외. 11/10 04:59 Claude 앱 자체 업데이트(Add)를 잡고, 05:10 Register만 있는 구간과 리허설 구간은 깨끗하다고 판정 |
| ├ Microsoft Store 앱 자동 업데이트 끄기 | 미확인 | 11/10 Store 설정에서 "이 장치에서 업데이트를 일시 중지하려면 관리자 권한이 필요합니다"로 거부됨(계정은 관리자 그룹). 원인 모름. 정책 키 `HKLM\SOFTWARE\Policies\Microsoft\WindowsStore`는 없음. 끄지 못하므로 위 감지로 대신한다 |
| ├ 자동 로그인(Sysinternals Autologon) | **검증됨** | 11/10 첫 시도는 "The user name or password is invalid"(이 PC의 계정은 Microsoft 계정). 사용자가 다시 설정한 뒤 `AutoAdminLogon=1`, 평문 암호 없음. 11/11 세션에서 재부팅 6번 모두 자동 로그인됨 |
| ├ 실제 재부팅 후 자동 재개·세션 끝 잠금 | **검증됨** | 11/11 02:22~07:25 첫 실제 세션. 재부팅 6번, 시범 3블록(각 25분) + 설치 전 4블록(각 56분), 201런 중 오류 1·제외 1. 끝낼 시각 57분 전에 "블록 하나 더 돌 시간 없음"으로 스스로 끝내고 런처를 지움 |
| ├ 부팅 간 흔들림 (설치 전 4부팅) | **검증됨** | Blender 작업 13.0초 CV 0.4%, Godot 임포트 3.44초 CV 0.05% 이하, Godot 에디터 창 2.6초 CV 0.5~1.1%, Notepad++ 50MB 열기 CV 0.4~5%, 시작 4.5~9%, 입력 지연 p95 2~4%. 배경 브라우저만의 영향: Blender 유튜브 +0.8%·문서 +0.9%·은행 페이지 0.0%, Godot 임포트 ±0.5% 안. **11/10 리허설의 "+3.9%, +4~5%"는 PC를 쓰던 상태의 값이라 과대였다(정정)** |
| ├ Defender 정의 업데이트 뒤 첫 실행이 느려짐 | **검증됨** | 11/11 03:25:58 정의 갱신(Defender/Operational 2000, WindowsUpdateClient 19·43으로도 보임. Windows 업데이트 일시 중지와 무관하게 들어온다). 직후 Godot 임포트 3.44→4.52초, Blender 창 1.0→2.7초, 그다음 실행부터 정상. 최근 7일 하루 2~3번. "정의 갱신 뒤 각 앱의 첫 실행"을 제외 규칙에 추가(설치 전 4블록 안에는 갱신 없음) |
| ├ 환경 지문 기준 (버그) | **검증됨(수정)** | 기준을 첫 세션 시작(재부팅 전)에 저장한 탓에 측정한 부팅 전부와 달랐다: 에디션 Home→Enterprise 전환이 첫 재부팅에서 끝났고(AppVClient·AssignedAccessManagerSvc·AppMgmt·CSC 등장, 두 번째 부팅부터 Sense·MsSecCore), 와콤 드라이버(`WacHidRouterPro`, `wacomrouterfilter`)가 올라옴. 사용자별 서비스 접미사(`CDPUserSvc_d5677`)도 로그온마다 바뀐다. 수정: 접미사 정규화, 세션 시작은 최근 블록과 등록 드라이버 기준 비교, 블록끼리 비교 기록, 지문에 에디션 추가. 실제 상태 사본으로 다음 세션 시작을 모의 실행 → 차단 0건 |
| ├ Godot 표준 오류 한 줄에 런이 죽음 (버그) | **검증됨(수정)** | s0-04 38번 런 "Unicode parsing error: Unpaired trail surrogate". PS 5.1은 `$ErrorActionPreference='Stop'`에서 `2>&1`의 첫 stderr 줄을 종료 오류로 만든다. 측정 명령은 그대로 두고 그 구간만 `Continue`로, stderr 줄은 `console_stderr`로 기록. Godot가 그 줄을 낸 원인은 미확인(67런 중 1번) |
| ├ Windows 에디션 | 미확인 | 11/10 낮까지 Home, 11/11 02:24 부팅부터 Enterprise(`EditionID=Enterprise`, 설치일은 11/04 그대로). 누가 왜 바꿨는지는 사용자 확인 필요. 측정한 블록은 전부 Enterprise 상태 |
| ├ 와콤 타블렛 드라이버 6.3.46-2 | **검증됨**(설치 사실) | 11/10 저녁~11/11 02:22 사이 설치됨. 프로그램 목록의 표시 이름에 깨진 UTF-16 문자가 들어 있음. 설치 전·후 측정에 똑같이 포함된다. 848행의 "이 PC에 없음"은 11/10 낮 기준 |
| └ 함정 | — | Windows PowerShell 5.1 은 BOM 없는 .ps1 을 ANSI 로 읽는다 → 스크립트 안의 한글 문자열이 깨져 정규식이 안 맞았다. lab 스크립트는 ASCII 만 쓰고 한글은 UTF-8 JSON 에서 읽는다. `shutdown /a` 는 대기 중인 종료가 없으면 오류를 내므로 `$ErrorActionPreference='Stop'` 에서 스크립트를 죽인다(정지 스크립트가 이 때문에 동작하지 않던 것을 모의 실행에서 발견) |
| **10/28 PerfProc 수치의 단위 (정정)** | **검증됨** | `Win32_PerfFormattedData_PerfProc_Process.PercentProcessorTime` 은 논리 CPU 1개 기준. 359행 SignKoreaWD 22%·Goji 22%·SKBspmtmon 13%는 PC 전체로 각 1.4·1.4·0.8%(같은 스파이크의 chrome 93·WmiPrvSE 31 포함 합계 181%로 확인). 수집기 `ResourceSource` 는 이미 `/ProcessorCount`(전체 기준), 엔진 규칙 E(≥10%)도 전체 기준이라 서로는 맞다. 틀린 것은 문서와 엔진 샘플 s12 의 13.9 |

## 참여자판 0.9.0 — 자동 업데이트 (11/11)

발단: 버전이 바뀔 때마다(0.8.1 → 0.8.2) 새 zip 을 참여자마다 다시 건네야 했다. 11/11 결정: 공개 GitHub Releases 로 내보낸다(학교 지원 조건이 오픈소스 공개). 절차·보안 모델은 `docs/study-operations.md` 2-5절.

- 수집 코드(`collector/`)는 바꾸지 않았다. 바뀐 곳은 `participant/` 와 `tools/` 뿐이다.
- 판단·검증 로직은 `participant/StudyCore/Update.cs`(서명·버전·주소 규칙, 묶음 풀기, 폴더 바꿔치기), 네트워크·프로세스는 `participant/BlackBox.Participant/Updater.cs`.
- 저절로 설치하지 않는다. 새 버전은 알림만 띄우고 참여자가 "업데이트"를 눌러야 바뀐다(CLAUDE.md 3: 사용자 선택 없는 자동 조치 금지).
- 공개 묶음에는 `study.json` 이 없다(수신 주소·토큰·문의처 비공개). 설치본의 것을 이어 쓴다. 묶음만 받은 사람은 앱을 켤 수 없다.
- 믿는 것은 업데이트 서명 키(ECDSA P-256) 하나. 개인키 `%USERPROFILE%\.pca-study\update-signing.private.pem`(11/11 만듦, 저장소 밖), 공개키는 `UpdateTrust.Keys`.
- 관리자 실행은 `C:\Program Files\PcBlackBox` 설치본만 바꾼다. 받은 묶음은 `C:\ProgramData\PcBlackBox\<SID>\update\` 에 풀고 거기서 실행한다.
- 교체는 새 exe 가 한다(`--apply-update`): 이전 버전의 코드는 받기·확인·넘기기만 하므로, 교체 단계의 버그는 다음 버전에서 고칠 수 있다.
- 0.8.x 설치본에는 이 기능이 없다 → 0.9.0 zip 을 한 번 더 직접 건네야 한다.
- `ElevatedAutostart.Install` 은 설치본이 더 새 버전이면 덮어쓰지 않는다(내려받기 폴더의 옛 exe 를 켜도 설치본이 되돌아가지 않게).
- 문의처를 학교 메일로 바꿨다(`participant/study.local.json`, 커밋 제외). 바꾸기 전 파일은 `%USERPROFILE%\.pca-study\study.local.json.bak-20261111`.

| 항목 | 분류 | 결과 |
|---|---|---|
| 서명·버전·주소 규칙, 묶음 풀기(폴더 밖 이름·상한), 폴더 바꿔치기·되돌리기 | **검증됨** | StudyCore.Tests 90건(기존 48 + 업데이트 42). 서명 뒤 고친 안내문, 다른 키 서명, 남의 저장소·http·127.0.0.1 주소, 낮거나 같은 버전, `..`·절대 경로·드라이브 문자 이름, 사용 중인 폴더(아무것도 안 바뀜) |
| 전체 흐름 — 표준 사용자 경로 (`tools\test-update.ps1`, 비관리자, `__COMPAT_LAYER=RunAsInvoker`) | **검증됨(이 PC)** | 11/11, 3회 실행 중 마지막 28개 확인 모두 통과. 실제 서명 키로 서명한 0.9.1 묶음(67.7MB, 277파일)을 127.0.0.1 에서 받음. ① 0.9.0 → 0.9.1: 확인·받기·SHA-256·풀기·시험 실행·수집 정지·교체·재시작·수집기 시작 확인까지 38~43초(시험은 켜고 25초 뒤에 시작), `study.json`·참여 ID 유지, 이전 폴더 정리 ② 낮은 버전 제안 무시 ③ 새 버전이 못 뜸(흉내) → 0.9.0 으로 되돌리고 다시 켬, 0.9.1 을 건너뛸 버전으로 기억 ④ 건너뛴 버전 다시 안 받음 ⑤ 서명 뒤 고친 안내문 → 서명 확인에서 거부, 받지 않음 ⑥ 같은 크기로 1바이트 바꾼 묶음 → SHA-256 에서 거부, 풀지 않고 받은 파일 삭제 |
| 전체 흐름 — 관리자 경로(예약 작업 + 설치본 교체) | **미확인** | `tools\test-update.ps1 -Mode admin`. 시험용 예약 작업(`-TEST`)과 시험 폴더만 쓴다. 11/11 17:56 첫 시도는 "Mode admin needs an elevated PowerShell" 로 멈췄다 — 창이 `C:\WINDOWS\system32` 에서 시작했지만 관리자 창이 아니었다(그 창의 프로세스 토큰을 확인: 권한 상승 아님, 부모 explorer). 스크립트를 고쳐 일반 창에서 실행하면 일반 권한으로 빌드한 뒤 UAC 를 한 번 묻고 관리자 쪽을 돌리게 했다(관리자 쪽 절반은 아직 실행된 적 없음) |
| 릴리스 도우미 `tools\release-participant.ps1` | **검증됨(예행)** | 11/11. 커밋 전에는 빌드를 거부(실제 저장소에서 확인). 임시 복제본에 커밋해 끝까지 실행: 실제 설정으로 빌드, 세 파일 생성·검증, 빌드한 exe 의 버전 정보가 `0.9.0+<그 커밋 해시>` 인 것 확인, 로컬 태그. `-Verify` 는 릴리스가 없는 지금 세 주소 모두 실패(404)로 보고 — 올린 뒤의 성공 경로는 미확인. `gh` 가 이 PC 에 없어 `-Publish` 는 미확인 |
| 실제 설치 위치(`C:\Program Files\PcBlackBox`, `C:\ProgramData\PcBlackBox\<SID>\update`)에서의 교체 | **미확인** | 시험은 시험 폴더에서만 돈다. 관리자 실행의 대상·실행 위치 제한(설치본만, 보호된 폴더에서만)은 실제 경로에서만 걸린다. 첫 실제 릴리스 전에 팀 PC 또는 Hyper-V 가상 머신에서 pre-release 가 아닌 흐름으로 한 번 |
| GitHub Releases 에서 받기(`/releases/latest/download/update.json` → 302 → 파일) | **일부 검증** | 11/12 릴리스 게시 뒤 `-Verify` 로 앱이 쓰는 주소 그대로 받아 확인(세 파일 일치, `update-verify` 통과). **설치된 앱이 실제 주소에서 받아 교체하는 것은 미확인** — 0.9.0 은 자기 자신과 같은 버전이라 "받을 것 없음"만 나온다. 0.9.1 을 낼 때 처음 일어난다. 릴리스가 없을 때의 404 는 "받을 것 없음"으로 처리 |
| github.com 이 막힌 망(회사·학교) | 미지원 | 확인 실패로 남는다(하트비트 `update.checkError`) → zip 을 직접 건넨다 |
| 교체 도중 강제 종료(전원·로그오프) | 미확인 | 이름 바꾸기 두 번 사이에 끊기면 설치 폴더가 없는 상태가 될 수 있다(창은 매우 짧다). 그 밖의 시점은 이전 버전 그대로이거나, 다음 실행에서 새 버전이 스스로 확인 |
| 뜨기는 하지만 잘못 동작하는 새 버전 | 미지원 | 되돌리기 기준은 "수집기가 떴는가"뿐이다. 팀 PC·pre-release 로 먼저 확인 |
| 동의 화면의 업데이트 안내 한 줄 | 추가됨 | 동의 버전은 올리지 않았다(다음 참여자 배포 때 AI 전송 문구와 함께 개정 예정 — 11/10 결정). 기존 참여자는 업데이트 확인 창과 `읽어주세요.txt` 7절에서 본다 |
| 저장소 `LICENSE` 파일 | 없음 | README 는 MIT 라고 적고 링크만 있다. 11/11 결정: 추가하지 않고 진행 |
| 릴리스 게시 | 하지 않음 | 11/11 "릴리즈 진행" 지시를 받았으나 두 가지가 먼저다: ① 릴리스 빌드는 커밋된 소스에서만 만든다 — 커밋은 역할별 작성자로 직접 하기로 한 부분(11/10), 그리고 푸시하면 원격에 없는 커밋 전부가 공개된다 ② GitHub 에 릴리스를 만들 수단(`gh` 또는 브라우저 로그인)이 이 세션에 없다. 관리자 경로 시험도 남아 있다 |
| Hyper-V | 꺼져 있음 | 11/11 18:00 확인: 기능 `Microsoft-Hyper-V-All` 등 disabled(이 에디션에 제공됨), 서비스 vmms·vmcompute 없음, `Get-VM` 없음, ISO 파일 없음. 켜려면 관리자 권한과 재부팅이 필요해 진행하지 못했다. `lab\vm\enable-hyperv.ps1`(UAC 한 번, 재부팅 안 함) 준비 — 관리자 쪽 절반 미확인. "Hyper-V Administrators" 그룹(S-1-5-32-578)과 `-NoRestart` 매개변수가 있는 것은 확인 |
| Windows 에디션이 Enterprise 가 된 이유(단서) | 미확인 | `dsregcmd /status`: `WorkplaceJoined: YES`(회사·학교 계정이 이 PC 에 등록돼 있음), AzureAdJoined·DomainJoined 는 NO. 학교 계정 등록과 에디션 전환의 관계는 추정일 뿐이다 |
| 전체 흐름 — 관리자 경로 (11/11 저녁 재시도) | **검증됨(이 PC)** | 사용자가 관리자 창에서 `tools\test-update.ps1 -Mode admin` 실행: 28개 확인 모두 통과(업데이트 36초, 새 버전이 시험 설치본 `startup\install` 에서 실행, 되돌리기 뒤 이전 버전 재실행, 건너뛰기, 고친 안내문·바뀐 묶음 거부). 마지막 정리 확인 줄에서 스크립트가 멈춤("The system cannot find the file specified", 247행): 예약 작업이 지워져 `schtasks` 가 표준 오류에 쓴 한 줄을 PS 5.1 이 종료 오류로 바꾼 것(lab 의 Godot 건과 같은 함정). 정리 자체는 됐다 — 시험 작업 없음, 시험 설치본·시험 기록 없음, 남은 프로세스 없음을 따로 확인. 확인 줄을 `cmd /c` 로 고침(고친 줄은 관리자 실행으로는 미확인, 일반 권한에서 없는 작업 조회 → 종료 코드 1·예외 없음 확인) |
| 실제 설치 위치(Program Files)에서의 교체 | 미확인(그대로) | 위 시험은 시험 폴더에서 돈다 |
| 릴리스 0.9.0 게시 | **검증됨** | 11/12. 브랜치(`03f558b` 까지)와 태그 `v0.9.0`(`8f4b7d7`) 푸시, 사용자가 브라우저에서 릴리스 게시(11/12 00:29, 초안·pre-release 아님, "latest" = v0.9.0). 첨부는 세 파일뿐(업데이트 묶음 70,944,328 바이트·`update.json` 384·`update.json.sig` 88) — 참여자 zip 은 올라가지 않음(공개 API 로 목록 확인). `tools\release-participant.ps1 -Verify`: GitHub 가 내주는 세 파일이 로컬과 바이트까지 같고 `update-verify`(서명·버전·주소·크기·SHA-256) 통과. GitHub 가 계산한 묶음 SHA-256 도 `a279cf5c…` 로 일치 |
| 릴리스 0.9.0 빌드 | 완료 | 11/11 18:24 커밋 `8f4b7d7` 에서 `tools\release-participant.ps1` 로 빌드: 참여자 zip SHA-256 `564471915f13d75f5a11aa44df0e3bcf5993ceb708cbc9798f09f82aa1f95f36`(278파일), 업데이트 묶음 SHA-256 `a279cf5c5fe1768c6e51767c29c2346a59462de5c03fb44f6f5be50cc6e46c02`(277파일, 연구 설정 없음), exe 버전 정보 `0.9.0+8f4b7d7…`, 로컬 태그 `v0.9.0`. 푸시는 GitHub 로그인 창(Git Credential Manager — 11/4 재설치 뒤 저장된 로그인 없음)에서 대기. 릴리스 만들기(세 파일 첨부)와 `-Verify` 는 그 뒤 |
| Hyper-V 켬 | **검증됨** | 11/11 사용자가 `lab\vm\enable-hyperv.ps1 -AddMeToHyperVAdmins` 실행(UAC) → 18:19 재부팅. 기능 4개 enabled, 서비스 vmms·vmcompute 실행 중, 일반 권한 창에서 `Get-VM`·`Get-VMHost`·`Get-VMSwitch` 동작(Hyper-V Administrators 그룹 적용), 가상 머신 0개, 스위치 `Default Switch`. 재부팅 전에는 기능만 enabled 로 보이고 서비스는 없었다 |
| Hyper-V 켜기 전 측정 자료 보관 | 완료 | `lab\runs` → `lab\runs-before-hyperv-20261111`(시범 3 + 설치 전 4블록, `ARCHIVED.txt`). `lab\runs` 가 없으므로 다음 `start-session.ps1` 은 시범부터 새 실험을 시작한다. 새 실험의 첫 세션 시작(환경 지문·금지 프로세스 검사)은 미확인 |
| 게스트 Windows 설치 파일(ISO) | 없음 | 가상 머신 스크립트(생성·체크포인트·게스트 안 실행)는 ISO 가 있어야 시험할 수 있다 |

## 11/12 — 계획 검토 반영, 가상 머신 스크립트, 배포 검사

결정(정태웅, `docs/capstone-plan-v2.md` 맨 위 "11/12 결정"): 목표 보강(원인이 보안 프로그램이 아니어도 원인 프로그램을 찾는다, 대상은 로그를 읽기 어려운 사용자) · 가상 머신을 사례 생산에 사용 · 호스트 설치는 11/24~11/26 시작 · 범위 축소(상담 창 대화, 사람 라벨링, Lv3 실제 실행 제외) · 일정은 1인 기준.

| 항목 | 상태 | 근거 |
|---|---|---|
| 이 PC의 가상 머신 여력 | 검증됨(자원) | Ryzen 7 9800X3D 8코어 16스레드, 메모리 93.7GB(여유 약 60GB), `F:` NVMe 여유 1,382GB, VBS·HVCI 동작 중. 한 대 4 vCPU·8GB 기준 2~3대 동시 권장 |
| 일반 권한(Hyper-V Administrators)으로 가상 머신 다루기 | **검증됨** | 스파이크: 2세대 생성, CPU·고정 메모리, 보안 부팅, 키 보호기 + 가상 TPM, 차등 디스크, 꺼진 상태 체크포인트, 시작(메모리 8GB 할당, `vmwp` 1개), 끄기, 복원, 삭제 — 관리자 권한 없이 모두 동작 |
| 통합 서비스 이름으로 켜기(`Enable-VMIntegrationService -Name 'Guest Service Interface'`) | 미지원(이 PC) | 한국어 Windows에서는 이름이 번역돼 있어 실패("No integration component with the given name"). 파일 복사는 `Copy-VMFile` 대신 PowerShell Direct 세션(`Copy-Item -ToSession`)으로 설계 |
| `lab\vm\base.ps1`, `clone.ps1` | **검증됨(OS 없이)** | `lab\vm\test-vm-scripts.ps1` 24개 확인 통과: 원본 생성(유효한 시험 ISO)·중복 거부·하드웨어 값·DVD 우선 부팅·봉인(읽기 전용)·봉인 뒤 폐기 거부, 복제 2대 동시 실행, 차등 디스크, 실행 중 체크포인트, 복원 2종, 목록, 삭제. 끝나면 시험용 가상 머신·파일 0 |
| ├ 읽을 수 없는 ISO | 검증됨 | 시작 실패(0x80070570) → 가상 머신과 폴더 삭제 후 이유 출력. 처음 구현은 삭제가 조용히 실패해 반쯤 만든 원본이 남았다 → 재시도하는 삭제로 고치고, 정리가 덜 됐으면 그렇게 말하게 함 |
| ├ 복원 직후 상태 | 검증됨(주의) | 꺼진 상태 체크포인트로 복원한 직후, 스크립트 안에서는 `Off`인데 다른 프로세스에서는 약 4초 동안 `Running`으로 읽혔다. 상태가 5회 연속 같을 때까지 기다린 뒤 보고 |
| `lab\vm\guest.ps1`(PowerShell Direct) | **미확인** | 자격 증명이 없을 때의 안내만 확인. 게스트 Windows가 없어 실행·복사·대기는 돌려 보지 못함. 일반 권한 창에서 되는지도 미확인 |
| 실제 Windows 설치·봉인·복제본 부팅, 복제본의 새 가상 TPM | **미확인** | ISO 없음(사용자가 평가판 센터에서 받아야 함 — 등록 양식) |
| GPU 없는 가상 머신에서 Blender·Godot 창 | **미확인** | |
| 금융 보안 모듈의 가상 머신 안 동작 | **미확인** | 첫 설치 예행연습에서 프로세스·드라이버 목록으로 확인 |
| AI 서비스 키 검사 `tools\find-api-key.ps1` | **검증됨** | 만든 가짜 키로 7개 확인: 접두어만 있는 문서는 통과, ASCII 키·UTF-16 키(바이너리 안)·zip 안의 키는 검출, 스크립트에서 불러 쓰는 방식. 0.9.0 참여자 폴더와 공개한 업데이트 묶음은 "키 없음". `publish-participant.ps1`(배포 폴더)과 `release-participant.ps1`(올릴 묶음)에 연결 — 연결한 뒤의 전체 빌드는 다음 릴리스에서 처음 돈다(구문 검사만 함) |
| ├ 한계 | 미지원 | 쪼개거나 인코딩·암호화해 넣은 키는 못 찾는다 |
| `docs/security-impact-design.md` 낡은 내용 | 정정 | 8절·11.3절의 "Windows 11 Home이라 가상 머신 불가", 3.4절의 추정 시간(블록 1.6시간·k=4 → 실측 56분·k=6). 사전 등록 문서라 원문을 남기고 정정 표시 |
| 가상 머신 PC 형태 프로필(`clone.ps1 -New <이름> -Spec office-low` 등, `vm.json` profiles) | **검증됨(설정값)** | 11/12 저녁. 자체 시험 27개 통과: `office-old` 복제본에 CPU 2개·코어당 50% 제한·메모리 4GB 고정·디스크 500 IOPS 가 적용되고 시작됨, 없는 프로필 이름은 아무것도 만들지 않고 거부. 게스트가 실제로 얼마나 느려지는지는 **미확인**(게스트 없음). 처음에 스위치 이름을 `-Profile` 로 했다가 PowerShell 의 자동 변수 `$PROFILE` 과 겹쳐 `-Spec` 으로 바꿈 |
| 호스트 설치 전·후 측정(E1) | 보류 | 11/12 저녁 정태웅 제기: 심각성 판단 근거가 되지 않고 가상 머신으로 환경을 조정할 수 있어 필요 없는 실험일 수 있음. 중단 제안을 `docs/capstone-plan-v2.md` "11/12 저녁 — 방향 재검토"에 적음(확정 전). 확정 전에는 호스트 측정 세션을 돌리지 않는다 |
| 가상 머신 키 입력·화면 캡처(WMI `root\virtualization\v2`: `Msvm_Keyboard.TypeKey`, `GetVirtualSystemThumbnailImage`) | **검증됨** | 11/12 밤, 일반 권한. `base.ps1 -Create` 가 시작 직후 8초 동안 Enter 를 14번 눌러 "Press any key to boot from CD or DVD"를 넘김 → 실제 평가판 ISO(26300.9457, ko-kr, Microsoft 배포 주소에서 받은 7.66GB)로 Windows 설치 화면까지 부팅. `screen.ps1 -Out` 으로 1024×768 PNG 저장, 화면에서 "설치 옵션 선택" 확인. 부작용: 남은 Enter 가 설치의 언어·키보드 화면 두 개를 기본값으로 넘겼다(한국어 기본값이라 해가 없음). 입력은 가상 머신에만 보낸다 |
| 원본 가상 머신 `pca-base` | 진행 중 | 11/12 밤 생성(4 vCPU, 8GB, `F:\pca-vm\base\base.vhdx`). Windows 설치는 사용자가 창에서 직접. 봉인 전 |
| GPU 분할(GPU-P) 가능 여부 | 미확인(목록만 확인) | `Get-VMHostPartitionableGpu` 에 내장 Radeon 과 RTX 4070 Ti SUPER 둘 다 나오고 `Add-VMGpuPartitionAdapter` 가 있다. 붙여 본 적은 없다. 게스트가 호스트와 같은 드라이버를 쓰게 되므로 "드라이버 버전 문제" 재현에는 맞지 않는다. 체크포인트와 함께 쓸 수 있는지 미확인 |
| 사용자가 겪은 실행 오류 | 원인 확인 | `C:\WINDOWS\system32` 에서 `-File lab\vm\base.ps1` 처럼 상대 경로로 실행 → "-File 매개 변수에 대한 인수가 없습니다". 안내문을 전체 경로로 고침 |

## 11/12 밤 — 호스트 측정 중단, 가상 머신 GPU

결정(정태웅, `docs/capstone-plan-v2.md` "11/12 밤 결정"): 호스트 설치 전·후 측정 중단(C1·RQ-A 제외, 호스트에 보안 프로그램 설치 안 함) · 가상 머신에 GPU와 주변기기 사용 · 가상 머신 시험도 PC를 쓰지 않는 시간에 · 유료 프로그램은 무료·평가판 또는 같은 유형으로 · 골드셋(직접 만든 사례) → 실버셋(제조사 이슈 문서 기반) → 에이전트 순서.

| 항목 | 상태 | 근거 |
|---|---|---|
| GPU 분할(GPU-P) 붙이기 | **검증됨(호스트 쪽)** | 일반 권한. `Get-VMHostPartitionableGpu` 에 내장 Radeon(VEN_1002)·RTX 4070 Ti SUPER(VEN_10DE). 내장 Radeon 분할을 OS 없는 가상 머신에 붙이고 켬 → 5초 뒤 새 프로세스에서 Running |
| GPU 분할 + 체크포인트 | **미지원** | 분할이 붙어 있으면 꺼진 상태·켜진 상태 모두 체크포인트 실패("하나 이상의 GPU 파티션이 할당되어 있어 … 작업을 수행할 수 없습니다"). 붙이기 전에 만든 체크포인트로 복원하면 분할이 사라짐. → `clone.ps1 -Save` 는 꺼진 상태에서 떼고 저장하고 다시 붙임, `-Restore` 는 복원 뒤 다시 붙임, 켜진 상태 저장은 이유를 말하고 거부. 시험 8개 통과(붙이기, 중복 거부, 저장, 시작, 켜진 상태 저장 거부, 복원 뒤 재부착, 떼기, 삭제) |
| 게스트에 호스트 드라이버 넣기(`gpu.ps1 -InstallDriver`) | **미확인** | 호스트 쪽 파일 목록만 확인: 내장 Radeon 드라이버 32.0.21042.62 는 드라이버 저장소 폴더 1개(1,081MB) + 그 밖의 파일(SysWOW64 등), 모두 141개. `Get-CimAssociatedInstance … CIM_DataFile` 은 "잘못된 매개 변수"로 실패해 `Win32_PNPSignedDriverCIMDataFile` 을 직접 읽는 방식으로 바꿈. 게스트로 복사하고 게스트에서 어댑터가 동작하는지는 게스트가 있어야 확인 가능. Microsoft 가 클라이언트 Windows 의 일반 가상 머신에 공식 지원하는 구성이 아님 |
| "복원 직후 4초간 Running" 관찰의 원인 | 정정 | Hyper-V 가 틀린 값을 준 것이 아니라, **오래 열어 둔 PowerShell 세션이 다른 프로세스가 바꾼 상태를 늦게 보여 준 것**으로 보인다(모듈의 캐시로 추정 — 같은 순간 새 프로세스에서는 맞는 값). 같은 세션에서 `Start` 뒤에도 `Off`, 떼기 뒤에도 이전 메모가 읽혔다. 시험은 새 프로세스에서 상태를 읽게 고침 |
| `vm.json` 설명 문구의 역슬래시 | 수정 | `lab\vm\gpu.ps1` 을 JSON 문자열에 그대로 써서 `ConvertFrom-Json` 이 실패(모든 스크립트가 멈춤). `/` 로 바꿈. 시험이 바로 잡아냄 |
| USB 장치 넘기기(확장 세션) | 미확인 | 호스트의 해당 정책 키가 없다(꺼짐). 켜려면 시스템 설정 변경이라 사용자 승인이 필요. 와콤·로지텍 장치가 넘어가는지 미확인 |
| Windows 설치(`pca-base`) | 진행 중 | 11/12 밤 사용자가 창에서 설치 중(화면 캡처로 "설치 중 94%" 확인) |
| Craft Anything | 확인 | 저장소 설명: 소프트웨어를 살펴보고 AI 코딩 에이전트로 자기 버전을 만드는 초기 알파 도구, MIT/Apache-2.0, Windows 용 서명된 MSI·포터블 zip. 그림·영상 도구가 아니라 개발 도구 |
| 대체 프로그램: ArtCraft 의 Craft 앱 모음 | 확인(정정) | 사용자가 뜻한 것은 `getartcraft.com/apps` 의 앱들(PhotoCraft, VectorCraft, FilmCraft, PdfCraft, AniCraft, FigCraft, WordCraft, GridCraft 등 17개, 무료 오픈소스, 런처 v0.15.0). 페이지 표기로 초기 알파 5개, 나머지는 개발 중. 위 "Craft Anything" 행은 그중 하나만 본 것 |
| 원본 Windows 설치 | 완료(봉인 전) | 11/12 02:16 사용자 화면: 바탕 화면, 계정 `lab`, Windows 11 Enterprise Evaluation(90일). 확장 세션 연결 창이 뜸 → 통합 서비스 동작 |
| 가상 머신 화면 해상도(일반 연결) | 검증됨 | `New-LabVm` 이 `Set-VMVideo 1920x1080 Single` 적용(시험용 가상 머신에서 확인). 원본은 기본값 1920x1200 |
| `base.ps1 -Prepare`(절전 끄기, `C:\lab`, 자동 로그온, 디스크 암호화·재시작 대기 보고) | **미확인** | 구문 검사만. 게스트 계정 저장(`guest.ps1 -SetCredential`) 뒤 처음 실행 |
| USB 장치 넘기기 설정 방법 | 미확인 | 레지스트리 값(`fUsbRedirectionEnableMode`)만 넣어서는 안 됐다는 보고가 있어, 문서에 나오는 길(그룹 정책 편집기 → 재부팅)로 사용자가 직접 설정하기로 함. 확장 세션으로 연결 중에는 `screen.ps1` 이 콘솔 화면(잠금 화면)만 본다 → 무인 실행은 일반 연결 + 자동 로그온으로 |

## 11/12 새벽 — 원본 봉인, 첫 복제본, 게스트에서 GPU 동작

| 항목 | 상태 | 근거 |
|---|---|---|
| USB 장치 넘기기 정책(호스트) | 설정됨 | 사용자가 그룹 정책 편집기에서 설정(한국어 Windows 에서 폴더 이름은 "원격 데스크톱 서비스"가 아니라 **"터미널 서비스"** — `ko-KR\TerminalServer.adml` 로 확인) → 02:29 재부팅 뒤 `fUsbRedirectionEnableMode = 2`, 서비스 `tsusbflt` 실행 중. 실제 장치가 게스트로 넘어가는지는 **미확인** |
| PowerShell Direct(`guest.ps1`, `base.ps1 -Prepare`) | **검증됨** | 일반 권한 창에서 동작(Hyper-V Administrators 만으로 충분). 게스트 `lab` 계정은 관리자. 명령 실행·대기 확인, 파일 복사는 드라이버 복사로 확인(1GB, 약 110초) |
| 원본 준비(`base.ps1 -Prepare`) | **검증됨** | 절전·화면 꺼짐 없음, `C:\lab`, 자동 로그온(문서화된 Winlogon 값), 자동 업데이트 정책 끔(`NoAutoUpdate = 1`), 자동 장치 암호화 방지 + C: 복호화(FullyDecrypted). 설정 앱의 업데이트 일시 중지는 11/30 까지라 정책을 따로 넣음 |
| ├ 고친 것 | 수정 | ① `-ArgumentList … [bool]$NoAutoLogon` 이 문자열로 넘어가 자동 로그온이 건너뛰어짐 → 변수에 먼저 담음 ② 셸에서 넣은 줄의 역슬래시가 사라져 정규식·레지스트리 경로가 깨짐(실행 전에 발견된 것 1, 실행 중 오류로 드러난 것 1 — 게스트에 잘못된 키는 만들어지지 않음) ③ 있는 레지스트리 키에 `New-Item -Force` 를 쓰면 값이 지워지므로 없을 때만 만들게 함 |
| 원본 봉인 | **검증됨** | 02:36 게스트 종료 → ISO 분리 → 가상 머신 삭제, 디스크 23.0GB 읽기 전용 |
| 복제본 부팅(`try1`) | **검증됨** | 차등 디스크 + **새 가상 TPM** 으로 정상 부팅, 콘솔에 `lab` 자동 로그온(화면 캡처로 바탕 화면 확인), 1920×1080, 약 40초 만에 응답 |
| 게스트에서 GPU 분할 동작 | **검증됨(장치 상태)** | 내장 Radeon 분할: 드라이버 넣기 전 `AMD Radeon(TM) Graphics` = Error(CM_PROB_FAILED_POST_START) → `gpu.ps1 -InstallDriver`(패키지 137파일 + 그 밖 55파일) + 재시작 → **OK**. 호스트 드라이버 폴더의 `.log` 하나가 사용 중이라 통째 복사가 실패 → 호스트에서 임시 폴더로 추린 뒤 보내게 고침. 실제 프로그램(Blender 등)이 이 GPU 로 그리는지는 **미확인** |
| GPU 가 붙은 복제본의 체크포인트 | **검증됨** | `clone.ps1 -Save try1 -Label gpu-ready`: 꺼진 상태에서 분할을 떼고 저장하고 다시 붙임. 복제본 크기 5.0GB |
| 게스트의 바탕 화면 세션에서 실행(`guest.ps1 -RunFile … -Desktop`) | **검증됨** | 예약 작업(로그온한 사용자, 대화형, 가장 높은 권한)으로 실행: 세션 1, 대화형, 관리자. 일반 PowerShell Direct 명령은 세션 0(바탕 화면 없음) |
| 게스트의 winget | **검증됨(주의)** | v1.6.10121 이 들어 있다. 기본 설정으로는 `0x8a15005e`(Microsoft Store 소스 인증서 불일치)로 실패 → `--source winget` 을 붙여 해결. 목록의 winget ID 17개 모두 확인(`install.ps1 -Check`) |
| 프로그램 무인 설치(`install.ps1`) | **검증됨** | `try1` 에 7-Zip 26.06(13초), Notepad++ 8.9.9(19초), Krita 5.3.4.1(37초), Blender 5.3.0(77초), ArtCraft 런처 0.19.0(MSI, 해시 확인, 6초). 설치 뒤 게스트의 설치 프로그램 목록에서 확인하고 `installed.json` 에 기록 |
| GPU 분할로 프로그램 화면 그리기 | **검증됨(화면)** | Blender 5.3.0 을 게스트 바탕 화면에서 실행 → 30초 뒤 살아 있고 창 제목 정상, 화면 캡처에 3D 뷰포트와 시작 창이 그려짐. 가만히 있을 때의 GPU 사용률 카운터는 0 이라 어느 어댑터가 그렸는지는 따로 확인하지 못함 |
| 무인 세션(`session.ps1`) | **검증됨(작은 계획)** | `plans\smoke.json`: 2코어·4GB 복제본 생성 → 시작 → 두 프로그램 설치 → 종료 → 체크포인트까지 1.7분, 끝난 뒤 켜진 가상 머신 없음. `plans\bundles.json`(사무·개발·디자인·영상 4개)은 미리 보기만 함 — **실제 실행 전** (→ 03:24 실행, 아래 "첫 무인 세션" 절) |
| DaVinci Resolve 21.2 무인 설치 | **미확인** | 사용자가 받은 zip(Blackmagic 배포 주소, 3.5GB) 안에 설치 파일 1개. 조용한 설치 스위치(`/i /q /noreboot`)는 기억에 의존한 값이라 창이 뜰 수 있다 — 그 경우 시간 초과로 끝나고 화면이 남는다 (→ 04:08 설치 확인, 아래 절) |
| ArtCraft 앱(PhotoCraft 등) 개별 설치 | **미확인** | 런처만 설치. 앱은 런처 안에서 설치해야 하고 자동화하지 않았다 |
| 셸 치환(perl·sed)으로 파일 고치기 | 금지(이 저장소) | 이번 밤에만 역슬래시·따옴표가 깨져 JSON 2번, 스크립트 2번이 망가졌다(모두 시험에서 잡힘). 경로·따옴표가 든 줄은 편집 도구나 따옴표 친 heredoc 으로만 넣는다 |
| ArtCraft 의 Craft 앱 19개 | **검증됨(실행)** | 11/12 03시경 사용자가 호스트 `F:\ArtCraft` 에 런처로 설치(앱마다 실행 파일 몇 개짜리 폴더, 합계 약 3.4GB). 폴더를 게스트 `C:\lab\apps\<id>` 로 복사하는 방식으로 목록에 추가(`apps.json` 의 `portable`, `vm.json` 의 `artcraft_dir`) → 19개 복사 241초, **19개 모두 게스트에서 창이 뜨고 20초 뒤에도 살아 있음**(GPU 분할이 붙은 복제본, 크래시 이벤트 0). 버전은 실행 파일에서 읽어 `installed.json` 에 기록(PhotoCraft 0.22.0, VectorCraft 0.23.0, WordCraft 0.17.0 등). 복사본이라 시작 메뉴 항목과 설치 프로그램 목록 항목은 없다. GPU 없는 복제본에서 뜨는지는 미확인 |
| 묶음 갱신 | 완료 | `office` 에 WordCraft·DeckCraft·GridCraft·PdfCraft, `design` 에 PhotoCraft·VectorCraft·DesignCraft·FigCraft·AniCraft, `video` 에 FilmCraft·EffectCraft·SoundCraft, 새 묶음 `cad`·`artcraft`(전체). ArtCraft 런처는 묶음에서 뺌 |

## 11/12 첫 무인 세션 — 묶음 4개 설치 (03:24~04:08, 사용자가 일반 권한 창에서 실행)

`session.ps1 -Plan plans\bundles.json -Until 09:30`. 기록: `F:\pca-vm\sessions\20261112-032418\`. 위 표의 "실제 실행 전"·"미확인" 항목 중 아래가 바뀌었다.

| 항목 | 상태 | 근거 |
|---|---|---|
| 무인 세션(`session.ps1`), 작업 4개 | **검증됨** | 44분 만에 끝남(office1 19.5분, dev1 3.7분, design1 8.6분, video1 12분), 끝난 뒤 켜진 가상 머신 없음. 체크포인트 `bundle-office`·`bundle-dev`·`bundle-design`·`bundle-video`. GPU 작업 2개(design1, video1)는 분할 붙이기 → 드라이버 복사 → 재시작 → 설치 → 분할 떼고 저장 → 다시 붙이기까지 사람 손 없이 됨. 일반 권한 창으로 충분(관리자 창 불필요) |
| 설치된 것(34개 항목 전부) | **검증됨** | office1(2코어·4GB): 7-Zip 26.06, Notepad++ 8.9.9, Chrome 157.0.8103.51, Firefox 160.0, LibreOffice 26.8.2.2(825초 — 이 묶음 시간의 대부분), 카카오톡 26.9.2.5342, VLC 3.0.24, WordCraft 0.17.0, DeckCraft 0.18.0, GridCraft 0.19.0, PdfCraft 0.21.0 / dev1: Git 2.56.0.4, VS Code 1.145.2, Godot 4.7.2, AutoHotkey 2.0.31 외 / design1: Krita 5.3.4.1, GIMP 3.2.6, Inkscape 1.4.4, Blender 5.3.0, Craft 앱 5개 / video1(8코어·16GB): VLC, OBS Studio 33.0.0, FilmCraft 0.20.0, EffectCraft 0.24.0, SoundCraft 0.19.0, DaVinci Resolve 21.2.00006 |
| DaVinci Resolve 21.2 무인 설치 | **검증됨(설치)** | `/i /q /noreboot` 로 창 없이 끝남: 서명 확인(Blackmagic Design Pty Ltd) → 3.5GB 복사 → 설치, 합계 370초, 종료 코드 0, 설치 프로그램 목록에 `DaVinci Resolve 21.2.00006`, 바탕 화면에 아이콘. **실행되는지(창이 뜨는지, 내장 GPU 분할로 충분한지)는 미확인** |
| 카카오톡 "설치 실패" 보고 | **거짓 실패 — 수정** | 세션은 `not installed: kakaotalk` 로 보고했지만 실제로는 설치돼 있었다(`C:\Program Files\Kakao\KakaoTalk\KakaoTalk.exe` 26.9.2.5342, winget 도 성공). 원인: 한국어 Windows 에서 설치 프로그램 목록의 이름이 한글 "카카오톡"이라 영문 이름으로 찾지 못함. `install.ps1` 이 표시 이름에서 못 찾으면 **레지스트리 키 이름**(`KakaoTalk`)으로 찾게 고침 → 재확인 통과. 세션의 `summary.json` 은 그때 기록 그대로 둠(office1 "done with failures"), `installed.json` 은 바로잡음(`recheck_note`) |
| ├ 같이 고친 것 | 수정 | ① 이미 설치된 프로그램을 다시 확인했을 때 `installed.json` 의 실패 기록이 그대로 남던 것 → 게스트에 있으면 기록을 고친다 ② Windows PowerShell 5.1 이 JSON 배열을 한 덩어리로 넘겨 기존 기록의 항목 비교가 목록 전체를 대상으로 돌던 것 → 풀어서 읽음 ③ `found` 값에 원격 실행 속성(`PSComputerName` 등)이 딸려 저장되던 것 → 글자만 저장(이전 기록은 다음에 쓸 때 정리됨) |
| GPU 없는 복제본에서 Craft 앱 | **검증됨(4개)** | office1(GPU 분할 없음, 2코어·4GB)에서 WordCraft·DeckCraft·GridCraft·PdfCraft 모두 창이 뜨고 20초 뒤에도 살아 있음(87~203MB, 크래시 이벤트 0, 화면 `F:\pca-vm\shots\office1-craft-nogpu.png`). 그림·영상·CAD 쪽 앱은 GPU 없는 복제본에서 미확인 |
| 프로그램이 스스로 버전을 올림 | 확인(주의) | 설치 8시간 뒤 office1 을 다시 켜자 몇 분 안에 Chrome 이 157.0.8103.51 → .52 로 바뀌어 있었고, GridCraft 는 0.19.1 업데이트 창을 띄웠다(체크포인트로 되돌려 저장 상태는 .51/0.19.0 그대로). **`installed.json` 의 버전은 설치 시점 값**이다 — 사례를 만들 때는 그 시점의 버전을 게스트에서 다시 읽어 적어야 하고, 버전을 고정해야 하는 사례는 네트워크를 끊거나 자동 업데이트를 꺼야 한다(방법 미정) |
| 디스크 | 확인 | 복제본 크기: office1 11.0GB, dev1 7.5GB, design1 13.2GB, video1 27.5GB, try1 12.8GB. F: 남은 공간 1,269GB |
| 확인 과정에서 가상 머신을 켠 것 | 기록 | 11/12 11:44~11:50 에 office1(2코어·4GB)만 세 번 켜서 카카오톡·Craft 앱을 확인하고 매번 `bundle-office` 로 되돌림. video1 은 켜지 않음("PC 를 쓰지 않는 시간에만" 규칙 — DaVinci Resolve 실행 확인은 다음 무인 세션으로) |

## 11/12 낮 — 게스트 안 사례 만들기 (`lab/vm/case.ps1`, 수집기 콘솔판을 게스트에서 실행)

수집기 코드는 바꾸지 않았다. 게스트에서 프로그램을 켜고 끄고 문제를 넣는 것은 모두 `lab/vm/`(실험 코드)의 일이다.

| 항목 | 상태 | 근거 |
|---|---|---|
| 게스트에 넣을 빌드(`payload.ps1`) | **검증됨** | 수집기 콘솔판 + FaultLab(자체 포함 빌드) + FaultHost.exe·FaultPlugin.dll → `F:\pca-vm\payload\case-payload.zip` 102.8MB, 커밋 `e1bb54f` 기준. 게스트로 복사·압축 풀기 약 30초 |
| 게스트에서 수집기 실행(관리자, 바탕 화면 세션) | **검증됨** | office1(2코어·4GB): 소스 상태 `eventlog=ok, sensors=ok, processes=ok, resources=ok, etw-kernel=ok, hotkey=ok`. 표준 입력으로 `m`·`q` 를 보내 마킹·종료(종료 코드 0). **센서는 게스트에서 미지원**: 소스 상태는 ok 지만 값이 하나도 없다(`sensors: []`, 0 으로 읽힌 6개는 "없음" 처리, 엔진 스냅샷 `sensor_support: false`) → 온도·전압·팬이 근거가 되는 사례는 가상 머신으로 만들 수 없다 |
| 시나리오 `crash-plugin`(서드파티 DLL 크래시) | **검증됨** | 12:00 office1: FaultHost.exe 가 FaultPlugin.dll 에서 `c0000005` → 수집기가 이벤트 1000 을 받아 30초 뒤 자동 덤프(`crash-1000`, 이벤트 207개, 파일 4개), 1001 후속 보고는 중복으로 억제. 유의성 10·`hidden`(시험용 프로그램 감점 −100 — 설계대로). 전체 3분 10초, 끝난 뒤 저장 지점으로 복원 |
| 시나리오 `baseline`(문제 없음, 실행 확인) | **검증됨(office1)** | 12:06 무인 세션 경로(`session.ps1` → `case.ps1`)로 실행, 9.8분: 설치된 11개(7-Zip, Notepad++, Chrome, Firefox, LibreOffice, VLC, WordCraft, DeckCraft, GridCraft, PdfCraft, 카카오톡)를 하나씩 켜서 25초 뒤 **모두 살아 있고 창이 있음**, 크래시 이벤트 0, 끝에 수동 마킹 → 덤프 `manual`(이벤트 990개). 시작 메뉴 바로 가기에서 실행 파일을 찾는 방식(카카오톡만 경로 지정). 프로그램은 관리자 권한으로 켜진다(창 제목에 `[Administrator]`) — 일반 사용자의 실행과 다를 수 있음 |
| 시나리오 `mem-pressure`(메모리 부족 + 사용자가 "느림" 마킹) | **검증됨(실행)** | 12:16 office1(4GB), Chrome·LibreOffice·카카오톡을 연 채로 FaultLab 이 3,500MB 를 약 8초에 점유 → 60초 뒤 `m slow` → 덤프 `manual`(이벤트 850개) → 100초 유지 뒤 정상 종료. 열어 둔 세 프로그램은 끝까지 살아 있고 응답함, 크래시 이벤트 0. 전체 6분. 덤프 안 신호는 "11/12 저녁" 절에서 확인(커밋 52→87%, 범인은 `processes` 의 `privMB`) |
| 결과물 | 확인 | `F:\pca-vm\cases\<복제본>-<시나리오>\<시각>\`: `result.json`(시간표·프로그램 실행 결과·크래시 이벤트·덤프 목록·그 시점 설치 버전), `scenario.json`(넣은 문제 = 정답, `confidence: "injected"`), `dumps\`, `collector.log`, `shots\`(30초마다 화면) |
| 정답 표기 | 제안(미합의) | 넣은 문제라 정답을 안다 → `injected`. `docs/conflict-test-plan.md` 의 official/user/guess 와 따로 센다. `cause_category` 낱말(`third_party_module`, `memory_pressure` 등)도 임시 — 엔진 분류와의 대응은 미정 |

| 첫 사례 세션 계획(`plans\cases.json`, 22개 작업) | **실행 전** | 복제본 4개의 `baseline` → 문제 주입 10개(office1 5, dev1 1, design1 2, video1 2) → 느린 사무용 PC 형태 `office2`(`office-old`) 설치와 사례 3개. office1 의 `baseline`·`mem-pressure` 는 위 시험으로 이미 만들어져 건너뛴다. 예상 3~4시간(추정). 시험에 쓴 첫 `crash-plugin` 실행은 `F:\pca-vm\cases\_smoke\` 로 옮김(준비 시간 25초·연 프로그램이 달라 계획의 것과 다름) |
| 첫 사례 세션 중간 결과(12:28 시작, 13:08 확인 — 아직 도는 중) | 확인(중간) | 끝난 8개 모두 `ok`: `baseline` video1·design1·dev1, office1 `crash-plugin`·`hang`(덤프 `hang-1001`)·`cpu-hog`. **DaVinci Resolve 실행됨**(창 "Welcome to DaVinci Resolve"), OBS·Krita·Inkscape·Blender 창 확인, GIMP 는 25초 안에 창이 안 뜸(살아는 있음) |
| GPU 분할 복제본의 `LiveKernelEvent 0x193` | **실제 발견(원인 미확인)** | design1·video1 의 `baseline` 에서 각 2번, 수집기가 `kernel-live`(유의성 100, `major`)로 자동 덤프. 넣은 문제가 아니다. 0x193 은 그래픽 커널(dxgkrnl) 라이브 덤프 계열로 알고 있으나 **코드의 정확한 뜻과 원인은 미확인** — GPU 분할(지원 밖 구성)과 관련됐을 가능성이 있다는 것까지만. 같은 시간대 호스트에는 LiveKernelEvent·디스플레이 드라이버 이벤트 없음, 호스트 NVIDIA 드라이버 621.14 정상(`nvidia-smi` 응답) |
| 시험하지 않은 시나리오 | → 아래 "11/12 저녁" 절 | 첫 세션에서 모두 한 번씩 돌았다. 결과와 무효 3건은 아래 절 |

**이 절까지 합쳐 미확인으로 남은 것:** 그림·영상·CAD Craft 앱의 GPU 없는 실행 · `peripheral` 묶음(Logitech G HUB) · USB 장치 넘기기 · 버전 고정 방법. (설치된 프로그램의 실행은 첫 세션 `baseline` 으로 확인 — 아래 절)

## 11/12 저녁 — 첫 사례 세션 검토(12:28~16:36)와 도구 수정

세션 결과(콘솔): 건너뜀 6 · 완료 14 · 실패 섞인 완료 1(office2 설치) · 실패 1(office2 `baseline` 시간 초과). 사례 폴더 17개를 덤프까지 열어 본 결과 **쓸 수 있는 사례 13개, "성공"으로 표시됐지만 덤프에 문제가 담기지 않은 것 3개**(→ `F:\pca-vm\cases\_invalid\` 로 옮기고 이유를 `INVALID.txt` 에 적음), 결과 없음 1개.

### 사례별 상태

| 항목 | 상태 | 근거 |
|---|---|---|
| `baseline` — 설치된 프로그램 실행 | **검증됨(4개 복제본)** | office1 11개·dev1 5개(7-Zip, Notepad++, VS Code, Godot, AutoHotkey)·design1 10개·video1 7개 모두 프로세스가 살아 있음. 창까지 확인: DaVinci Resolve·OBS·Krita·Inkscape·Blender·VLC·Craft 앱 전부. **GIMP 만 25초 안에 창이 없음**(프로세스는 살아 있음 — 첫 실행 준비가 긴 것으로 보이나 미확인). 크래시 이벤트 0 |
| `crash-dotnet` (dev1) | **검증됨** | FaultLab 처리 안 된 예외 → 덤프 `crash-1000`, 엔진 스냅샷 `symptom=app_crash, target=FaultLab.exe, faulting_modules=[KERNELBASE.dll]`. 유의성 10·`hidden`(시험용 프로그램 감점) |
| `crash-plugin` (office1) | **검증됨** | 덤프 `crash-1000`, `target=FaultHost.exe, faulting_modules=[FaultPlugin.dll]`, 태그 `third-party-module` |
| `crash-plugin` (design1) | **무효** | FaultHost.exe 가 `0xC0000135`(DLL 없음)로 시작도 못 함 — design1 에는 VC++ 재배포 패키지가 없고 FaultHost 는 `/MD` 빌드라 `VCRUNTIME140.dll` 이 필요(`dumpbin /dependents` 로 확인). office1 은 LibreOffice 가 재배포 패키지를 깔아 줘서 우연히 됐다. 그런데도 `ok` 였던 이유: GPU 복제본이 스스로 남긴 `kernel-live` 덤프가 "덤프 있음"으로 세어졌다 |
| `hang` (office1) | **검증됨** | `WerReportHang` → 덤프 `hang-1001`, `symptom=freeze, target=FaultLab.exe` |
| `cpu-hog` (office1, video1) | **검증됨(원시 이벤트)** | `resources` 이벤트에 CPU 합계 99.9~100%, `topCpu` 1위 `faultlab 99.1%` — 범인이 이름으로 남는다 |
| `disk-hog` (office1) | **검증됨(원시 이벤트)** | `topCpu` 의 `faultlab io 1121`(프로세스 I/O), `pagesPerSec` 13만대(평소 수십~수천). CPU 합계는 26% — **디스크가 원인인 느려짐은 CPU 로는 안 보인다** |
| `mem-pressure` (office1, design1, video1) | **검증됨(원시 이벤트)** | 커밋 52→87%(office1), 34→96%(design1), 20→93%(video1), 사용 가능 메모리 최저 264~561MB. 범인은 `processes` 이벤트의 `privMB`(design1: FaultLab 7,104MB)로 남는다. `resources.topCpu` 에는 안 나온다(CPU 상위 목록이라 메모리만 쥔 프로세스는 빠짐) |
| `disk-hog`·`mem-pressure` (office2, `office-old`) | **무효** | 느리게 만든 복제본에서 FaultLab(자체 포함 .NET) **읽어 들이는 데만 82초·34초**. 마킹은 "프로세스를 켠 뒤 45·60초"였고 덤프 창은 마킹+30초에 닫혀서, 부하가 시작(`LOAD_AT`)되기도 전에 덤프가 끝났다(disk: `LOAD_AT` 16:02:10 vs 덤프 창 끝 16:01:59). 덤프 속 FaultLab 은 CPU 0.1%·I/O 0. Chrome 을 "열어 둠"으로 적었지만 office2 에 Chrome 이 없었다 |
| office2 `baseline` | 실패(시간 초과) | 14분 한도. 꾸러미 복사·압축 풀기와 프로그램 닫기가 느려 4번째 프로그램에서 끝남 |

### 덤프를 열어 보고 알게 된 것 (수집기 쪽)

| 항목 | 상태 | 근거 |
|---|---|---|
| 짧게 돈 범인은 엔진 스냅샷 추세에 안 나온다 | **확인(설계 한계)** | `engine-snapshot.json` 의 `resource_trends.top_cpu_60`·`top_memory_growers` 가 13개 사례 전부 빈 배열. `MinuteRollup` 이 프로세스당 **1분 표본 10개 이상**일 때만 행을 만든다(`if (list.Count < 10) continue`) — 수집기를 5분 돌리고 범인이 100초 사는 사례에서는 원리상 비어 있다. 범인은 `events.json` 의 원시 `resources`/`processes` 에만 있다. 엔진이 `engine-snapshot.json` 만 읽는다면 느려짐 사례의 근거가 없다 → 긴 시나리오 2개(`cpu-hog-long`, `mem-leak-slow`)를 추가해 다음 세션에서 추세가 범인을 짚는지 본다 |
| `commit_pct_now` 가 "지금"이 아니다 | **확인** | 분 단위 롤업의 마지막 값이라 최대 1분 늦다. design1 `mem-pressure`: 마킹 시점 원시 값 96.4%, 엔진 스냅샷 33.56%(문제 시작 직전 값). office1·video1 은 우연히 맞았다(86.7%, 93.0%) |
| 디스크 수준 신호 없음 | **미지원 → 같은 날 밤 추가**(아래 "11/12 밤" 절) | 디스크 대기열·사용률·응답 시간을 수집하지 않는다. `office-old`(500 IOPS 제한)에서는 CPU 2~8%인데 `pagesPerSec` 가 계속 약 1,000 으로 평평 — 디스크가 한도에 붙어 있다는 간접 표시뿐. 사무용 느린 PC 가 대상이라면 필요한 신호 → 스파이크 후보(`PhysicalDisk` 카운터, 10/28 느려짐 스파이크에서 본 것과 같은 계열) |
| WER 1001 의 P4 를 오류 모듈로 읽던 오류 | **수정·검증됨** | `LiveKernelEvent 0x193` → `faulting_modules=["aaaaaaaaaaaaaaaa"]`(덤프 매개변수), `AppHangB1` → `["2cd5"]`(응답 없음 서명)이 엔진 스냅샷에 들어갔고 유의성에 `unknownModule +10` 이 붙었다. `TriggerClassifier.WerP4IsModule` 추가 — 라이브 덤프·응답 없음 계열은 모듈 없음으로. 단위 테스트 91개 통과(7개 추가). **같은 날 밤 범위를 넓힘**: 크래시 계열(APPCRASH·BEX 등)에서만 모듈로 읽는다(아래 "11/12 밤" 절) |
| `LiveKernelEvent 0x193` 의 뜻 | **확인(문서)** | Microsoft Learn "Bug Check 0x193 VIDEO_DXGKRNL_LIVEDUMP": 그래픽 커널(dxgkrnl)이 남기는 라이브 덤프 식별 코드, 실제 블루스크린 코드로는 쓰이지 않음, 매개변수 1 = 이유 코드. 이번 기록은 매개변수 4개가 모두 `0xaaaaaaaaaaaaaaaa` — 문서에 없는 값(미확인) |
| GPU 분할 복제본의 0x193 | **재현됨(원인 미확인)** | design1·video1 에서 돈 6개 실행 **전부**에서 발생(총 9건), GPU 없는 복제본(office1·dev1·office2) 0건. 매번 수집기 시작 후 **111~115초**에 첫 건(준비 90초 + 첫 프로그램 직후), 일부는 139~221초에 한 번 더. 프로그램 종류와 무관해 보이나, 프로그램을 하나도 안 켜도 나는지는 모름 → `idle` 시나리오 추가. 수집기는 미분류 라이브 덤프 코드를 `freeze`·기본 90점으로 매겨 `major` 가 된다 — 0x193 을 GPU 계열(141·117 과 같은 묶음)로 볼지는 미정 |
| 경로 치환 부작용 | 참고 | 게스트 사용자 이름이 `lab` 이라 `C:\lab\faultlab\...` 이 `C:\%USERNAME%\faultlab\...` 로 덤프됨(경로 조각 단위 치환 — 설계대로). 사용자 이름과 같은 폴더 이름이 있으면 경로가 달라 보인다 |
| 느린 PC 에서 마킹 순간 비용 | 참고 | office2: 스냅샷 수집 82.7초(`captureDurationMs`), 평소 수 초 |

### 설치 (office2, `office-old`: 2코어 50% 제한·4GB·500 IOPS)

| 항목 | 상태 | 근거 |
|---|---|---|
| 묶음 설치 101분 | 확인 | office1(제한 없음) 19.5분의 5배. Notepad++ 466초, Firefox 373초, VLC 736초 |
| "시간 초과" 3건은 잘못된 기록 | **확인 → 수정** | 7-Zip·LibreOffice·카카오톡이 20분 한도에 걸려 실패로 적혔지만, 그 뒤 사례 실행 때 읽은 설치 목록에는 셋 다 있다(7-Zip 26.06, LibreOffice 26.8.2.2, 카카오톡 26.9.2.5342). 한도는 우리 대기를 끝낼 뿐 설치를 멈추지 않는다 |
| Chrome 설치 실패 | 확인(원인: 외부) | `Installer hash does not match` — winget 목록의 해시와 Google 이 올려 둔 설치 파일이 다름(목록이 늦게 갱신된 것으로 보임). 관리자 권한에서는 무시 불가. 다른 복제본은 새벽에 정상 설치 → 다시 시도하면 될 수 있음 |

### 도구 수정 (가상 머신 없이 확인한 범위를 구분해 적음)

| 항목 | 상태 | 근거 |
|---|---|---|
| 마킹 시점: 부하가 실제로 시작된 뒤 | **수정, 부분 검증** | `guest-case.ps1` 이 FaultLab 의 표식 줄(`LOAD_AT`, 누수는 `LOAD_READY_AT` = 점유 완료)을 기다린 뒤 `after_sec` 후 마킹. 호스트에서 FaultLab 을 돌려 "실행 중인 프로그램의 로그에서 표식 읽기"는 확인. 게스트 전체 흐름은 다음 세션에서 |
| "성공" 조건 강화 | **수정, 미검증(게스트)** | 시나리오가 기대한 종류의 덤프(`expect_dump_kind`: crash·hang·manual)가 있어야 하고, FaultLab 종료 코드가 성공 값이어야 하며(`ok_exit`), 표식이 안 나오면 실패. 다른 트리거의 덤프(GPU 복제본의 `kernel-live`)는 세지 않고, 그 덤프 때문에 대기가 일찍 끝나지도 않는다. 안 켜진 배경 프로그램은 `warnings` 로 남고 `scenario.json` 의 `open` 에는 실제로 돈 것만 적는다 |
| 느린 프로필 시간 늘리기 | **수정, 미검증(게스트)** | CPU 제한이나 IOPS 제한이 걸린 복제본은 대기·한도를 3배(`scenarios.json` `slow_factor`). 설치 한도도 3배, 한도에 걸린 뒤 4분(×3) 동안 다시 찾아보고, 설치 끝에 실패 항목을 한 번 더 확인(`[ok*]`) |
| FaultHost 의 DLL 의존 | **수정, 부분 검증** | 이 PC 에 Windows SDK 가 없어(`windows.h` 없음 — 포맷 뒤 미설치) `/MT` 재빌드 불가. 대신 `payload.ps1` 이 Microsoft 서명이 유효한 x64 `vcruntime140.dll`(Visual Studio 설치 폴더, 14.51.36260.0)을 FaultHost 옆에 넣는다. 호스트에서 꾸러미의 `plugin` 모드 → `FAULTHOST_EXIT=0xc0000005` 확인. 재배포 패키지 없는 게스트(design1·dev1)에서의 확인은 다음 세션. `plugin/CMakeLists.txt` 는 정적 CRT 로 바꿔 둠(SDK 설치 후 재빌드하면 DLL 동봉이 필요 없어짐) |
| FaultLab | **수정·검증됨(호스트)** | `plugin` 모드: FaultHost 가 `0xC0000005` 로 끝나지 않으면 종료 코드 2(실패 경로는 실제로 밟아 보지 못함 — 이 PC 에는 런타임이 있음). `leak`: `LOAD_READY_AT` 표식, 세 번째 인자 `간격ms`(느린 누수). 출력 UTF-8 고정(게스트 로그의 한글 깨짐) |
| `scenario.json` 의 `profile` 이 전부 `standard` | **수정** | `case.ps1` 이 VM 객체 대신 이름을 넘기던 오류. 남긴 사례 13개의 값은 VM 메모에서 읽어 고쳐 씀(`profile_corrected` 표시). 이제 CPU 제한·IOPS 제한·`slow_factor`·`usable` 도 기록 |
| 새 시나리오 | 추가(미실행) | `idle`(아무것도 안 켜고 5분 — GPU 복제본의 0x193 확인용), `cpu-hog-long`(13분, 11분째 마킹), `mem-leak-slow`(14초마다 50MB, 메모리의 60%까지) |
| 다음 세션 계획 `plans\cases-2.json` | **실행 전** | 15개 작업, 약 4.5시간(추정): 고친 도구 확인 5개 → `idle` 3개 → 긴 시나리오 2개(office1) → office2 재설치(`bundle-office` 에서 이어서 `bundle-office-b` 로 저장)와 사례 4개. `-DryRun` 통과 |

**미확인으로 남은 것:** GPU 분할 복제본에서 0x193 이 나는 원인(게스트 안 라이브 덤프 파일 `C:\WINDOWS\LiveKernelReports\WATCHDOG\*.dmp` 는 열어 보지 않음) · 0x193 을 사용자가 체감하는지 · GIMP 창이 늦는 이유 · Chrome 해시 불일치가 일시적인지 · WER 1001 의 다른 유형(CLR20r3, MoAppCrash 등)에서 P4 의 뜻 · 긴 시나리오에서 추세가 범인을 짚는지.

## 11/12 밤 — 엔진 입력 보강(`resource_recent`), 디스크 신호, 새로 켜진 프로세스의 CPU (정태웅 결정: "추천대로 진행")

결정 5건 중 진행한 것: ① 수집기 요약에 "마킹 직전 구간" 항목 추가 + 엔진이 읽게 함, ② 디스크 신호 추가, ④ Windows SDK 설치됨(정태웅) → 엔진·FaultHost 재빌드, ⑤ `injected` 확정(`docs/conflict-test-plan.md` 3.2). ③ 0x193 분류는 다음 세션의 `idle` 결과를 본 뒤.

### 기준선 — 고치기 전 엔진이 오늘 사례를 어떻게 읽었나

| 항목 | 상태 | 근거 |
|---|---|---|
| 느려짐 6건(메모리 3·CPU 2·디스크 1) | **범인 0건** | 1순위가 `0x80040827`(아래) 또는 "최근 설치된 프로그램"·상주 필터 드라이버. FaultLab 은 후보에도 없음 |
| `0x80040827` 이 1순위 "오류 모듈" | **수집기 오류** | Edge 업데이트의 `crashpad_log` 보고(WER 1001, 트리거 아님)에서 P4 = 오류 코드가 `faultingModule` 로 실려 엔진이 "크래시의 오류 모듈"로 읽었다. `F:\pca-vm\cases` 아래 모든 덤프의 1001 기록 53건(같은 기록이 여러 덤프에 겹쳐 들어간 것 포함): `crashpad_log` 30(P4 = 0x80040827), `LiveKernelEvent` 19(aaaaaaaaaaaaaaaa), `APPCRASH` 3(FaultPlugin.dll·KERNELBASE.dll — 모듈 맞음), `AppHangB1` 1(2cd5) |
| 크래시·멈춤 3건 | 1순위 1건(서드파티 DLL), 3위 안 2건 | `crash-dotnet`·`hang` 은 FaultLab.exe 가 3위 안이지만 "최근 설치" 후보에 밀린다 — 프로그램 자체의 버그는 엔진이 찾는 "충돌 상대"가 아니어서다(이번에 손대지 않음) |

### 스파이크 (읽기 전용, 호스트)

| 항목 | 상태 | 근거 |
|---|---|---|
| K — `PhysicalDisk` 카운터 (`collector/spikes/K-DiskCounters`) | **검증됨** | ko-KR 에서 영어 범주·카운터 이름 동작, 인스턴스 `"1 C:"` 형식 + `_Total`, 디스크 4개 읽기 1ms. `% Idle Time`·`Avg. Disk Queue Length`·`Avg. Disk sec/Transfer`·`Disk Transfers/sec`·`Disk Bytes/sec` 값이 1초 간격 두 번째 표본부터 나옴. `_Total` 은 디스크 평균이라 한 디스크만 바쁜 경우를 가린다 → 디스크별로 수집 |
| L — 새로 켜진 프로세스의 CPU (`collector/spikes/L-NewProcessCpu`) | **검증됨** | `Process` 범주의 `% Processor Time` 원시값 = 시작 이후 누적 CPU 시간(100ns): 2스레드 약 3초 → 5.97 CPU초. `IO Data Bytes/sec` 원시값 = 누적 바이트: 64MB 쓰기 → 64MB. 직전 읽기에 그 PID 가 없으면 새 프로세스. `Elapsed Time` 은 한 표본으로 계산되지 않음(0) — 쓰지 않는다 |

### 수집기 변경

| 항목 | 상태 | 근거 |
|---|---|---|
| `resources` 이벤트에 `disk` (디스크별 바쁨 %, 대기열, 응답 ms, IOPS, MB/s) | **검증됨(호스트)** / 게스트 미확인 | 10초마다, 첫 표본은 `warming`, 5분마다 디스크 다시 열거. 호스트 2.5분 실행에서 값 확인(유휴 0.6%). 실패 시 `{"status":"unavailable"}`. **포화된 디스크에서의 값과 가상 머신 IOPS 제한이 이 값에 나타나는지는 미확인**(다음 세션 `disk-hog`) |
| 새로 켜진 프로세스가 첫 CPU 측정부터 잡힘 | **검증됨(호스트)** | 전에는 직전 읽기에도 있던 인스턴스만 차분해서, 방금 켠 프로세스는 다음 측정(30~60초 뒤)에야 나왔다 — video1 `cpu-hog`: PC 전체 88.9% 인데 상위 목록에 범인 없음. 이제 PID 로 짝을 맞추고 새 PID 는 누적값으로 계산. 호스트: 4스레드(16 중) 부하를 측정 15초 전에 켬 → 첫 측정 12.3%(기대 12.5%), 다음 24.8%(기대 25%). 끝난 프로세스의 마지막 구간은 여전히 못 잰다 |
| `resources` 이벤트에 `cpuAt` | 추가 | CPU·상위 목록을 실제로 잰 시각(30초마다). 사이 표본은 같은 값을 다시 싣기 때문에 요약할 때 중복을 거르는 열쇠 |
| `engine-snapshot.json` 에 `resource_recent` (`Core/RecentResources.cs`) | **검증됨(단위 테스트 + 실제 덤프 재생 + 호스트 실행)** | 덤프 창의 `resources`·`processes` 이벤트를 간추림: 커밋(마지막·최대)·사용 가능 메모리·페이지, CPU 합계, CPU 상위 5(평균·최대·횟수), 입출력 상위 3, 메모리 상위 5(창 안 증가량·창 안에서 켜졌는지), 디스크별 요약. 정렬·평균만 한다(판단 없음). `BlackBox --resource-recent <events.json>` 으로 예전 덤프에서 다시 계산 가능 |
| WER 1001 의 P4 는 크래시 계열에서만 모듈 | **수정·검증됨** | `WerP4IsModule` 을 크래시 접두(APPCRASH·CLR20r3·BEX·MoAppCrash·MoBEX)만 참으로. 비크래시 보고·응답 없음·라이브 덤프·미분류 유형은 모듈 없음. CLR20r3·MoAppCrash 의 P4 뜻은 여전히 미확인(기존 동작 유지) |
| 콘솔 입력의 BOM | 확인·보강 | 입력 인코딩이 UTF-8 인 부모(이 PC 의 개발 셸)는 프로세스를 시작하는 순간 BOM 을 파이프로 보낸다 → 첫 명령 `m slow` 가 안 읽힘. 게스트는 한글 코드 페이지라 우연히 괜찮았다. `guest-case.ps1` 은 빈 줄을 먼저 보내고 ASCII 로 쓴다. 수집기는 U+FEFF 를 떼어 낸다(이쪽 코드 페이지가 다르면 못 고침) |
| 단위 테스트 | **104개 통과** | `RecentResourcesTests` 10개, `WerP4IsModule` 10개 |

### 엔진 변경 (`engine/` — 송민석 영역, 커밋 시 저자 확인 필요)

| 항목 | 상태 | 근거 |
|---|---|---|
| 새 도구로 빌드 | **검증됨** | Visual Studio 18 + Windows SDK 10.0.26100 + CMake 4.3.1. `engine\build` 를 다시 구성(전 캐시는 VS 2022 용), 기존 테스트 통과 |
| `resource_recent` 읽기 + 규칙 3개(`addFromResourceRecent`) | **검증됨(테스트 + 재생)** / 기준값은 **잠정** | 짧은 CPU 점유(최대 ≥50%, 2회 이상, 평균 ≥15%) · 메모리 압박(커밋 ≥85% + 한 프로세스가 한도의 30% 이상) · 디스크 포화(가장 바쁜 디스크 평균 ≥80% + 입출력 1위). 근거와 약한 점은 `engine/docs/rules_rationale.md` 9절. 디스크 규칙은 **실측 근거가 없다** |
| "근거 부족" 경고 | 수정 | 기여도 0 인 근거("센서 값을 읽을 수 없어 확인 불가")가 근거로 세어져 센서 없는 PC 에서 경고가 사라지던 것. 디스크가 계속 바빴으면 안내 문장 추가 |
| 엔진 테스트 | **170개 통과** | s14(CPU)·s15(메모리)·s16(디스크) 추가, 값은 오늘 사례의 실측 |

### 오늘 사례 13개 재생 (가상 머신 없이: 저장된 `events.json` → 새 요약 → 새 엔진)

| 사례 | 전 | 후 | 비고 |
|---|---|---|---|
| `mem-pressure` office1·design1·video1 | 없음 | **1순위**(12~13%, 다음 후보 5~6%) | "커밋 96.3% 상태에서 이 프로세스가 7104.1MB(한도의 63.8%)" |
| `cpu-hog` office1 | 없음 | **1순위**(7%, 2위와 같은 7%) | 간신히 — `other_sw` 사전 가중치가 낮다 |
| `cpu-hog` video1 | 없음 | 없음 | 덤프 창 안에 범인이 든 측정이 1번뿐(위 "새 프로세스" 문제). 수집기를 고쳤으므로 새 덤프에서는 2번 든다 — **다음 세션에서 확인** |
| `disk-hog` office1 | 없음 | 없음 | 예전 덤프에는 디스크 신호가 없다 — **다음 세션에서 확인** |
| `crash-plugin` office1 | 1순위 | 1순위 | 변화 없음 |
| `crash-dotnet`, `hang` | 3위 안 | 3위 안 | 변화 없음 |
| GPU 복제본의 `kernel-live` 덤프 8개(남긴 사례 기준) | — | — | 넣은 문제가 아니라 채점 대상 아님. `faulting_modules` 에서 `aaaaaaaaaaaaaaaa` 가 빠짐 |

이 재생은 "수집기가 오늘 밤 코드였다면 썼을 요약"을 예전 원시 기록에서 다시 만든 것이다. 실제로 새 수집기가 게스트에서 만든 덤프로 확인한 것이 아니다.

### 실험 도구

| 항목 | 상태 | 근거 |
|---|---|---|
| FaultHost·FaultPlugin 정적 빌드 | **검증됨** | SDK 설치 뒤 `/MT` 로 재빌드, `dumpbin /dependents` = `KERNEL32.dll` 만. 꾸러미에 런타임 DLL 을 넣는 우회는 자동으로 꺼진다(`payload.json`: `static`). 게스트(design1·dev1)에서의 실행은 다음 세션 |
| 다음 세션 계획 `plans\cases-2.json` | **실행 전** | 17개 작업, 약 5시간(추정). 디스크 신호 확인(dev1 `disk-hog`)·새 프로세스 CPU 확인(design1 `cpu-hog`) 추가 |

### 같은 밤 추가 — 평가 준비 (정태웅 결정 7건: `docs/evaluation-design-v3.md`)

| 항목 | 상태 | 근거 |
|---|---|---|
| API 키 | **검증됨** | 사용자 범위 환경 변수 `ANTHROPIC_API_KEY`. 모델 목록 조회(요금 없음) 성공 — Opus 5.5·Sonnet 5.5·Haiku 5.5·Fable 5.2 사용 가능. 키는 어디에도 적지 않는다 |
| 실험 환경 `eval/` | **검증됨** | uv 가상 환경(Python 3.12.16, `anthropic` 2.8.0). 시스템 설치 없음. `.venv`·`runs/` 는 커밋 제외 |
| 덤프의 토큰 수 (`eval/count_tokens.py`) | **검증됨(실측)** | 사례 13개: 원본(`events.json`+`snapshot.json`) 27만~47만, 중앙값 **354,721** · 당시 요약 중앙값 2,993 · 마킹 직전 구간을 넣은 요약 중앙값 **5,247** → 원본이 31~105배. 세 모델의 토큰 수는 같다. 원본은 이미 공백 없는 JSON(공백 제거 시 99%). 원본 바이트의 약 70%는 `processes`(10초마다 전체 프로세스 목록), 약 20%는 ETW |
| 정답이 이름에 드러나는 문제 | **수정, 호스트 검증** / 게스트 미확인 | 첫 세션의 범인은 전부 `FaultLab.exe`·`FaultPlugin.dll` 이고 `test-artifact` 표시가 붙었다 → AI 가 증거 없이 맞힐 수 있다. `lab/vm/aliases.json`(지어낸 이름 12개)로 같은 프로그램을 다른 이름·버전 정보로 빌드(`payload.ps1`, 별칭당 67MB), `Program Files` 아래 설치, **명령줄 인자 없이** 실행(할 일은 옆의 `task.cfg`). 호스트: 프로세스 이름 `DocSyncAgent.exe`, 명령줄에 인자 없음, 회사 `Dalmaru Soft`, 파일 이름에 fault 0개, 플러그인 크래시도 별칭 이름(`DocSyncRender.exe` / `dsfilter64.dll`)으로 재현. 예외 문구·임시 파일 이름·창 제목에서도 시험용 낱말 제거 |
| 곁에 있는 정상 프로그램(decoy) | 추가(미실행) | FaultLab `idle` 모드: 5초마다 40ms 일하고 24MB 를 쥔다. `case.ps1 -Decoys <n>` — "모르는 작은 프로그램 = 범인"이 통하지 않게 |
| `case.ps1 -Alias <id\|auto>` · 세션 작업의 `alias`·`decoys` | 추가(미실행) | 별칭 실행은 `<복제본>-<시나리오>+alias` 폴더에 따로. 정답(`scenario.json`)에는 그 사례에서 보이는 이름을 적는다. `plans\cases-2.json` 에 별칭 작업 4개 추가(21개 작업, `-DryRun` 통과). 기존 작업의 동작은 바꾸지 않았다 |

**미확인으로 남은 것:** 포화된 디스크에서 새 신호가 어떤 값을 내는지, 80% 기준이 맞는지 · 별칭 실행의 게스트 전체 흐름(수집기가 별칭 프로세스를 어떻게 기록하는지 — 유의성 점수 포함) · 게스트에서 새 수집기 전체 흐름 · 0x193 분류 · 대상 프로그램을 지정하지 않은 "느림" 마킹에서 사용자가 일부러 돌린 무거운 작업이 범인으로 지목되는 문제 · CPU 점유 후보의 낮은 순위 여유.

## 11/13 — 두 번째 사례 세션 검토 (`plans\cases-2.json`, 03:11~07:15, 21개 작업 = 19 완료 · 1 설치 실패 포함 완료 · 1 실패)

검토 도구를 저장소에 남겼다: `eval/review_cases.py <사례 폴더> [--since 날짜] [--write]`. 사례마다 (1) 넣은 문제가 시나리오가 말하는 피해를 실제로 냈는지 (2) 별칭 실행의 덤프에 정답을 알려 주는 글자가 있는지 (3) 읽는 사람이 알아야 할 표시(복제본이 스스로 남긴 이벤트, 문제 없이도 포화된 디스크)를 덤프만 보고 판정해 `review.json` 으로 남긴다. `eval/cases.py` 는 피해가 재현되지 않은 실행을 사례로 읽지 않는다.

### 어젯밤 "미확인"으로 남겼던 것

| 항목 | 상태 | 근거 (게스트 실측) |
|---|---|---|
| 표시(LOAD_AT 등)를 기다렸다가 마킹 | **검증됨** | 느려짐 사례 9개 전부 `alive_at_mark=true`, 표시→마킹 20~45초(office2 는 32초·45초). 첫 세션의 "문제 없는 덤프" 0건 |
| 기대한 종류의 덤프만 인정 | **검증됨** | GPU 복제본의 `kernel-live` 덤프가 먼저 생겨도 `crash-1000`·`hang-1001`·`manual` 을 기다렸다(design1 3건, video1 1건) |
| 정적 빌드 FaultHost (VC++ 런타임 없는 게스트) | **검증됨** | design1·dev1 `crash-plugin`: 종료 코드 0, 1000 이벤트의 오류 모듈 = `FaultPlugin.dll` |
| 새 프로세스의 첫 CPU 측정 (누적값 계산) | **검증됨** | design1 `cpu-hog`: 켠 지 수 초 뒤의 첫 측정에 28.4% 로 잡힘(kernel-live 덤프, 측정 1회). 수동 덤프에서는 3회, 최대 99.2% |
| 디스크 신호 (`resources.disk`, `resource_recent.disks`) | **검증됨** | 모든 덤프에 `0 C:` 한 줄. 유휴 0.4~1%, 프로그램 여는 중 최대 12~47%, dev1 `disk-hog` 평균 22.6%·최대 56.1%(774 IOPS, 327MB/s), office2(디스크 제한) 97.5~100%·응답 219~803ms |
| 별칭 실행 (다른 이름·decoy) | **검증됨** | 4건 전부 ok. 수집기는 별칭 이름으로 기록(`updatenotifier`, `FontPreviewHost`, `InvoiceExport.exe`, `NsTwainHost.exe`/`nsdeskew64.dll`), decoy 는 프로세스 목록에 있음. 덤프 4개 파일에서 `faultlab`·`faulthost`·`faultplugin`·`task.cfg`·`C:\lab\`·표시 문자열 검색 결과 0건 (`fault` 로 찾으면 `default`·`WerFault`·`Faulting` 만 나온다) |
| 느린 프로필(office2)의 늘린 대기 | **검증됨 / 일부 실패** | `mem-pressure`·`cpu-hog`·`baseline` 완료(24~49분). `disk-hog` 는 5분 만에 원격 세션이 끊겨 실패(두 번째. 원인 **미확인**), Chrome 은 winget 해시 불일치로 다시 설치 실패 |
| 긴 시나리오에서 60분 추세가 범인을 가리키는가 | **검증됨** | `cpu-hog-long`: `top_cpu_60` 에 FaultLab 평균 80.9%(표본 11). `mem-leak-slow`: `top_memory_growers` 1위 FaultLab 분당 214MB(R² 1.0) |

### 새로 나온 것

| 항목 | 상태 | 내용 |
|---|---|---|
| dev1 `disk-hog` — 문제는 있었지만 피해가 없었다 | **사례에서 제외** | 빠른 디스크라 327MB/s 를 써도 디스크는 평균 22.6% 만 바빴고 응답은 0.73ms. "느리다"는 마킹은 시나리오가 한 것이지 PC 가 느려진 것이 아니다. 엔진이 후보를 올리지 않은 것이 맞다(엔진 시험 s16 의 "한가한 디스크의 큰 복사는 원인이 아니다"와 같은 상황). `review.json` `effect_ok=false` |
| 피해 확인 기준 (`review_cases.py`) | 추가 | CPU: PC 전체 최대 90% 이상 + 범인이 `top_cpu` 에 있음 · 메모리: 커밋 최대 85% 이상 + 범인이 `top_memory` 에 있음 · 디스크: 가장 바쁜 디스크 최대 80% 이상 + 범인이 `top_io` 에 있음 · 크래시/멈춤: 덤프의 대상·오류 모듈이 범인. 기준값은 엔진 규칙과 같은 눈금이라 **독립적인 확인이 아니다** — 사람이 화면으로 느린 것을 본 것이 아니라는 뜻 |
| office2 는 문제 없이도 디스크가 포화 | **프로필 수정** | `baseline`(문제 없음)에서 디스크 평균 97.5% 바쁨, 응답 평균 803ms·최대 12초, 초당 100회·3.5MB. Hyper-V 의 IOPS 는 8KB 단위라 500 = 약 4MB/s 였다. 이 상태에서는 디스크 점유를 유휴와 구별할 수 없고 설치에 26분이 걸린다. `vm.json` `office-old` 를 3000(약 24MB/s)으로 바꿨다. 이미 만든 office2 사례 3개는 `scenario.json` 에 500 으로 적혀 있고 그대로 "디스크가 매우 느린 PC" 사례다. 체크포인트가 예전 제한을 되살리므로 `case.ps1` 이 복원 뒤 다시 건다 — **게스트 미확인** |
| office2 `baseline` 의 정답 | **결정 필요** | 범인 프로그램은 없지만 PC 는 실제로 느리다(디스크). 엔진은 Defender 를 6.9% 로 1순위에 올렸다(포화된 디스크의 입출력 1위). "문제 없음"도 "Defender 가 범인"도 맞는 답이 아니고 "디스크가 한계"가 맞다 → 정답 종류에 `hardware_limit` 를 두고 따로 센다(제안) |
| `LiveKernelEvent 0x193` | **미확인(범위는 좁혀짐)** | 두 세션 통틀어 GPU 분할 복제본(design1·video1)의 남긴 실행 11/11 에서 수집기 시작 111~115초 뒤에 발생(video1 유휴는 165초에 한 번 더), 프로그램을 하나도 켜지 않은 유휴 실행에서도 발생. GPU 분할이 없는 복제본(dev1·office1·office2) 실행 0/21. 보고서 경로는 `LiveKernelReports\WATCHDOG`. 넣은 문제와 무관한 것은 확정. **수집기(센서 조회)가 일으키는지, 복제본이 로그온 뒤 일정 시간에 스스로 내는지는 구분 못 함** — 수집기 시작 시각이 부팅 기준으로 늘 같았다. 다음 세션에 `idle-late`(수집기를 3분 늦게 시작) 2건으로 가른다. 수집기 탓이면 "읽기 전용·저부하" 원칙에 걸리는 문제다 |
| 수집기의 `kernel-live` 덤프 증상 분류 | 관찰 | 0x193 덤프의 증상이 `freeze`, 유의성 90(major)으로 기록된다. 화면이 멈춘 적은 없다. 위 구분이 끝난 뒤 다룬다 |

### 엔진 (세션 덤프 그대로 실행)

| 시나리오 | 결과 | 비고 |
|---|---|---|
| 넣은 문제 14건(피해 재현된 것) | 범인 1순위 **13/14 → 규칙 수정 뒤 14/14** | 같은 사례로 규칙을 맞춘 것이므로 **개발용 수치**다. 정확도 주장에 쓰지 않는다 |
| `cpu-hog-long` office1 | 2순위 → 1순위 | 범인이 평균 65% 를 쓰는 동안 Defender 가 23% 를 썼는데 두 CPU 근거의 세기가 같아, 사전 가중치와 "실시간 감시 켜짐" 근거로 Defender 가 1순위였다 |
| `cpu-hog` office2 | 1순위(간신히) | 마킹 45초 전에 켜진 프로그램은 5분 창 평균이 15.9% — 기준 15% 를 겨우 넘었다 |
| 문제 없는 실행 5건 | 4건은 특정 후보 없음(전부 5% 안팎), office2 는 Defender 6.9% | 위 "결정 필요" |

규칙 E2 를 고쳤다(`engine/src/candidates.cpp`, 시험 s17 추가, 172 통과): 평균은 창 전체가 아니라 **그 프로세스가 측정에 잡힌 동안**의 평균(25% 이상), 근거의 세기는 **가장 많이 쓴 프로세스 대비 비율**. 여전히 잠정값이다. 1순위와 2순위의 차이는 CPU·멈춤 사례에서 6~7% 대 5% 로 작다.

### 실험 도구 · 다음 세션

| 항목 | 상태 | 내용 |
|---|---|---|
| `scenarios.json` `idle-late` · `guest-case.ps1` `pre_collector_sec`, 결과의 `boot_at`·`live_kernel_events` | 추가(**미실행**) | 구문 검사만 통과 |
| `session.ps1` 작업의 `id` | 추가(**미실행**) | 같은 복제본·시나리오를 여러 번 넣을 수 있게. 끝난 작업은 `cases\_done\<id>.txt` |
| `plans\cases-3.json` | **실행 전** | 25개 작업, 약 4시간(추정): `idle-late` 2 · 별칭 사례 17(시나리오 7종 × 복제본 4, decoy 1~2) · decoy 만 있고 문제 없는 실행 2 · office2 새 디스크 제한 4. `-DryRun` 통과 |

### 같은 날 — 시범 실행 (`eval/sets/pilot-1.txt` 10건, 지시문 p1, 지출 5.66달러 / 시범 한도 15)

| 항목 | 상태 | 근거 |
|---|---|---|
| 한 번 호출 방식 4종(A0·A2·A3·A4) × 모델 3종의 전체 흐름 | **검증됨** | 답 139개, 거절·잘림·형식 깨짐·API 오류 0건. 24만~41만 토큰 요청(A2)도 5~20초에 처리 |
| 답 1개 요금(실측) | **검증됨** | A4: Haiku 0.0015 · Sonnet 0.025 · Opus 0.054달러. A2: Haiku 0.17 · Sonnet 0.54 · Opus 1.01달러 → 원본 전체는 요약+엔진의 약 115배 · 22배 · 19배 |
| 같은 사례 3번 반복의 일치(A4) | 측정 | Haiku 8/10, Sonnet 8/10. "멈춤" 사례에서 지목과 판단 보류가 갈린다 |
| 인용한 증거 번호 | **검증됨** | 요약 방식의 모든 답에서 실제로 준 번호만 인용 |
| 정확도 | **주장하지 않음** | 문제 사례가 4개이고 전부 개발용이다. 틀린 답은 전부 판단 보류였고, 엉뚱한 프로그램·decoy 를 지목한 답은 요약 방식에서 0건이었다는 것까지만 적는다 |
| 집계 도구 `eval/summarize.py`, 사례 목록 파일 `--set` | 추가 | 실행 폴더들을 방식·모델별 한 줄로. 복제본 표시가 붙은 문제 없는 실행은 따로 센다 |

본 실험 전에 고칠 것 3건(답 형식, 요약의 CPU 평균, 정답 종류 `hardware_limit`)과 실측으로 다시 계산한 예산은 `docs/evaluation-design-v3.md` 2절 "시범 결과", 8절.

**미확인으로 남은 것:** 0x193 이 수집기 탓인지(`idle-late`) · 디스크 제한 3000 IOPS 에서 office2 가 어떻게 도는지, 그 디스크 점유 사례 · office2 `disk-hog` 원격 세션 끊김의 원인 · `guest-case.ps1`·`session.ps1`·`case.ps1` 의 오늘 수정(구문 검사와 모의 실행만 통과) · decoy 만 있고 문제 없는 실행 · 긴 시나리오의 별칭 실행 · Chrome winget 해시 불일치(office2).

## 11/14 — 세 번째 사례 세션 검토 (`plans\cases-3.json`, 02:29~05:42, 25개 작업 모두 완료)

검토 도구: `eval\review_cases.py F:\pca-vm\cases --since 20261114 --write`. 실행 25개 중 **사례로 쓸 수 있는 것 24개**(문제 넣음 19, 문제 없음 5). 01:37 에 시작했다가 26초 만에 멈춘 세션의 빈 폴더(`design1-idle-late\20261114-013714`)가 하나 있다 — 결과 파일이 없어 도구가 건너뛴다.

### 게스트에서 확인된 것

| 항목 | 상태 | 근거 |
|---|---|---|
| 11/13 에 고친 실험실 스크립트(`pre_collector_sec`, 결과의 `boot_at`·`live_kernel_events`, 작업 `id` 와 `_done` 표시, 저장 지점 복원 뒤 디스크 제한 다시 걸기) | **검증됨** | 25개 작업이 모두 끝났고 `_done` 에 25개, office2 작업 로그 4개에 "disk limit … set to 3000 IOPS (the checkpoint had 500)" |
| **0x193 은 수집기 탓이 아니다** | **검증됨** | GPU 복제본 실행 9개 모두에서 보고가 **부팅 후 180~184초**에 찍혔다. 수집기를 부팅 후 85~93초에 켠 7개와 251~252초에 켠 2개(`idle-late`)가 같다 — 수집기가 꺼져 있던 때에 이미 났다. GPU 없는 복제본 16개 실행에서는 0건. 부팅 후 4~5초에도 매번 1건(저장 지점에 남아 있던 보고가 부팅 때 접수된 것으로 보이나 **미확인**), 일부 실행은 5~9분에 1건 더(3/9) |
| 디스크 제한 3000 IOPS 의 office2 | **검증됨** | 아무것도 안 넣은 5분: 디스크 평균 7.6% 바쁨, 90% 이상 표본 1개(11/13 의 500 IOPS 에서는 평균 97.5%). 실행 1개에 7.6~9.5분(11/13 에는 24~49분). 원격 세션 끊김은 다시 나오지 않았다(4/4 완료) — 500 IOPS 의 포화가 원인이었을 가능성이 크지만 확인한 것은 아니다 |
| office2 `disk-hog` | **검증됨(첫 사례)** | 마킹 45초 전에 켜진 프로그램이 디스크를 채움: 표본 24개 중 9개가 93~100%, 응답 평균 293ms·최대 6.3초, 입출력 1위가 범인(잡힌 동안 평균 10.3MB/s) |
| 별칭 사례 묶음 | **검증됨** | 문제 넣은 실행 19개(시나리오 9종 × 복제본 5) 모두 기대한 덤프가 있고 피해가 덤프에 있다. 덤프에서 시험 프로그램 이름·실험실 경로 검색 0건 |
| decoy 만 있고 문제 없는 실행 | **검증됨** | office1·dev1: decoy 2개가 프로세스 목록에 있고(`DocSyncAgent`·`MediaIndexer`, `ScanBridge`·`DocSyncAgent`) 엔진은 아무도 지목하지 않음 |
| 긴 시나리오의 별칭 실행(`cpu-hog-long`, `mem-leak-slow`) | **검증됨** | 둘 다 완료(17.5분·17.8분), 엔진 1순위 = 범인 |
| 빠른 디스크의 `disk-hog` | **미재현(세 번째)** | office1: 376MB/s 를 써도 디스크 최대 77%, 응답 0.5ms — 피해가 없어 사례에서 제외. `disk-hog` 는 느린 디스크 프로필에서만 사례가 된다 |

### 엔진 (개발용 숫자 — 규칙을 이 사례들에 맞췄다)

| 항목 | 값 |
|---|---|
| 이번 세션 문제 사례 19건의 엔진 1순위 | 고치기 전 18/19 → **19/19** |
| 놓친 1건 | office2 `disk-hog`: 규칙 D2 가 창 평균 80% 를 요구하는데, 마지막 1분만 포화라 창 평균이 44.1% 였다(11/13 의 CPU 규칙 E2 와 같은 종류의 묽어짐) |
| 고친 것 | D2 에 (나) 추가: 90% 이상 바빴던 표본이 3개 이상이고 입출력 1위 프로세스가 2회 이상 측정에 잡힘. 시험 `[s18]`, 엔진 시험 176개 통과. `engine/docs/rules_rationale.md` D2 행 |
| 문제 없는 실행 5건 | 5건 모두 지목 없음 |
| 첫 세션(11/12) 덤프에서 순위가 낮은 4건 | 그날 확인한 수집기 오류(`0x80040827`·`2cd5` 가 오류 모듈로 실림)가 덤프에 남아 있는 것 — 새 문제가 아니고, 이름이 드러난 사례라 평가에 쓰지 않는다 |

### 수집기 요약(`resource_recent`)에 추가 — 이미 모은 값을 세고 평균할 뿐, 판단 없음

| 항목 | 상태 | 내용 |
|---|---|---|
| `top_cpu[].cpu_avg_active`, `top_io[].io_mbps_avg_active` | **검증됨(단위 시험 + 덤프 82개 재계산)** | 그 프로세스가 상위 목록에 잡힌 측정만의 평균. 창 평균(`cpu_avg`)은 그대로 둔다 |
| `top_cpu[].cpu_last`, `top_io[].io_mbps_last` | **검증됨(〃)** | 창의 마지막 측정(마킹 무렵)의 값. 그때 상위 목록에 없었으면 null |
| `disks[].busy_ge90_samples` | **검증됨(〃)** | 90% 이상 바빴던 10초 표본 수 |
| `disks[].busy_pct_recent`·`latency_ms_recent`·`mbps_recent` | **검증됨(〃)** | 마지막 표본 6개(약 1분)의 평균 |
| 예전 덤프의 요약 다시 계산 `eval\replay.py` | 추가 | `BlackBox --resource-recent` 로 덤프마다 요약을 다시 만들어 `cases\_replay\` 에 둔다. `eval\cases.py`·`review_cases.py` 는 그 파일이 있으면 그것을 읽는다 — 게스트에서 돈 수집기 판이 달라도 모든 사례가 같은 항목을 갖는다 |
| 게스트용 꾸러미 | 다시 빌드 | `lab\vm\payload.ps1`(호스트에서 빌드만). 다음 세션부터 게스트의 수집기가 새 항목을 직접 쓴다 — **게스트 미실행** |

수집기 단위 시험 106개 통과(2개 추가). 호스트에는 .NET 8 런타임이 없어 `DOTNET_ROLL_FORWARD=Major` 로 돌린다.

### 같은 날 — 개발용 사례 36개로 지시문 고치기 (`eval/sets/dev-1.txt`, 시범 항목 누적 10.40달러 / 한도 15)

개발용 묶음: 평범한 이름으로 넣은 문제 23 + 문제 없음 6 + GPU 복제본의 문제 없음 6(0x193 표시) + 디스크가 늘 포화인 PC 1. **아래 숫자는 정확도 결과가 아니다** — 지시문 p3 는 같은 사례에서 p2 의 실패를 보고 고친 것이다.

| 방식·모델 | 지시문 | 문제 사례 1순위 | 틀린 지목 | 문제 없음 6건(명시/보류/지목) | 채점 제외 | 답 1개 요금 |
|---|---|---|---|---|---|---|
| 요약+엔진(A4) · Haiku ×3 | p2 | 63/69 | 5 | 7/10/**1** | 0 | 0.0017 |
| 요약+엔진(A4) · Haiku ×3 | p3 | **69/69** | 0 | 6/12/0 | 0 | 0.0018 |
| 요약+엔진(A4) · Sonnet | p2 | 22/23 | 1 | 3/3/0 | 0 | 0.029 |
| 요약+엔진(A4) · Sonnet | p3 | **21/21** | 0 | 2/4/0 | **2(잘림)** | 0.037 |
| 요약만(A3) · Haiku ×3 | p3 | **69/69** | 0 | 6/12/0 | 0 | 0.0017 |
| 요약만(A3) · Sonnet | p2 | 21/23 | 2 | 3/3/0 | 0 | 0.025 |
| 요약만(A3) · Sonnet | p3 | **23/23** | 0 | 2/4/0 | 0 | 0.026 |

| 항목 | 상태 | 내용 |
|---|---|---|
| "멈춤" 사례의 판단 보류(p1) | **고쳐짐** | p2 부터 4건 모두 지목(Haiku 12/12, Sonnet 4/4). 지시문에 "밖에서 끼어든 흔적이 없으면 멈춘 프로그램 자신이 후보"를 넣음 |
| 요약의 묽은 CPU 평균(p1 에서 Sonnet 이 요약만 받으면 보류) | **고쳐짐** | `cpu_avg_active` 를 넣은 뒤 요약만 받은 Sonnet 도 CPU 사례 6/6 |
| p2 의 부작용 ① | 발견 → p3 | "하드웨어가 원인이면 프로그램을 범인으로 만들지 말라"는 문장 때문에 느린 PC(office2)에서 프로그램이 원인인 사례까지 "디스크"라고 답했다(Sonnet 3건: 요약만 2·요약+엔진 1, Haiku 6건 중 5건은 단정) |
| p2 의 부작용 ② | 발견 → p3 | 증상이 없는 실행에서 배경의 Edge 업데이트 오류 보고를 "꺼진 프로그램 자신"으로 지목(Haiku 3회 중 1회). p3: 사용자 증상과 이어지지 않는 기록은 후보가 아니다 |
| 디스크가 늘 포화인 PC(정답: 하드웨어 한계) | **미해결** | p3 에서는 모든 답이 Defender 를 1순위(확신 중간)로, 디스크를 2순위로 적었다(Haiku 6/6, Sonnet 2/2). 권고는 "검사가 끝나기를 기다린다, 백신은 끄지 않는다" 수준. 덤프 하나로는 "평소에도 이런가"를 알 수 없다 — 평소 상태와 비교할 자료가 있어야 가를 수 있다 |
| 답이 길이 한도에서 잘림 | **새로 관찰** | Sonnet 요약+엔진 36개 중 2개: JSON 문자열 안에서 같은 글자(`}}</br>`)를 반복하다 16,000 토큰에서 멈춤(73초, 한 개에 약 0.16달러). 잘리기 전의 1순위는 맞았다. 다른 답 570여 개에서는 0건. `run_single.py` 가 잘린 답을 한 번 다시 묻게 했다(두 번 다 요금에 넣고 `attempts` 에 기록) — **다시 묻는 경로는 실제 호출로 확인하지 않았다** |
| 개발용 묶음의 쓸모 | 한계 | p3 에서 요약만(A3)과 요약+엔진(A4)이 모두 만점이라 **이 묶음으로는 방식 차이를 가를 수 없다**. 엔진 판단이 더해 주는 것이 있는지는 변형 시나리오가 든 시험용에서만 볼 수 있다 |
| 반복 일치(Haiku 3회) | 측정 | p3: 35/36 (요약만·요약+엔진 모두) |

### 다음 세션 — 시험용 사례 생산 (`lab\vm\plans\test-1.json`, **실행 전**)

| 항목 | 상태 | 내용 |
|---|---|---|
| 계획 | 모의 실행 통과 | 52개 작업(문제 넣음 42, 문제 없음 10), 약 7시간(추정) → `-Hours 9`. 작업마다 `id` 가 있어 끊기면 다음 날 이어서 돌릴 수 있다 |
| 변형 시나리오 7종(`scenarios.json`) | 추가(**게스트 미실행**) | `cpu-hog-early`(25초 만에 마킹), `cpu-hog-late`, `mem-leak-mid`, `hang-late`, `crash-start`, `crash-failfast`, `disk-hog-long`. FaultLab 의 기존 방식에 인자만 다르게 준 것. `crash-failfast` 는 Windows 가 이벤트 1000 을 남기는지 **미확인** — 안 남기면 그 실행 2개는 실패로 끝난다 |
| 시험용을 따로 두는 절차 | 도구 추가(**미실행**) | 세션 뒤 `eval\split_test.py … --move` 로 실행 폴더를 `F:\pca-vm\cases-test` 로 옮기고, `eval\replay.py` → `eval\review_cases.py --no-engine --write` 로 사례 자격만 본다. 동결(11/23) 뒤 한 번 쓰기 전에는 엔진·모델을 돌리지 않는다 |
| 계획에 없는 것 | 미지원 | "사용자가 일부러 돌린 무거운 작업"(헷갈리는 정상)과 실제 프로그램의 알려진 버그 사례는 이 계획에 없다 — 시나리오 종류를 새로 만들어야 한다 |

**미확인으로 남은 것:** GPU 복제본의 부팅 직후(4~5초) 0x193 보고와 5~9분 뒤의 세 번째 보고의 정체 · 수집기가 `kernel-live` 덤프를 증상 `freeze`·유의성 90 으로 분류하는 것이 맞는지 · 500 IOPS 에서의 원격 세션 끊김 원인 · Chrome winget 해시 불일치(office2) · 새 꾸러미의 수집기와 변형 시나리오의 게스트 동작 · 잘린 답 다시 묻기 · 늘 포화인 디스크를 "하드웨어 한계"로 가르는 방법.

## 11/14 저녁 — 수집기 자체의 부담: 게스트 덤프에서 다시 계산 (새 실행 없음)

지금까지 만든 덤프 82개(`F:\pca-vm\cases`, 복제본 5종)의 `events.json` 에서 수집기 프로세스(`BlackBox`, 콘솔판, 비관리자) 자신의 줄만 뽑았다. 프로세스 목록은 10초마다, CPU 상위 10 은 30초마다 찍힌다.

| 항목 | 분류 | 값 |
|---|---|---|
| 메모리(Private) — 마킹 시점 | 검증됨(짧은 실행) | 중앙값 48.5MB, 범위 37.9~79.5MB. 복제본별 중앙값 45~59MB(office2 가 가장 큼) |
| 메모리(Working Set) 최대 | 검증됨(짧은 실행) | 중앙값 76MB |
| 덤프 창 안에서의 증가 | 검증됨 — **누수로 읽으면 안 됨** | 약 12MB 에서 시작해 창 끝까지 +32~36MB. 창이 수집기 시작 직후를 포함하기 때문(실행 5~10분). 그 뒤 평탄한지는 이 자료로 알 수 없다 |
| CPU | 검증됨(간접) | CPU 측정 1,556회에서 수집기가 상위 10위에 든 적 0회. 정확한 %는 이 자료로 알 수 없다(상위 10 밖은 기록되지 않음) |
| 호스트 값과의 차이 | 참고 | 호스트(10월, 센서 동작, 30분): Private 94~114MB, Working Set 186~267MB. 게스트는 센서 값이 없고 실행이 짧다 |
| 1시간 이상 켜 둔 메모리 | **미확인** | 게스트·호스트 모두 기록 없음(호스트는 30분까지) |
| 2코어·4GB 프로필에서 작업 시간에 주는 영향 | **미확인** | 수집기 유무로 같은 작업을 재는 실행이 없다 |
| 메모리가 어디에 쓰이는지 | **미확인** | 링버퍼는 약 2MB(10월 실측)뿐이다. 나머지의 내역(런타임, ETW, 센서 라이브러리, 성능 카운터)은 재지 않았다 — 줄이기 전에 스파이크로 분해한다 |

계산 스크립트는 저장소 밖(작업 임시 폴더)에 있다. 다음에 쓸 일이 생기면 `eval/` 로 옮긴다.

## 11/14 오후 — 어려운 사례를 만드는 틀: 실제 프로그램 실행 (`kind: recipe`, **게스트 미실행**)

넣은 문제(FaultLab)만으로는 엔진 단독으로도 1순위가 만점이라 방식 차이가 나오지 않는다(개발용 23/23). 그래서 실험실에 "실제 프로그램을 그대로 실행하는" 시나리오 종류를 더했다. 수집기 코드는 바꾸지 않았다.

| 항목 | 분류 | 내용 |
|---|---|---|
| 꾸러미(`lab/vm/packages.json`, `packages.ps1`) | 검증됨(호스트) | 공식 보관소에서 받은 옛 버전을 `<root>\payload\packages\<id>.zip` 으로 묶는다. 게스트에서는 `C:\Tools\…`(프로그램)와 `C:\Work\…`(사용자 파일)에 풀린다. 5종 빌드: AutoHotkey 2.0.29, Godot 4.3, Notepad++ 8.6.7, CPython 3.13.5, Git for Windows 2.47.0(MinGit, 릴리스 노트의 SHA-256 과 일치 확인) |
| 시나리오 종류 `recipe`(`scenarios.json`, `case.ps1`, `guest-case.ps1`) | 검증됨(호스트 일부) | 실행 파일+인자, 설치된 프로그램의 다른 실행 파일(`app`+`exe_name`), 키 입력 스크립트(`driver`) 세 가지로 실행. 새 함수(`Join-Args`·`Start-RunSpec`·`Stop-Tree`)는 호스트에서 무해한 명령으로 11개 검사 통과(종료 코드 유지, 공백·세미콜론 인자, 자식까지 종료). 기존 종류(fault·idle·launch)에 대해서는 게스트에 주는 작업 정의에 빈 항목 2개만 늘어남을 확인 |
| CPython 3.13.5 `setlocale` 크래시 | 검증됨(호스트 스파이크) | 종료 코드 0xC0000409, 이벤트 1000(프로그램 python.exe, 오류 모듈 ucrtbase.dll). 오류 모듈이 Windows DLL 인데 원인은 프로그램인 사례 |
| AutoHotkey·Godot·Notepad++ | 검증됨(11/10 호스트) | 3/3 씩 재현했던 것. 게스트에서는 처음 |
| Git 2.47.0 `git maintenance start` | **미확인** | 릴리스 노트와 수정 PR 만 근거. 호스트에서는 예약 작업을 등록하므로 돌리지 않았다 — 첫 실행이 게스트다. 정상 종료하면 사례가 아니다 |
| 사용자가 돌린 무거운 작업(`work-7zip`, `work-blender`) | **미확인** | 범인이 없는 사례. 게스트에서 7z.exe·blender.exe 를 찾는지, 마킹 때까지 돌고 있는지 모른다 |
| Windows 자체 작업(`sys-defender-scan`) | **미확인** | `Start-MpScan -ScanType FullScan`. 게스트에서 전체 검사가 시작되는지 모른다 |
| 두 가지가 동시에(`busy-*`) | **미확인** | 7-Zip 작업 중에 넣은 문제. 4GB 게스트에서 7-Zip + 메모리 85% 점유가 시간 안에 끝나는지 모른다 |
| 검토·채점 도구 | 검증됨(회귀) | `review_cases.py`: 사용자 작업·Windows 작업의 피해 규칙, 표시 `user_workload:`·`busy:`. `grade.py`: 정답의 다른 이름(`truth.also` — MsMpEng.exe = Microsoft Defender). `summarize.py`: 사용자 작업 사례를 따로 센다. 기존 54건 자체 검사 0 실패, 개발용 요약 숫자 그대로 |

오늘 고친 `case.ps1`·`guest-case.ps1` 은 오늘 밤 시험용 세션(`test-1.json`)도 지나간다. 추가한 줄은 새 종류에서만 동작하지만 **게스트에서 돌려 본 적은 없다.**

같은 날 추가 — Blender 5.1.0(`real-blender-bgscene`): 공식 보관본을 받아 SHA-256 을 확인하고 꾸러미로 만들었다(6번째, 388MB). **호스트에서 실행해 보는 확인은 하지 못했다**(도구의 권한 검사에서 막힘 — 우회하지 않았다). 그래서 명령 줄 렌더(`-b`)에서도 크래시가 나는지, Windows 가 이벤트 1000 을 남기는지는 **미확인**이고 첫 게스트 실행이 그 확인이다. Windows 기록이 없을 경우를 위해 이 시나리오는 끝에 사용자 마킹("crash")을 보내고, 검토 도구는 종료 코드로 사례 자격을 정한 뒤 `silent_crash` 로 표시한다. `real-1.json` 은 23개 작업이 됐다.

같은 날 추가 — 낮 시간 세션(정태웅 동의)을 위한 장치와 Steam 시나리오. 수집기 코드는 그대로다.

| 항목 | 분류 | 내용 |
|---|---|---|
| 게임 실행 중 대기(`session.ps1`, `vm.json` `host_wait_processes`) | 검증됨(호스트) | 목록의 프로세스가 호스트에서 도는 동안 새 작업을 시작하지 않는다. 와일드카드 이름 일치·기한에서 끝남·빈 목록은 기다리지 않음을 확인. 작업 도중에 켜진 게임은 알아채지 못한다 |
| 세션 중복 방지(`<root>\session.lock`) | 검증됨(호스트) | 살아 있는 세션의 잠금만 인정한다(프로세스 번호와 시작 시각). 죽은 프로세스·재사용된 번호의 잠금은 무시 |
| 호스트 부하 기록(`case.ps1` → `shots\host-load.csv`, `scenario.json` `host_load`) | 검증됨(호스트) | 30초마다 호스트 자신의 CPU %, 전체 논리 프로세서 CPU %(게스트 포함), 남은 메모리. 두 WMI 클래스가 일반 권한에서 값을 준다(첫 호출 약 5.7초). 검토 도구가 `host_busy` 로 표시(호스트 평균 50% 이상 또는 전체 85% 이상 — 근거 없는 임시 기준) |
| `bg-steam-update`(SteamCMD 익명 로그인) | **미확인** | 공식 파일의 서명만 확인(Valid, Valve). 첫 실행의 자체 업데이트에 걸리는 시간과 익명 내려받기가 게스트에서 되는지 모른다. `real-1.json` 은 25개 작업 |
| 여러 대 동시 실행 | 미지원 | 세션이 끝날 때 모든 복제본을 끄기 때문에 지금은 한 번에 하나만 돈다 |
