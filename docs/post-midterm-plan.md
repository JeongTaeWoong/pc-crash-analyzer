# 중간발표 이후 계획 — 수집기·통합 관점 (2026-10-28)

입력: 멘토링 보고서(2차, 10/15) · 중간발표 자료(10/16) · 이 PC 30일 이벤트 로그 재분류 · 느려짐 신호 스파이크 ·
문헌/GitHub 조사(출처는 5절). 숫자는 전부 실측이며, 추정은 "추정"으로 표기한다.

---

## 0. 먼저, 현재 크래시 검출이 왜 "의미 없어" 보였나 — 데이터로

테스트 PC의 최근 30일 Application 로그 (1000 앱 크래시 / 1001 WER 보고 / 1002 응답 없음):

| 항목 | 건수 | 해석 |
|---|---|---|
| 전체 | 127 | |
| 1000 앱 크래시 | 42 | 이 중 **30건이 CrashDummy(우리 테스트)** → 실제 크래시 12건, 8개 프로그램 |
| 1001 WER 보고 | 79 | 유형: APPCRASH 38 · **RADAR_PRE_LEAK_64 21** · CLR20r3 6 · crashpad_log 4 · StoreAgentScanForUpdatesFailure 4 · **LiveKernelEvent 2** · AppHangB1 2 · FaultTolerantHeap 1 · BEX64 1 |
| 1002 응답 없음 | 6 | NVIDIA Overlay 4 · Hearthstone 1 · STOVE 1 |

세 가지가 보인다.

1. **1001은 "크래시"가 아니라 "WER이 보고서를 만들었다"는 이벤트다.** RADAR_PRE_LEAK(메모리 누수 진단), 스토어
   업데이트 실패, crashpad 로그, FaultTolerantHeap 등 **비크래시가 30건(테스트 제외 60건 중 50%)**. APPCRASH/CLR20r3는
   같은 크래시의 1000과 중복이다. 현 트리거(1000 또는 1001 → 즉시 덤프)로는 30일간 121회 덤프 중 절반이 노이즈.
2. **사용자가 체감하는 신호는 빠져 있다.** 응답 없음(1002) 6건, 커널 라이브 덤프(LiveKernelEvent — GPU TDR 등 시스템급)
   2건은 트리거에 없거나(1002) `crash-1001`로 뭉뚱그려진다(LiveKernelEvent).
3. **실제 크래시 12건(HearthstoneDeckTracker·NexonPlug·Unity·beatbanger·tbs_browser·winrtutil32…)은 전부 수집기가 꺼져
   있을 때 났다.** 덤프 0건. "실제 소프트웨어 충돌을 확인하지 못했다"의 절반은 필터 문제, 절반은 **상시 가동 부재**다.

남건호 PC(비관리자) 재현(`exp/collector-autoDetect-test`): 감지 4ms, 덤프 정상, 수명 0.29초 프로세스의 모듈은 비관리자라
미확보(예상대로), **.NET 8 런타임 미설치 시 실행 실패** 발견 → 팀 세팅 가이드에 추가.

---

## 1. 멘토링 2차 피드백 → 수집기·통합 작업으로 번역

| 피드백 | 수집기·통합이 할 일 | 비고 |
|---|---|---|
| 에이전트에 넘길 데이터 설계 (단일 턴 → 도구 호출 멀티 턴) | ① 엔진 요약(상위 후보+근거)만 1차 전달은 이미 가능 ② **로컬 도구 API**: 덤프 안을 검색하는 도구(이벤트 시간창/PID/소스별 조회, 크래시 프로세스 모듈, 센서 구간)를 수집기(클라이언트)가 제공 — 서버 에이전트가 도구 호출 → 소켓으로 클라이언트가 응답 | 비시스템 필터(실측 69% 제거)·요약으로 토큰 최소화 |
| 골든 셋 팀원별 30건 | **"케이스 내보내기"**: 덤프 → `engine-snapshot.json` + `ground_truth` 라벨을 붙여 `cases/`에 저장하는 UI/CLI | 라벨은 사람이 확인 후 입력 (골든) |
| 문제 유발 테스트 프로그램 | CrashDummy → **FaultLab**로 확장: crash(a/b/c) · hang(1002) · leak(RADAR) · cpu 과부하 · disk 과다 · **plugin-dll-crash**(서드파티 DLL이 대상 프로세스 안에서 죽는 s6 패턴 재현) | 정답이 분명한 골든 셋 생성기 |
| 컴퓨터 느려진 이유 진단 | 2절 | 스파이크 완료 |
| 서버·Bedrock·소켓 | 클라이언트 측 인터페이스(도구 요청/응답 JSON) 정의만 수집기 담당 | 서버는 에이전트 담당 |
| 자동화 범위 ①~④ (자동 수행 / 제안만 / 승인 후 / 안 함) | 수집기는 ①(수집·후보)만. 조치 실행기는 "되돌릴 수 있는 조치"만, 원복 지점 기록 — 4절 플레이북에 위험 Lv 명시 | 설문 0%/92%와 일치 |

---

## 2. 성능(느려짐) 진단 — 신호별 가용성 (2026-10-28 읽기 전용 스파이크, 관리자)

| 신호 | 방법 | 상태 | 실측 |
|---|---|---|---|
| 프로세스별 CPU·WS·IO 상위 N | `Win32_PerfFormattedData_PerfProc_Process` | **검증됨** | 유휴 상태에서 chrome 93%, WmiPrvSE 31%, **SignKoreaWD 22%, Goji 22%, SKBspmtmon 13%** — 공인인증·금융보안 상주물이 CPU를 쓰고 있었음 |
| 메모리 압박 | `PerfOS_Memory` (AvailableMB, Commit%, Pages/s) | 검증됨 | 가용 48GB, 커밋 72%, 페이징 0 |
| 디스크 부하 | `PerfDisk_PhysicalDisk(_Total)` (busy%, 큐 길이, B/s) | 검증됨 | busy 0%, 큐 0 |
| 디스크 건강 | `Get-PhysicalDisk` HealthStatus/OperationalStatus | 검증됨 | 4개 전부 Healthy |
| SMART 예측 실패 | `MSStorageDriver_FailurePredictStatus` | **미지원** (관리자에서도 액세스 거부 — NVMe 계열 추정) | HealthStatus로 대체 |
| 시작 프로그램 | Run 키(HKCU+HKLM) + 로그온/부팅 트리거 예약 작업 | 검증됨 | Run 17개 · 활성 예약 작업 32개 |
| 백신 실시간 검사 자원 | PerfProc(MsMpEng) | 검증됨(PerfProc) | `Get-Process`의 CPU는 보호 프로세스에서 0으로 나와 **신뢰 불가** → PerfProc만 사용 |
| GPU 온도·클럭·TDP% | 기존 SensorSource | 검증됨 | 스로틀 추론 기존 방식 |
| CPU 온도 | — | 미지원(현 보안 구성) | 이미 확정 |

설계 제안:
- **새 증상 `slow`** (엔진 스키마 `symptom.type`에 추가 — 엔진 담당과 협의). 트리거는 수동 마킹(단축키/버튼) — "느려졌다고 느낀 순간".
- **장기 베이스라인**: 링버퍼(6분)와 별도로 **분 단위 롤업**(상위 5 프로세스 CPU/IO, 메모리 가용, 디스크 busy)을 24시간
  메모리에만 보관(1,440행 × ~1KB ≈ 1.4MB 추정). 마킹 시 "마킹 창 vs 24h 베이스라인" 차이를 스키마의 기존 필드
  `baseline_diff`(added/removed/changed)로 내보낸다.
- 덤프 추가 필드(엔진 협의): `resource_top[]`, `startup{run,tasks}`, `disk_health`, `av_resource`.
- 발표 사례 후보: 위 실측 그대로 — "작업관리자를 열지 않아도 금융보안 상주물 3개가 CPU 57%를 쓰고 있다는 걸 알려 준다".

---

## 3. 의미 있는 크래시만 잡기 — 트리거 재설계

### 3-1. 1단계: 트리거 분류 (수집기, 규칙·비용 0)

| 이벤트 | 현재 | 변경 |
|---|---|---|
| Application 1000 (앱 크래시) | 트리거 | 유지. **denylist**(CrashDummy·FaultLab 등 테스트 바이너리, 설정 파일로 관리) |
| Application 1001 (WER) | 전부 트리거 | **유형별**: LiveKernelEvent → `kernel-live`(상위 등급) / BEX·BEX64 → 유지 / APPCRASH·CLR20r3 → **1000과 ReportId·시각으로 중복 제거**, 짝 없는 경우만 트리거 / **RADAR_PRE_LEAK*·StoreAgentScan*·crashpad_log·FaultTolerantHeap → 트리거 안 함**(RADAR는 `leak` 신호로 버퍼에만 기록) |
| Application 1002 (응답 없음) | 없음 | **추가** → `hang-1002`, 증상 `freeze` |
| System BugCheck 1001 (BSOD 기록) | 없음 | **추가** → `bsod-1001` (41과 병합) |
| System 41 / 4101 / WHEA | 트리거 | 유지 |

30일 데이터로 역산(추정): 트리거 121회 → **약 21회**(실제 크래시 12 + 응답 없음 6 + 커널 라이브 2 + BEX 1), 전부 사용자 체감 사건.

### 3-2. 2단계: 유의성 점수 (덤프 시 계산, meta.significance 0~100 + 태그)

| 요소 | 점수 | 근거 |
|---|---|---|
| 커널급(LiveKernelEvent·41·BSOD) | 기본 90 | 시스템 전체 영향 |
| 사용자 대면 프로세스(창 있음 / 크래시 직전 전경) | +30 | 체감 여부 — 10초 스냅샷에 전경 PID 있음 |
| 오류 모듈이 서드파티(System32·Microsoft 서명 아님) | +20 | **충돌 가설의 핵심 단서** (s6 패턴) |
| 7일 내 같은 프로그램 재발 ≥2 | +15 | 일회성 제외 |
| 프로세스 수명 > 30초 | +15 / 수명 < 5초 −20 | 설치기·헬퍼·업데이터 제외 (`setup.exe`, `Update` 류) |
| 예외 코드 c0000005·c0000409·c000041d | +10 / e0434352(.NET 관리 예외) +0 | 네이티브 충돌 가능성 vs 앱 자체 버그 |
| denylist | −100 | 테스트 아티팩트 |

운영: 1단계 통과 트리거는 전부 덤프(건당 ~300KB, 30일 ~6MB 추정)하되 점수를 태그. UI 기본 필터 "주요(≥60)",
"참고(30~59)", "숨김(<30)". 점수는 **분석이 아니라 정렬 힌트**이며 엔진 입력에는 `significance`로 전달만 한다.

### 3-3. 상시 가동 (가장 효과 큰 항목)

실제 크래시 12건이 전부 수집기 OFF 때 났다. 로그온 시 자동 시작(작업 스케줄러 등록은 "설정 변경"이므로 **UI에서
사용자가 켜는 옵션**, 되돌리기 제공) + WPF 트레이 상주 + 비관리자 폴백 유지. 팀원 3 PC에서 2주 상시 가동이 11/27
실사례 수집의 전제다.

---

## 4. "간단한 문제"는 규칙으로 — Lv0~2 플레이북 후보 (엔진 rules.json 연동)

| 신호 조합 | 판정 | 조치(Lv) | 되돌리기 |
|---|---|---|---|
| 크래시 프로세스에 오버레이 DLL(Discord/Steam/NVIDIA) 로드 + 오류 모듈 서드파티 | 오버레이 충돌 | 오버레이 끄기(Lv1) | 켜기 |
| 백신 후킹 DLL이 오류 모듈 (aswhook 류) | 백신 훅 충돌 | 실시간 검사 10분 중지 후 재현(Lv2, 동의) | 자동 재개 |
| `slow` 마킹 창에서 특정 프로세스 CPU가 베이스라인 대비 +N%p | 상주물 과부하 | 프로세스 종료·시작 항목 해제 제안(Lv2) | 재시작 |
| 시작 프로그램 ≥ 15 & 부팅 직후 느림 | 시작 항목 과다 | 항목별 비활성 제안(Lv2) | 재활성 |
| 디스크 여유 < 10% 또는 busy 90%↑ 지속 | 디스크 병목 | 정리 안내(Lv0) | — |
| GPU Hot Spot 고온 + 클럭 하락 공존 | 열 스로틀 | 안내(Lv0): 팬·먼지·전력 설정 | — |
| 7일 내 RADAR_PRE_LEAK 같은 프로그램 ≥3 | 메모리 누수 의심 | 재시작·업데이트 안내(Lv0) | — |
| 설치·업데이트 직후(24h) 첫 크래시 | 변경 회귀 | 롤백 제안(Lv3, 승인) | 재설치 |

규칙은 결정적(같은 입력→같은 결과)이라 골든 셋으로 검증 가능하고, 에이전트는 규칙이 못 다루는 조합만 맡는다.

---

## 5. 조사 결과와 발전 방향 (출처 확인된 것만)

| 주제 | 출처 | 우리에게 의미 |
|---|---|---|
| 크래시 보고서 군집화(버킷팅) | ReBucket, Dang et al., ICSE 2012 — https://s-cube-xmu.github.io/uploads/ReBucket.pdf (MSR 소개: https://www.microsoft.com/en-us/research/?p=260382) | 호출 스택 유사도로 중복 크래시를 묶는 방법. 우리는 스택이 없어도 **(프로세스, 오류 모듈, 예외 코드) 버킷**으로 재발을 집계 → 유의성 점수의 "재발" 요소 |
| PC가 갑자기 느려지는 원인 | Basu et al., "Why Did My PC Suddenly Slow Down?", USENIX SysML 2007 — https://static.usenix.org/events/sysml07/tech/full_papers/basu/basu.pdf | 모든 실행 프로세스의 성능 카운터로 통계 모델을 만들면 단순 원인(자원 과점 프로세스)을 짚을 수 있다는 MSR 연구. 2절 설계(프로세스별 카운터 + 베이스라인 비교)의 직접 근거 |
| 후킹 엔진 충돌의 실재 | EnSilo 연구(2016): 15개+ 보안 제품의 후킹 코드 결함, Detours 포함 — https://www.helpnetsecurity.com/2016/07/19/flawed-code-hooking-engines/ | 보안 제품의 사용자 모드 후킹이 다른 주입 코드와 충돌한다는 1차 범위의 외부 근거 |
| AppInit_DLLs 위험 | Microsoft 권고 인용(Cimatron 문서) — https://help.cimatron.com/en/2026/Software_Conflict_Test.htm | user32 초기화 시 로드되는 DLL이 교착·성능 문제 유발 → 수집기가 AppInit_DLLs 값을 읽어 신호로 추가(읽기 전용) |
| WER 1001의 의미 | Microsoft TechNet wiki — https://learn.microsoft.com/en-us/archive/technet-wiki/3115.event-id-1001-windows-error-reporting ; 1002 — https://kb.eventtracker.com/evtpass/evtPages/EventId_1002_ApplicationHang_45605.asp | 1001은 보고서 상태 변화마다 기록(중복), 유형은 event name으로 구분 → 3-1 분류표의 근거 |
| 기존 도구 비교: osquery `windows_crashes` | https://fleetdm.com/tables/windows_crashes | 미니덤프 폴더를 테이블로 노출(사후). 우리 차별점: **크래시 전후 실시간 컨텍스트(링버퍼·ETW)** — 발표 "기존 도구와의 비교"에 추가 |
| 미니덤프 자동 분석 | mcp-windbg — https://pypi.org/project/mcp-windbg/ ; udmp-parser — https://github.com/0vercl0k/udmp-parser | WER LocalDumps가 켜진 앱은 미니덤프가 있다 → cdb `!analyze`로 스택을 뽑아 오류 모듈 근거를 강화하는 Lv0 도구 후보 (LocalDumps 활성화는 사용자 동의 필요) |

발전 방향 (우선순위):
1. **(즉시) 트리거 재설계 + 상시 가동** — 데이터가 가장 큰 결손을 가리킨다.
2. **(11월) 느려짐 진단** — Basu 2007 방식의 축소판: 프로세스별 카운터 베이스라인 비교. 교수님이 필요성을 인정한 유일한 항목.
3. **(11월) 크래시 버킷 + 재발 집계** — ReBucket의 아이디어를 스택 없이 적용.
4. **(선택) 미니덤프 스택 보강** — LocalDumps 동의 흐름 + cdb 자동 분석. 근거 품질 대폭 상승, 단 설정 변경이라 동의 UI 필수.
5. **(에이전트 팀과)** 로컬 도구 API — 덤프 검색 도구 3종이면 멀티 턴 구조 시작 가능.

---

## 6. 일정 (발표 자료 마일스톤에 맞춤)

| 마일스톤 | 수집기·통합 산출물 |
|---|---|
| ~10/30 | 트리거 분류표 구현(1002·BugCheck 추가, 1001 유형 필터, denylist) · 로그온 자동 시작 옵션(동의) · 팀 세팅 가이드(.NET 8 런타임) · FaultLab v1(crash/hang/leak/cpu) |
| ~11/13 | 유의성 점수 + UI 필터 · 느려짐 신호 수집(resource_top·startup·disk_health) + `slow` 증상 E2E · 케이스 내보내기(골든 셋) · 로컬 도구 API 초안(JSON 스펙) |
| ~11/27 | 팀원 3 PC 상시 가동 2주 → 실사례 수집·재분류 검증(트리거 수·유의성 분포) · Lv0~2 플레이북 엔진 연동 · 미니덤프 보강 여부 결정 |
| 12/11 | 시연 3종 + **느려짐 진단 시연**(금융보안 상주물 CPU 사례) |

프로그램 간 충돌 검증(매트릭스·실데이터 3층·전문성 등급)은 `docs/conflict-test-plan.md`로 분리(10/28).

## 7. 결정이 필요한 것 (팀)

1. 유의성 낮은 트리거도 덤프를 남길지(태그만, 기본 숨김) vs 아예 안 남길지 — 제안은 "남기되 숨김".
2. `slow` 증상·`resource_top` 등 스키마 확장은 엔진 담당과 협의 — 수집기 쪽에서 초안 PR로 제안.
3. 로그온 자동 시작은 설정 변경 → UI 옵션으로 사용자가 켜는 방식(기본 꺼짐)으로 할지.
4. 미니덤프(LocalDumps) 동의 흐름을 12월 범위에 넣을지.
