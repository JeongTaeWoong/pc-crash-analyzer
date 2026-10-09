# 수집기 → 엔진 파이프라인 실행 (E2E)

수집기(BlackBox)가 마킹 시 덤프 폴더에 `engine-snapshot.json`(엔진 입력 규격, `engine/docs/snapshot_schema.md` v1.1)을
함께 쓰므로, 크래시 → 자동 덤프 → 진단이 명령 두 개로 이어진다. 2026-10-09 실측으로 전 구간 동작 확인.

## 1) 빌드

```bash
# 엔진 (C++17, VS2022)
cmake -S engine -B engine/build -G "Visual Studio 17 2022" -A x64
cmake --build engine/build --config Release        # → engine/build/Release/crash_engine.exe

# 수집기 (.NET 8)
dotnet build collector/BlackBox -c Release
```

## 2) 수집 → 진단

```powershell
# 관리자 터미널 권장 (ETW·fltmc·모듈 커버리지가 좋아짐. 비관리자도 동작하되 해당 신호는 unavailable로 기록)
cd collector/BlackBox
dotnet run -c Release          # m+Enter=수동 마킹, Ctrl+Shift+F12=전역 단축키, q=종료
# 앱 크래시(이벤트 1000/1001)·전원 이상(41)·TDR(4101)·WHEA는 자동으로 마킹된다

# 덤프가 생기면 (dumps/<시각>-<종류>/)
../../engine/build/Release/crash_engine.exe diagnose dumps/<폴더>/engine-snapshot.json
```

## 덤프 구성

| 파일 | 용도 |
|---|---|
| engine-snapshot.json | **엔진 입력** (스키마 v1.1, 실패 항목은 필드 생략) |
| events.json | 링버퍼 원본 (마킹 -5분 ~ +30초 전체 신호) |
| snapshot.json | 마킹 시점 상태 (프로세스·모듈, 드라이버, 센서, 설치 이력, 크래시 프로세스 모듈 매칭 근거) |
| meta.json | 트리거 원문, 소스 상태, 버퍼 통계 |

## 현재 한계 (collector/NOTES.md, docs/collector-status.md 상세)

- `symptom.scope/started_at`은 트리거 종류로 추정해 채운다 — 실제 제품 흐름에선 UI가 사용자에게 받아야 정확 (스키마 명세)
- 수동 마킹의 symptom.type은 `unknown` — UI 연동 전까지 엔진 근거가 제한됨
- CPU/보드 센서 미지원(현 보안 구성), GPU 센서만 수집. `sensors.samples`에 gpu_temp/gpu_clock만 들어감
