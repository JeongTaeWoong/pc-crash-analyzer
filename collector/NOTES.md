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
