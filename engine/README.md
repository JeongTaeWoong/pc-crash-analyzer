# engine — 추론 엔진

PC Crash Analyzer 7조 / 담당: **송민석** — 층 판정, 후보 점수화, 시도 순서 최적화
(+ 멘토링 1차 반영: 원인 확인 → 해결 안내 → 검증 세션, 에이전트용 도구 인터페이스, 자동 점검 모드 판단)

수집기가 덤프한 스냅샷을 읽고 다음을 계산한다.

1. **어느 층의 문제인지** (소프트웨어 / 드라이버 / 하드웨어)
2. **원인 후보 순위와 근거** (근거 그래프)
3. **어떤 순서로 확인해야 가장 적은 비용으로 원인에 도달하는지**
4. 원인이 확인되면 **어떻게 해결하는지**, 그리고 각 조치를 **자동으로 해도 되는지**
5. 해결책을 적용한 뒤 **정말 해결되었는지** (사후 스냅샷으로 재발 검증)

C++17 표준만 사용하고 외부 라이브러리는 없다(JSON 파서 포함). 코드는 C식 `for (int i = 0; ...)`
반복문으로 썼고, 범위 기반 for·람다·`auto` 는 쓰지 않았다.

## 멘토링 1차(9/27)에서 바뀐 점

| 멘토링 내용 | 엔진에서 한 일 |
|---|---|
| 2.1 LLM 에이전트 결합 권장 | 엔진을 **도구(tool)** 로 노출 (`crash_engine tool ...`, `docs/tool_api.md`). 추론은 엔진이, 설명·조율은 에이전트가 맡도록 역할을 나눔 |
| 2.2 실제 구현으로 범위 검증 | 수집기 MVP 신호(이벤트 1000/1001, UpperFilters, 로드된 DLL, 센서 가용 여부)만 있어도 동작하도록 확인 (시나리오 s6) |
| 2.3 핀포인트 범위 선정 | 1차 범위를 **보안 프로그램 충돌**(안티치트·백신·금융 보안모듈·보안 방화벽)로 정하고, 후보마다 `in_focus` 표시. 보안 시나리오 4건 작성 |
| 2.3 사례 1건 끝까지 | **세션**: 증상 → 원인 확인 → 해결 안내 → 실제 해결(검증)까지 이어지는 흐름 구현. 안티치트 + 게임 크래시 시나리오로 끝까지 동작 |
| 2.4 / 지도교수님: 완전 자동화 | **안내 모드**(기본)와 **자동 점검 모드**(선택). 되돌릴 수 있고 허용한 조치만 `auto`, 되돌리기 어려운 조치는 항상 `confirm` |
| 지도교수님: 단순 결합을 넘는 공학적 시도 | 층 판정·점수화·비용 가중 그룹 테스팅·베이즈 갱신·검증 로직은 모두 팀이 직접 구현 |

## 빌드

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure     # 또는 build/ce_tests data
```

Windows(Visual Studio)에서는 실행 파일이 `build\Release\crash_engine.exe` 에 생긴다.
아래 예시의 `./build/crash_engine` 을 그 경로로 바꿔 쓰면 된다. 콘솔 한글이 깨지지 않도록
실행 시 UTF-8 출력으로 바꿔 둔다.

## 실행

```bash
# 1) 진단 1회 (상태 저장 없음)
./build/crash_engine diagnose data/samples/s1_anticheat_crash.json --compare
./build/crash_engine diagnose data/samples/s3_av_launch_fail.json --mode auto

# 2) 세션: 확인 → 해결 → 검증 (시연 절차는 docs/scenario_walkthrough.md)
./build/crash_engine session start  data/samples/s1_anticheat_crash.json --session s1_session.json
./build/crash_engine session report --session s1_session.json --check <next_check id> --result not_solved
./build/crash_engine session report --session s1_session.json --check pause_anticheat@anticheat:vgk.sys --result solved
./build/crash_engine session apply-fix --session s1_session.json --at 2026-10-15T20:55:00+09:00
./build/crash_engine session verify --session s1_session.json --after data/samples/s1_anticheat_after_fix.json

# 3) 에이전트·UI용 도구 (JSON 입출력)
./build/crash_engine tool list
echo '{"snapshot_path":"data/samples/s2_finsec_input_loss.json"}' | ./build/crash_engine tool explain

# 4) 검증 지표 / 피드백 / 규칙 내보내기
./build/crash_engine bench data/samples/s1_anticheat_crash.json data/samples/s2_finsec_input_loss.json \
    data/samples/s3_av_launch_fail.json data/samples/s4_display_util_flicker.json \
    data/samples/s5_thermal_fps_drop.json data/samples/s6_mvp_av_hook_crash.json
./build/crash_engine export-rules my_rules.json          # 실험·피드백용 사본 (data/rules.json 원본은 그대로 둔다)
./build/crash_engine feedback data/samples/s1_anticheat_crash.json --candidate anticheat:vgk.sys --result solved --rules my_rules.json
```

## 팀 인터페이스

| 경계 | 형식 | 문서 |
|---|---|---|
| 수집기(정태웅) → 엔진 | 스냅샷 JSON (MVP 최소 구성 표 포함) | `docs/snapshot_schema.md` |
| 엔진 → 에이전트(LangGraph 등, 정태웅 검토) | 도구 7종, JSON stdin/stdout | `docs/tool_api.md`, `docs/tool_specs.json`, `tools/ce_tools.py` |
| 엔진 → 자동 점검 실행기 | `action.execution`(auto/confirm/manual/guide) + `action.automation` 명령 | `docs/tool_api.md` 「실행기 약속」 |
| 엔진 → UI | `diagnose` 결과의 `candidates[].evidence` = 근거 그래프 노드 | `docs/engine_design.md` |
| 사례 DB·실험(남건호) ↔ 엔진 | `data/rules.json` 의 `cases`, 시나리오의 `ground_truth` | `docs/rules_rationale.md` |

엔진은 스냅샷 JSON만 읽으므로 수집기는 C#·C++ 어느 쪽으로 만들어도 된다.
라이브러리(`ce_core`)로 링크해 `ce::diagnose()`, `ce::Session` 을 직접 불러도 된다.

## 디렉터리

```
include/ce/   json, snapshot, rules, evidence, signals, layer, candidates, planner,
              remedy(조치 결정), engine, session(확인→해결→검증), tool(에이전트 도구), feedback, bench
src/          위 헤더의 구현 + main.cpp(CLI). rules_default.cpp 는 data/rules.json 에서 자동 생성
data/rules.json      가중치·점검·조치·해결책·사례 DB (실험으로 갱신하는 값)
data/samples/        시나리오 6종 + 사후 검증 스냅샷 1종 (아래 표)
docs/                설계 · 스냅샷 스키마 · 도구 API · 시연 절차 · 규칙 근거
tools/               gen_rules_default.py(규칙 내장본 생성), ce_tools.py(파이썬 래퍼)
tests/               의존성 없는 assert 테스트
```

| 시나리오 | 내용 | 범위 |
|---|---|---|
| s1_anticheat_crash (+ s1_anticheat_after_fix) | 다른 게임용 커널 안티치트가 무관한 게임을 튕기게 함. 사후 스냅샷으로 해결 검증 | 1차 범위 |
| s2_finsec_input_loss | 은행 사이트에서 깔린 키보드 보안모듈 필터로 게임 입력 누락 | 1차 범위 |
| s3_av_launch_fail | 게임 업데이트 직후 백신이 실행 파일을 오탐·격리 | 1차 범위 |
| s6_mvp_av_hook_crash | 수집기 MVP 신호만으로 백신 훅 DLL 크래시 진단 | 1차 범위 |
| s4_display_util_flicker | 모니터 유틸 설치 후 화면 깜빡임 | 회귀 확인용 |
| s5_thermal_fps_drop | 과열 스로틀링으로 프레임 저하 | 회귀 확인용 |

시나리오 파일은 **실험용으로 만든 데이터**이며, 실제 PC에서 수집한 값으로 차례로 바꿔 나간다.

## 현재 상태 (2026-10-01)

* 테스트 102건 통과.
* 시나리오 6건: 층 판정 6/6, Top-1 6/6 (1차 범위 4건 모두 Top-1).
* 평균 누적 비용: 커뮤니티 표준 순서 103.7분 → v1 14.6분 (점검 신뢰도 1 가정).
* 6건이 모두 1순위 정답이라 v0(1순위부터 바로 확인)가 v1보다 약간 유리하게 나온다.
  v1은 1순위가 틀릴 가능성까지 고려해 **기대 비용**을 줄이는 방식이므로(기대 비용: v0 20.5분 / v1 20.4분),
  실제 사례 20~30건이 모인 뒤 두 값을 함께 비교해야 한다.
* 가중치·비용·해결률은 팀 추정치다. 제어 실험 측정 후 `data/rules.json` 을 갱신한다.

## 다음 작업

1. 수집기 MVP 실제 스냅샷으로 시나리오 s1 재현 (정태웅·남건호와 스키마 맞추기)
2. 에이전트 프레임워크 선정 결과에 맞춰 도구 스키마 조정 (정태웅 초안 확인 후)
3. 자동 점검 모드 허용 범위 초안과 `actions[].reversible / auto_default` 값 맞추기
4. 중간발표(10/16) 자료: 세션 시연 + 지표 표
