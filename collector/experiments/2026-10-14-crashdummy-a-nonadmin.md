# 실험 기록 — CrashDummy a, 비관리자 (2026-10-14 01:10, 남건호 PC)

## 목적
새 PC(남건호)에서 수집기 BlackBox가 앱 크래시를 자동 감지해 덤프를 만드는지 확인.

## 환경
| 항목 | 값 |
|---|---|
| OS | Windows 10 (10.0.19045) |
| 권한 | **비관리자** (`isElevated: false`) |
| .NET | 수집기 net8.0 빌드, 실행 PC에는 런타임 6.0.5 / 10.0.8만 설치되어 있었음 → 8.0 런타임 미설치 상태에서 시작 시 실행 실패 (아래 「발견」) |
| 소스 상태 | eventlog ok · sensors ok (2s) · processes ok (10s) · hotkey ok · **etw-kernel unavailable** (관리자 필요) |

## 절차
1. `collector\BlackBox` 에서 `dotnet run -c Release` (비관리자 터미널)
2. 다른 터미널에서 `CrashDummy.exe a` (즉시 unhandled exception)

## 결과
| 항목 | 값 |
|---|---|
| 크래시 시각 (이벤트 1000 EventTime) | 01:10:21.150 |
| 수집기 수신 | 01:10:21.154 (**약 4ms**) |
| 마킹 | 01:10:21.194, kind=crash-1000 |
| 1001 (WER) | 01:10:23.150 수신 → 쿨다운 내 병합 (의도된 동작) |
| 덤프 완료 | 01:10:51.357 (마킹 후 +30.16초, 설계값 30초) |
| 덤프 이벤트 수 | 110 |
| 프로세스 수명 | 약 0.29초 (시작 01:10:20.858) |
| 예외 코드 / 오류 모듈 | 0xe0434352 (.NET 예외) / KERNELBASE.dll |

`engine-snapshot.json` 에 들어간 것: symptom(app_crash, single_app, CrashDummy.exe, faulting_modules), 프로세스 목록, 드라이버 2종(RSFDrv, TKFWFV), 입력장치 필터, 보안(Defender), 설치 이력, 이벤트 2건, GPU 센서 샘플.

**빠진 것: 크래시 프로세스의 로드 모듈 목록.** 비관리자라 ETW ImageLoad가 꺼져 있고, 수명 0.29초라 10초 주기 스냅샷에도 잡히지 않음 — `docs/collector-status.md` 2절의 "a / 주기 스냅샷 폴백 0/3" 과 같은 결과를 이 PC에서도 재현.

## 발견
- 수집기 실행 PC에 **.NET 8 런타임이 없으면** `dotnet run` 이 "Framework 'Microsoft.NETCore.App', version '8.0.0' not found" 로 실패한다 (SDK 10으로 빌드는 됨). 팀원 PC 세팅 안내에 .NET 8 런타임 설치를 넣을 필요가 있음.

## 다음
- 관리자 터미널에서 a/b/c × 3회 재측정 → 모듈 목록 확보율 비교 (비관리자 대비)
- 원본 덤프는 개인 PC 정보(프로세스·설치 이력)가 있어 저장소에 올리지 않음 (`.gitignore` 의 `dumps/`). 필요하면 요청 시 개별 전달.
