# 도구 API (에이전트·UI·실행기용)

멘토링 1차 2.1/3.1: 추론 엔진은 팀이 직접 구현한 공학적 핵심으로 두고, LLM 에이전트(LangGraph 1순위,
Google ADK 비교)는 엔진을 **도구로 호출**해 결과를 해석·설명·조율한다. 이 문서는 그 경계를 정한다.

## 호출 방식

```bash
crash_engine tool <이름> [--input args.json]     # --input 이 없으면 표준입력에서 JSON 을 읽음
crash_engine tool list                            # 도구 목록 + 입력 스키마(JSON Schema)
```

응답은 항상 하나의 JSON 객체다.

```json
{"ok": true,  "tool": "session_report", "result": { ... }}
{"ok": false, "tool": "session_report", "error": "이미 결과를 받은 점검입니다: ..."}
```

* 종료 코드: 성공 0, 실패 1.
* 프로세스는 매번 새로 떠도 된다. 세션 상태는 `session_path` 파일에 저장된다.
* 입력 스키마 전체: `docs/tool_specs.json` (`crash_engine tool list` 로 다시 만들 수 있음).
* 파이썬에서는 `tools/ce_tools.py` 의 `CrashEngine.call(이름, **인자)` 를 쓰면 된다.

## 도구 7종

| 도구 | 하는 일 | 주요 인자 |
|---|---|---|
| `diagnose` | 1회 진단: 층·후보·근거·확인 순서·단계별 실행 방식 | `snapshot_path`, `mode`, `allowed_actions` |
| `explain` | 후보 하나의 근거 문장을 기여도 순으로 (LLM 설명용) | `snapshot_path`, `candidate_id` |
| `session_start` | 세션 시작, 첫 점검 안내 | `session_path`, `snapshot_path`, `mode` |
| `session_status` | 현재 상태 보기 | `session_path` |
| `session_report` | 점검 결과 알림 (`solved` / `not_solved` / `skipped`) | `session_path`, `check_id`, `result` |
| `session_apply_fix` | 해결책 적용 기록 | `session_path`, `remedy_id`(생략 시 현재 추천), `applied_at` |
| `session_verify` | 재발 검증 (사후 스냅샷 또는 사용자 응답) | `session_path`, `after_snapshot_path` 또는 `recurred`+`observed_minutes`, `save_rules` |

## 세션 응답(view)의 주요 필드

```jsonc
{
  "status": "checking | confirmed | fixing | resolved | exhausted | failed",
  "status_text": "원인 확인 중",
  "belief": [ {"id":"anticheat:vgk.sys","label":"Riot Vanguard (커널 안티치트)","probability":0.69,"in_focus":true,"stage":1} ],
  "next_check": {                       // checking 일 때만
    "check_id": "pause_anticheat@anticheat:vgk.sys",
    "label": "안티치트 서비스 중지 후 재부팅",
    "advice": "...",
    "chance_to_reveal_cause": 0.63,
    "action": { "execution": "guide|auto|confirm|manual", "automation": "service_start_type:vgk.sys:disabled",
                "steps": ["..."], "rollback": "...", "reversible": true, "requires_reboot": true, "reason": "..." }
  },
  "confirmed": { "id": "...", "label": "...", "evidence": [ ... ] },      // 원인 확정 후
  "restore_after_check": "점검 때 바꾼 설정은 ... 되돌려 주세요",
  "remedies": [ {"remedy_id":"r_anticheat_autostart_off","expected_success":0.7,"actions":[ ... ]} ],
  "current_remedy": "r_anticheat_autostart_off",
  "last_verify": { "outcome": "resolved", "recurrences": 0, "observed_minutes": 120, ... },
  "next_action": "다음에 무엇을 해야 하는지 한 줄",
  "history": [ ... ]
}
```

에이전트는 `next_action` 과 `status` 만 보고도 다음 도구를 고를 수 있다.

## 에이전트 그래프 예시 (수집 → 추론 → 확인 → 안내)

```
[수집기 스냅샷] → session_start
      │
      ▼
  status == checking ──▶ next_check 를 사용자에게 설명(LLM) ─▶ 실행기/사용자 수행 ─▶ session_report ─┐
      │                                                                                        │
      ◀────────────────────────────────────────────────────────────────────────────────────────┘
  status == confirmed ─▶ explain 으로 근거 설명 + current_remedy 안내 ─▶ 적용 ─▶ session_apply_fix
      │
  status == fixing ───▶ (사용 후) 수집기 사후 스냅샷 ─▶ session_verify
      │
  resolved / failed / exhausted ─▶ 종료 메시지
```

LLM은 확률·순서·실행 방식을 **바꾸지 않고** 설명만 한다. 판단은 엔진이 한다.

## 실행기 약속 (자동 점검 모드)

`action.execution` 값에 따라 실행기(수집기 또는 에이전트 쪽)는 다음과 같이 동작한다.

| execution | 실행기 동작 |
|---|---|
| `guide` | 실행하지 않는다. `steps` 를 사용자에게 보여 준다 (안내 모드) |
| `auto` | 실행 전에 원상복구 지점을 기록하고 `automation` 명령을 실행, 결과를 로그로 남긴다 |
| `confirm` | 사용자에게 `reason` 과 `steps` 를 보여 주고 '예'를 받은 뒤에만 실행 |
| `manual` | 사람이 직접 해야 하는 조치. `steps` 만 안내 |

`automation` 명령 형식은 `동작:대상[:값]` 이다.

| 명령 예 | 뜻 |
|---|---|
| `toggle_overlay:Discord 오버레이:off` | 해당 제품의 게임 내 오버레이 끄기 |
| `stop_process:LogiKbdFilt` | 프로세스 종료 |
| `av_realtime:AhnLab V3:pause:600` | 실시간 감시 600초 일시 중지 |
| `av_exclude:AhnLab V3:IndieGame.exe` | 대상 프로그램 폴더를 백신 예외에 등록 |
| `firewall_allow:game.exe` | 방화벽 허용 규칙 추가 |
| `service_start_type:vgk.sys:manual` | 서비스 시작 유형 변경 |

허용 범위(어떤 조치를 `auto` 로 둘지)는 `data/rules.json` 의 `actions[].reversible`, `auto_default` 와
세션 시작 때의 `allowed_actions` 로 정한다. 멘토링 실행 항목 「자동 점검 모드 허용 범위 초안」이 정해지면
이 값을 맞춘다.

## 예시: 파이썬에서 세션 한 바퀴

```python
from ce_tools import CrashEngine
eng = CrashEngine("engine/build/Release/crash_engine.exe")
s = "s1_session.json"
v = eng.call("session_start", session_path=s, snapshot_path="engine/data/samples/s1_anticheat_crash.json")
while v["status"] == "checking":
    chk = v["next_check"]
    result = ask_user(chk)              # 'solved' / 'not_solved' / 'skipped'
    v = eng.call("session_report", session_path=s, check_id=chk["check_id"], result=result)
if v["status"] == "confirmed":
    v = eng.call("session_apply_fix", session_path=s)
    # ... 사용자가 충분히 사용한 뒤 수집기가 사후 스냅샷을 만든다
    r = eng.call("session_verify", session_path=s, after_snapshot_path="after.json")
    print(r["verify"]["outcome"])
```
