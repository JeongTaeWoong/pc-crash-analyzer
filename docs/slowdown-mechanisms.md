# 작업 중 "점점 느려짐 → 다운" — 원인 메커니즘별 감지 가능 범위 (2026-10-28)

대상: Blender·Maya·CAD·After Effects·Unity 같은 장시간 작업 프로그램에서 "점점 느려지다 멈추거나 꺼지는" 증상.
정태웅 10/28 요구 3건에 대한 정리다.

> 1. 원인 메커니즘별로 감지 가능한 부분을 정리하고 관련 자료를 조사
> 2. 실제 작업하는 지인들에게 프로그램을 제공해서 데이터 수집
> 3. 개인 프로그램이나 예측 못한 커스텀 작업이 있을 것

핵심 결론 세 줄:
- 이 증상은 **이벤트가 아니라 추세**다. 5분 링버퍼로는 못 잡고, 분 단위 롤업에 "프로세스별 메모리 기울기"가 들어가야 한다.
- 다섯 메커니즘 중 **A(앱 메모리 증가)·C(GPU 열)·D(TDR)·E(외부 간섭)는 지금 스택으로 감지 가능**하고, **B(VRAM)는 어댑터 총량만 신뢰 가능**(프로세스별 카운터는 Microsoft가 오류를 인정한 상태).
- 프로그램 이름에 의존하는 설계는 3번 요구 때문에 처음부터 버린다. 신호는 전부 **프로그램 무관(generic)**이어야 하고, 프로그램 지식은 엔진의 플레이북에만 둔다.

이 PC 10/28 스파이크(읽기 전용, 5초)로 각 카운터가 실제 값을 내는지 확인한 결과를 ○/△/✕로 표시했다.

---

## 1. 메커니즘별 감지 가능 범위

### A. 앱 자체 메모리 증가 (실행 취소 이력·캐시·씬 누적·에디터 리로드 잔류)

| 신호 | 출처 | 이 PC 확인 | 비고 |
|---|---|---|---|
| 프로세스 Private Bytes·Working Set | PerfProc `\Process(*)\Private Bytes` | ○ (Unity 에디터 4,697 MB, NVIDIA Overlay 3,308 MB 관찰) | 이미 10초 샘플 수집 중. **기울기(MB/분)** 계산이 추가 필요 |
| 프로세스 핸들 수 | `\Process(*)\Handle Count` | ○ | 핸들 누수는 메모리와 별개로 느려짐 유발 |
| 시스템 커밋 | `\Memory\Committed Bytes`, `Commit Limit`, `% Committed Bytes In Use`, `Available MBytes`, `Pages/sec` | ○ 전부 | 커밋 한계 접근 = OOM 직전 신호. 이 PC: RAM 93.7 GB, 페이지파일 6,280 MB(피크 5,092 MB) |
| 앱 자체 메모리 경고 | AE "Unable to allocate memory" [A1], Maya 크래시 로그의 free memory/free swap 필드 [A4] | – | 앱마다 다름 → 수집기가 아니라 **엔진 플레이북 지식** |

벤더 공식 근거:
- After Effects: 메모리·디스크 캐시 환경설정, "다른 응용 프로그램용 RAM 예약" [A1]; 메모리 할당 오류 시 1차 조치 = RAM 예약 조정 → Edit > Purge > All Memory & Disk Cache → 서드파티 플러그인 제거 [A2]
- Blender: 실행 취소 단계(기본 32)와 메모리 한도(기본 0 = 무제한) 환경설정 [A3] — 현재 버전의 메뉴 위치는 버전마다 달라 **미확인**
- Maya: 크래시 리포트(.dmp)는 %TEMP%에 생성, 로그에 메모리 수치 포함 [A4] — Autodesk 공식 "메모리 부족" 문서는 **못 찾음**
- Unity: Editor.log `%LOCALAPPDATA%\Unity\Editor\Editor.log`, 크래시 `%TMP%\Unity\Editor\Crashes` [A5]. **도메인 리로드 메모리 누적은 공식 문서 미확인**(커뮤니티 보고 수준)

**이 PC 실사례(10/28 발견):** `%TMP%\Unity\Editor\Crashes\Crash_2026-10-10_072938424\`에 crash.dmp(1.9 MB)+Editor.log가 있고, 같은 날 16:29(KST, 폴더명은 UTC)에
Application 1000 "Unity.exe 6000.4.9 / 오류 모듈 Unity.dll"이 기록돼 있다. 즉 **앱 자체 크래시 폴더와 WER 이벤트가 1:1로 대응**한다. 그때 수집기는 꺼져 있었다.

### B. VRAM 압박

| 신호 | 출처 | 이 PC 확인 | 비고 |
|---|---|---|---|
| 어댑터 전용 메모리 사용량 | `\GPU Adapter Memory(*)\Dedicated Usage` (VidMm) | ○ (2,796 MB / 3 어댑터 인스턴스) | **신뢰 가능**. 총량 대비 비율로 압박 판정 |
| 프로세스별 GPU 메모리 | `\GPU Process Memory(*)\Dedicated Usage` | △ 값은 나오나 한 PID가 **8,646,276 MB**로 비정상 | Microsoft 공식: Windows 10 1709+에서 이 카운터가 누수처럼 잘못된 값을 보임 [B1]. **미지원**으로 분류, 어댑터 총량만 사용 |
| GPU 엔진 사용률 | `\GPU Engine(*)\Utilization Percentage` | ○ | 작업 부하 맥락용 |
| LibreHardwareMonitor GPU Memory Used/Total | LHM 0.9.7 | ○ **10/29 검증**(값은 바이트) | 주 GPU 의 사용량/총량 → `resource_trends.gpu_mem_pct_max`, 엔진 B 규칙(≥90%) 구현 |

### C. 열 스로틀링

| 신호 | 출처 | 이 PC 확인 | 비고 |
|---|---|---|---|
| GPU 온도·클럭·전력 2초 | LHM(구현됨) | ○ (10/09 30분 실측) | "온도↑ → 클럭↓" 패턴은 수집 중 |
| GPU 스로틀 사유 플래그 | LHM | ✕ **미지원 확정(10/29)** | LHM 0.9.7-pre749 에 센서 없음 — 온도·클럭 공존으로 추론 |
| CPU 속도 제한 이벤트 | System, `Microsoft-Windows-Kernel-Processor-Power` **Event 37** "프로세서 속도가 시스템 펌웨어에 의해 제한" [C1] | ○ 구독 가능 (30일 내 0건) | CPU 센서가 막힌 이 PC에서 **유일한 CPU 스로틀 신호**. Microsoft는 열이 아니라 전원 정책이 원인일 수 있다고 함 → "열"로 단정하지 않고 "속도 제한"으로만 기록 |
| CPU 온도 | LHM | ✕ 미지원 (WinRing0 차단, 10/09 확정) | 지인 PC에서는 될 수 있음 → `unavailable` 사유를 그대로 남김 |

### D. GPU 드라이버 TDR·크래시

| 신호 | 출처 | 이 PC 확인 | 비고 |
|---|---|---|---|
| 디스플레이 드라이버 응답 중지·복구 | System **4101** [D1] | ○ 트리거 구현됨. **10/29 과거 로그 재검증으로 실기록 7건 해석 확인**(nvlddmkm), 같은 시각 LiveKernelEvent 0x141 동반 | TdrDelay 기본 2초·TdrDdiDelay 5초·TdrLevel 3 [D2]; 이 PC는 기본값(레지스트리 값 없음) |
| TDR 복구 실패 → BSOD | BugCheck 0x116 VIDEO_TDR_FAILURE [D2] → System 1001 BugCheck | ○ 트리거 구현됨, 사례 없음 | |
| 앱 크래시의 오류 모듈이 **드라이버 DLL** | 1000 [3]/[11] | ○ 수집 중 | **점수기 결함**: 경로가 `\Windows\System32\DriverStore\` 아래라 현재 "시스템 모듈"로 취급돼 가산 없음 → **GPU 드라이버 범주 신설 필요**(아래 3절) |

### E. 외부 간섭 (백신 실시간 검사·오버레이 후킹·상주물)

`docs/conflict-test-plan.md`에서 다룬다. 느려짐 관점에서 추가되는 것: 캐시 폴더(AE 디스크 캐시·Unity Library·Blender 임시)에 대한 백신 검사는 **파일 쓰기가 많은 앱일수록** 체감이 크다 — JetBrains 공식 문서의 논리가 그대로 적용된다 [E1].

---

## 2. "느려짐 종류"를 가르는 증거 조합 (엔진 플레이북 입력)

수집기는 아래 다섯 값을 분 단위로 남기고, 분기는 엔진이 한다.

| 판정 | 전경 프로세스 메모리 기울기 | 시스템 커밋 % | GPU 어댑터 메모리 % | GPU 온도·클럭 | 상위 CPU 프로세스 | 처방 방향(안내만) |
|---|---|---|---|---|---|---|
| A 앱 메모리 증가 | **양(+)·단조** | 상승 | – | – | 전경 앱 자신 | 저장 후 재시작, 캐시 비우기, 실행 취소 한도(앱별 플레이북) |
| B VRAM 압박 | – | – | **90%+** | 정상 | – | 뷰포트·텍스처 해상도, 미리보기 범위 |
| C 열 스로틀 | – | – | – | **온도↑ 클럭↓** 또는 Event 37 | – | 냉각·전원 모드. CPU 온도 미지원 PC는 "속도 제한 발생"까지만 |
| D TDR | – | – | 높을 수 있음 | – | – | 4101 뒤 앱 다운 → 드라이버 버전·DriverStore 모듈 지목 |
| E 외부 간섭 | 평탄 | – | – | – | **전경 앱이 아닌 것**(MsMpEng·상주물) | 충돌 프로그램 종료·제외(사용자가 직접) |

하나로 안 떨어지면 복수 가설을 그대로 보여준다(엔진 쪽 설계와 일치).

---

## 3. 수집기 변경 목록 (11/13 롤업 설계에 반영)

| # | 변경 | 근거 | 상태 |
|---|---|---|---|
| S1 | 분 단위 롤업에 **프로세스별 Private Bytes·Handle Count** 보존(상위 N + 전경) → 덤프 시 최근 60분 기울기 | A | **구현(10/29)** `MinuteRollup` 24h, 덤프 `rollup24h` + `resource_trends` |
| S2 | `\Memory\Committed Bytes`·`Commit Limit`·`Available MBytes` 10초 샘플 + 롤업 | A | **구현(10/29)** `ResourceSource` |
| S3 | `\GPU Adapter Memory(*)\Dedicated Usage` 10초 샘플 (프로세스별은 **미지원**으로 기록) | B, [B1] | **구현(10/29)** `ResourceSource` |
| S4 | System Event 37(Kernel-Processor-Power) 구독 → 트리거 아님, 버퍼 이벤트 | C, [C1] | **구현(10/29)**; 엔진은 37을 power 근거로 |
| S5 | `SignificanceScorer`: 오류 모듈 경로가 `\DriverStore\` 또는 알려진 GPU 드라이버 DLL이면 `gpu-driver-module` 태그(+20) | D | **구현(10/29)** + 테스트 3건 |
| S6 | 덤프 시 **앱 자체 크래시 로그의 경로·크기·시각만** 기록(Unity Crashes·`%TEMP%\*.crash.txt`·WER ReportArchive). 내용은 읽지 않음 | A, [A3][A4][A5] | **구현(10/29)** `snapshot.appCrashLogs`, 라이브 확인 |
| S7 | 마킹 시 **한 줄 메모** 입력(선택) → `meta.userNote` | 요구 3 | UI 측(송민석)과 협의 |
| S8 | 프로세스 스냅샷에 **명령줄 인수**를 넣을지 결정 — 커스텀 스크립트(python.exe·node.exe)의 정체는 명령줄에만 있음 | 요구 3 | **결정 필요**: 개인정보 위험(경로·토큰) vs 진단 가치. 넣는다면 스크립트 파일명만 |

---

## 4. 지인에게 배포해 수집할 때 수집기가 갖춰야 할 것 (요구 2)

이 PC에서만 돌리던 것을 남의 PC에 두는 순간 요구가 달라진다.

| 항목 | 지금 | 필요 | 담당 |
|---|---|---|---|
| 설치 | 소스 빌드 | **단일 폴더 self-contained 배포**(.NET 런타임 포함, 남건호 PC 사례 재발 방지) — **구현(10/29)** `tools/publish-collector.ps1` → `dist/BlackBox-<ver>-win-x64.zip` | 수집기 |
| 서명 | 없음 | 없음(비용) → SmartScreen 경고를 안내문에 미리 적음 | 문서 |
| 권한 | 관리자 권장 | 비관리자에서도 **A·B·C·E 전부 동작**, ETW 모듈만 `unavailable` — 지인에겐 비관리자 기본 | 수집기(이미 그렇게 동작, 확인만) |
| 상시 가동 | 수동 실행 | 로그온 자동 시작(동의 체크) | UI(송민석) |
| 오버헤드 | 0.13% CPU, 2.1 MB | 롤업 추가 후 재측정 — **10분 0.31% CPU, 94MB(10/29)**; 롤업 24h 상한 2MB(테스트) | 수집기 |
| 수집 범위 고지 | 없음 | 1쪽 안내 — **작성(10/29)** `docs/participant-guide.md`(zip 에 `읽어주세요.md` 로 포함) | 문서 |
| 사용자 라벨 | 없음 | 마킹 시 메모(S7) + 2주 후 설문 5문항(`conflict-test-plan.md` 3.3) | UI·문서 |
| 회수 | 폴더 수동 전달 | `dumps/` 를 참여자가 열어본 뒤 zip → `dumps_archive/`(gitignore)에만 | 문서·절차 |
| 삭제권 | – | 참여자가 언제든 폴더 삭제 가능, 우리 측 사본도 요청 시 삭제 | 문서 |
| 지원 | – | 문제 시 연락 창구 1개, 수집기 자체 로그(`logs/`, gitignore) | 수집기 |

배포 우선 대상: **Blender·Unity·AE 실사용자**(A·B·D), **노트북 사용자**(C — Event 37이 노트북에서 흔함), **금융·보안 모듈 많은 사무 PC**(E).

---

## 5. 예측 못한 프로그램·커스텀 작업에 대한 설계 원칙 (요구 3)

1. **프로그램 목록 금지.** 수집기 어디에도 "Blender면 이렇게"가 없다. 모든 신호는 프로세스 무관(메모리·핸들·GPU·이벤트·모듈). 프로그램 지식은 엔진 플레이북(rules.json)에만.
2. **모르는 모듈이 기본값.** 점수기는 이미 "알 수 없는 모듈 +10, 서드파티 경로 +20"으로 미지 모듈을 다룬다. 서명 없는 개인 프로그램은 `company` 빈값 → 엔진이 "미확인 서드파티"로 표시.
3. **호스트 프로세스 뒤의 정체.** 커스텀 작업은 python.exe·node.exe·cmd.exe·powershell.exe 같은 호스트로 보인다. 모듈 목록(ETW)으로 로드된 네이티브 확장(.pyd·.node)은 보이지만, 스크립트 이름은 명령줄에만 있다 → S8 결정.
4. **플러그인 생태계.** Blender 애드온(Python)·Maya 스크립트(MEL/Python)는 DLL이 아니라 모듈 목록에 안 뜬다. 이 경우 수집기가 줄 수 있는 건 앱 자체 크래시 로그의 **위치**(S6)이고, 내용 해석은 사용자가 엔진 UI의 안내를 따라 직접 연다.
5. **사용자 메모가 최고의 라벨.** "렌더 걸어놓고 유튜브 보던 중", "애드온 X 켠 직후" 같은 한 줄이 모듈 100개보다 가설을 좁힌다(S7). 키 입력 수집이 아니라 사용자가 의도적으로 쓰는 메모라 원칙 6에 어긋나지 않는다.
6. **"모름"을 결과로 낸다.** 다섯 메커니즘 어디에도 안 맞으면 엔진은 "증거 부족 — 다음에 느려질 때 `m`을 눌러 달라"로 끝낸다. 억지 가설보다 낫고, 그 자체가 L3 데이터다.

---

## 6. 다음 단계

| 시점 | 할 일 |
|---|---|
| ~11/5 | ~~S5, S2·S3·S4~~ **10/29 완료**. 남은 것: LHM GPU 메모리·스로틀 사유 스파이크(스파이크 E 재빌드) |
| ~11/13 | ~~S1 롤업, 오버헤드 재측정, self-contained 빌드, 안내문~~ **10/29 완료**. 남은 것: S6(앱 크래시 로그 경로), 30분 조용한 조건 오버헤드 재측정, 엔진 `slow` UI 노출(송민석) |
| ~11/27 | Unity·Blender 1시간 세션으로 A·B 기울기 실측(이 PC), 지인 PC 1차 회수 |
| 팀 결정 | S7 메모 UI, S8 명령줄 수집 여부 |

---

## 출처

- [A1] Adobe Help, After Effects "Memory and storage" — https://helpx.adobe.com/after-effects/using/memory-storage1.html
- [A2] Adobe Help, "Unable to allocate memory | After Effects" — https://helpx.adobe.com/after-effects/kb/allocate-memory-error.html
- [A3] Blender Manual, "Troubleshooting › Crashes"(crash.txt 위치) — https://docs.blender.org/manual/en/4.1/troubleshooting/crash.html ; 실행 취소 단계·메모리 한도는 구 매뉴얼(2.6) 기준 — 현재 버전 **미확인**
- [A4] Autodesk Maya 포럼(크래시 리포트 .dmp 위치·로그 메모리 필드) — 커뮤니티 — https://forums.autodesk.com/t5/maya-forum/where-is-the-crash-log-for-maya-2016/m-p/6274394
- [A5] Unity Manual, "Log files" — https://docs.unity.com/en-us/engine/6000.0/manual/get-started/project-configuration/log-files
- [B1] Microsoft Learn, "GPU process memory counters report incorrect values" — https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/gpu-process-memory-counters-report-wrong-value
- [B2] Microsoft DirectX Developer Blog, "GPUs in the Task Manager"(VidMm·VidSch 출처 설명) — https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/
- [C1] Microsoft Support, "Kernel processor power Event ID 37" — https://support.microsoft.com/en-us/help/4043839
- [D1] Microsoft TechNet(아카이브), "Event ID 4101 — Display Driver Timeout Detection and Recovery" — https://technet.microsoft.com/cc756372(v=ws.10)
- [D2] Microsoft(아카이브 MSDN), "Testing and debugging TDR during driver development"(TdrDelay·TdrDdiDelay·TdrLevel 기본값) — https://msdn.microsoft.com/en-us/library/ff569918.aspx
- [E1] JetBrains Support, "Antivirus Impact on Build Speed" — https://intellij-support.jetbrains.com/hc/en-us/articles/360006298560
