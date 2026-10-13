# ui/wpf/CrashAnalyzer.UI — 최종 UI (C# WPF) v1

Python 시안(`ui/prototype-python`)의 C# WPF 포팅 + **수집기 상주 통합** 1단계 (2026-10-12 결정 사항 이행).
UI는 판단하지 않는다 — 층·확률·순서·실행 방식은 전부 엔진(`crash_engine tool ...`)의 응답을 그대로 보여 준다.

## 빌드·실행

```powershell
# 선행: 엔진 빌드(engine/README.md), 수집기 빌드(dotnet build collector/BlackBox -c Release)
dotnet build ui/wpf/CrashAnalyzer.UI -c Release
ui\wpf\CrashAnalyzer.UI\bin\Release\net8.0-windows\CrashAnalyzerUI.exe
```

- 엔진 위치는 자동 탐색(`engine/build/Release/crash_engine.exe`), 다르면 `CE_ENGINE` 환경 변수.
- 덤프 탐색 루트는 `collector/` 전체, 다르면 `CE_DUMPS`.
- `--selftest [출력파일]` : GUI 없이 엔진 연결·진단 매핑 검증 (스모크/CI용).
- `--open <스냅샷.json>` : 시작하자마자 해당 스냅샷을 진단 (시연용).
- `--shot <저장.png> [--open ...]` : 창을 화면 밖에서 렌더해 PNG로 저장 후 종료 — 사용자 화면을
  캡처하지 않는 자가 검수용.

## 디자인 토큰 (App.xaml)

잉크 네이비 #182233(헤더) · 서피스 #F3F5F8 · 액센트 #2563C7 · 층 코드색(SW 블루 / 드라이버 퍼플 #6D4FC4 /
HW 앰버 #C97A2B) · 실행 방식 필(안내 회색 / 자동 블루 / 동의 앰버 / 직접 레드). 새 화면을 추가할 땐
`CardBox`·`SectionTitle`·`Dim`·`Faint`·`BtnPrimary/Secondary/Good/Ghost` 스타일을 재사용한다.

## v1 범위 (Python 시안 대비)

| 기능 | Python 시안 | WPF v1 |
|---|---|---|
| 진단 탭 (층 판정·후보·근거·점검 순서) | ○ | ○ |
| [최근 덤프 열기] / 스냅샷 열기 | ○ | ○ |
| 수동 마킹 unknown 증상 입력 다이얼로그 (원본 보존, 사본 진단) | ○ | ○ |
| 세션: 확인(solved/not_solved/skipped) → 해결(apply_fix) → 검증(사후 스냅샷/응답) | ○ | ○ |
| **수집기 상주: UI 안에서 시작/중지 + 로그 + 새 덤프 자동 감지·자동 진단** | × | **○ (신규)** |
| 상담 창 (LLM 에이전트) | ○ | × — v2 (Anthropic C# SDK 연동 예정) |
| 자동 점검 모드 표시/실행기 | 표시만 | × — 허용 범위 확정 후 |
| 트레이 최소화·부팅 시 시작 | × | × — v2 |

메모: 수집기는 UI를 띄운 권한을 상속한다 — ETW·모듈 추적까지 쓰려면 UI를 관리자로 실행
(머리글 오른쪽에 현재 권한 표시). 수집기 중지는 q 입력으로 정상 종료라 진행 중 덤프도 드레인된다.
