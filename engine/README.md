# engine — 추론 엔진

PC Crash Analyzer 7조 / 담당: **송민석** — 층 판정, 후보 점수화, 시도 순서 최적화
(+ 멘토링 1차 반영: 원인 확인 → 해결 안내 → 검증 세션, 에이전트용 도구 인터페이스, 자동 점검 모드 판단)
(+ 지원 범위 확장: 1차 보안 프로그램 → 2차 드라이버·시스템 → 3차 하드웨어)

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
| 2.3 핀포인트 범위 선정 | 1차 범위를 **보안 프로그램 충돌**(안티치트·백신·금융 보안모듈·보안 방화벽)로 정하고, 후보마다 `stage`(1차/2차/3차/범위 밖) 표시. 보안 시나리오 4건 작성 |
| 3.3 확장 순서 (2차 드라이버·시스템 → 3차 하드웨어) | 블루스크린 덤프·전원 손실 41·WHEA 부품·SMART·랜 링크 끊김 신호 추가, 싼 재현 점검(드라이버 롤백·팬·전력 제한·XMP·랜카드 절전)과 해결책 추가, 원인 신호로 해결 검증. 드라이버 시나리오 2건 + 하드웨어 시나리오 3건 추가 |
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
    data/samples/s5_thermal_fps_drop.json data/samples/s6_mvp_av_hook_crash.json \
    data/samples/s7_gpu_driver_bsod.json data/samples/s8_net_driver_drop.json \
    data/samples/s9_power_reboot.json data/samples/s10_memory_whea.json data/samples/s11_storage_freeze.json
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
data/samples/        시나리오 11종 + 사후 검증 스냅샷 3종 (아래 표)
docs/                설계 · 스냅샷 스키마 · 도구 API · 시연 절차 · 규칙 근거
tools/               gen_rules_default.py(규칙 내장본 생성), ce_tools.py(파이썬 래퍼)
tests/               의존성 없는 assert 테스트
```

| 시나리오 | 내용 | 범위 |
|---|---|---|
| s1_anticheat_crash (+ s1_anticheat_after_fix) | 다른 게임용 커널 안티치트가 무관한 게임을 튕기게 함. 사후 스냅샷으로 해결 검증 | 1차 보안 |
| s2_finsec_input_loss | 은행 사이트에서 깔린 키보드 보안모듈 필터로 게임 입력 누락 | 1차 보안 |
| s3_av_launch_fail | 게임 업데이트 직후 백신이 실행 파일을 오탐·격리 | 1차 보안 |
| s6_mvp_av_hook_crash | 수집기 MVP 신호만으로 백신 훅 DLL 크래시 진단 | 1차 보안 |
| s4_display_util_flicker | 모니터 유틸 설치 후 화면 깜빡임 | 2차 드라이버·시스템 |
| s7_gpu_driver_bsod (+ s7_gpu_driver_after_fix) | GPU 드라이버 자동 업데이트 후 블루스크린 0x116. 롤백 후 이틀 관찰로 해결 검증 | 2차 드라이버·시스템 |
| s8_net_driver_drop | 랜카드 드라이버 업데이트 후 네트워크 링크가 반복해서 끊김 | 2차 드라이버·시스템 |
| s5_thermal_fps_drop (+ s5_thermal_after_fix) | 과열 스로틀링으로 프레임 저하. 센서(스로틀링)로 해결 검증 | 3차 하드웨어 |
| s9_power_reboot | 게임 중 블루스크린 없이 꺼짐(전원 손실 41 3번) | 3차 하드웨어 |
| s10_memory_whea | 메모리 WHEA 오류 + 여러 프로그램이 다른 모듈에서 튕김 | 3차 하드웨어 |
| s11_storage_freeze | SMART 경고 + 디스크 재시도 이벤트 + 프리징 | 3차 하드웨어 |

시나리오 파일은 **실험용으로 만든 데이터**이며, 실제 PC에서 수집한 값으로 차례로 바꿔 나간다.

## 현재 상태

* 테스트 144건 통과(gcc·clang 경고 없음). Windows(Visual Studio 2022) 빌드는 1차 버전에서 확인했고,
  이번 변경분은 다시 확인이 필요하다.
* **동작 확인 결과** — 실험용 시나리오 11건(1차 4 / 2차 3 / 3차 4)에서 층 판정과 원인 1순위가 모두 정답.
  시나리오를 엔진 개발 쪽에서 만들었고 수가 적어 **정확도가 아니라 동작 확인**이다.
  정확도는 실제 사례 20~30건으로 따로 측정한다(제안서 4.2).

| 점검 순서 (11건 평균) | 평균 누적 시간 | 평균 기대 총비용 |
|---|---|---|
| 커뮤니티 표준 순서 | 139.3분 | 156.1분 |
| 엔진 v0 (확률 순) | 7.0분 | 41.8분 |
| 엔진 v1 (비용 가중) | 7.2분 | 35.2분 |

| 범위 | 건수 | 기대 총비용 (표준 / v0 / v1) |
|---|---|---|
| 1차 보안 프로그램 | 4 | 40.1분 / 23.7분 / 22.7분 |
| 2차 드라이버·시스템 | 3 | 240.9분 / 50.0분 / 34.8분 |
| 3차 하드웨어 | 4 | 208.5분 / 53.8분 / 48.0분 |

* **누적 시간**: "점검하면 원인이 반드시 드러난다(점검 신뢰도 1)"고 가정하고 정답에 닿을 때까지 더한 값.
* **기대 총비용**: 엔진의 후보 확률과 점검 신뢰도로 계산한 기대 점검 시간 + (원인을 못 찾을 확률 × 120분).
  120분은 '못 찾아서 수리점·전문가에게 맡기는 비용'으로 둔 팀 추정치(`session.unresolved_cost_minutes`)다.
  못 찾은 경우를 0으로 세면 점검을 일찍 멈추는 순서가 싸 보이므로 이 값으로 비교한다.
* 11건 모두 1순위가 정답이라 1순위부터 확인하는 v0 의 누적 시간이 조금 더 짧다. v1 은 1순위가 틀릴 가능성까지
  고려해 기대 총비용을 줄이는 방식이고, 11건 모두에서 v1 ≤ v0 이다(1차는 차이가 작고, 비싼 점검이 섞인 2·3차에서 차이가 큼).
* 점검 비용·신뢰도·해결률·120분은 모두 팀 추정치라 소수점 아래 차이는 의미가 없다. 큰 차이만 본다.
  제어 실험 측정 후 `data/rules.json` 을 갱신한다.

## 다음 작업

1. 수집기 MVP 실제 스냅샷으로 시나리오 s1 재현 (정태웅·남건호와 스키마 맞추기)
2. 에이전트 프레임워크 선정 결과에 맞춰 도구 스키마 조정 (정태웅 초안 확인 후)
3. 자동 점검 모드 허용 범위 초안과 `actions[].reversible / auto_default` 값 맞추기
4. 중간발표(10/16) 자료: 세션 시연 + 지표 표
5. 2차·3차 신호(커널 덤프 버그체크, WHEA 부품, SMART, 랜 링크 끊김)를 수집기에서 잡을 수 있는지 확인
   (`docs/snapshot_schema.md` v1.2 표 기준, 정태웅과 맞추기)
