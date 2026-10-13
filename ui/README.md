# ui — 안내형 인터페이스

PC Crash Analyzer 7조 / 송민석 (2026-10-12)

| 폴더 | 내용 | 상태 |
|---|---|---|
| `prototype-python/` | Python(tkinter) 시안 — 진단 결과 / 원인 확인 / 해결·검증 탭, 상담 창(LLM 에이전트), 수집기 덤프 열기 | 동작함. **시연·비교용** |
| `wpf/` | **최종 UI** — 수집기와 같은 C#(.NET 8) WPF. 시안 화면 + **수집기 상주 통합**(UI 안에서 시작/중지·새 덤프 자동 진단) | **v1 동작함** (`wpf/CrashAnalyzer.UI/README.md`) — 남은 것: 상담 창, 트레이, 자동 점검 실행기 |

결정 (2026-10-12): 수집기가 C#(.NET 8)이고 UI·통합을 수집기 담당과 함께 맡으므로 최종 UI 는 C# WPF 로 만든다.
Python 시안은 C# 버전이 기능을 다 따라잡을 때까지만 두고, 새 기능은 C# 버전에만 추가한 뒤 지운다.

두 버전 모두 판단은 하지 않는다. 엔진(`engine/build/Release/crash_engine.exe tool ...`)을 콘솔 창 없이 불러
JSON 결과를 그대로 보여 준다(`engine/docs/tool_api.md`).
