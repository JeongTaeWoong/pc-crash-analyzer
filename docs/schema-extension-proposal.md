# 스냅샷 스키마 확장 제안 (v1.1 → v1.2) — 수집기 쪽 초안 (2026-10-28)

대상: `engine/docs/snapshot_schema.md` (엔진 담당 검토 요청). 원칙은 v1.1 그대로 — 모두 **선택 항목**, 없는 필드는 "확인 불가".
엔진이 미지 필드를 무시함을 확인했으므로(10/28, `significance`·`resource_top`을 넣어도 진단 정상) 수집기는 아래를
**지금부터 내보내고**, 엔진은 준비되는 대로 읽는다.

## A. 유의성 (수집기가 이미 내보냄)

```json
"significance": {"score": 72, "tier": "major", "tags": ["user-facing", "third-party-module", "recurred-3x-7d"]}
```

- 의미: "이 덤프가 사용자 체감 사건일 가능성"의 **정렬·숨김 힌트**. 판단이 아니다.
- 산정 요소: 트리거 종류 기본점(커널급 90 / 응답 없음 60 / 앱 크래시 30), 사용자 대면 +30, 오류 모듈 서드파티 +20,
  7일 내 재발 +15, 수명 >30초 +15 / <5초 −20, 네이티브 예외 코드 +10, 테스트 아티팩트 → ≤10.
- 엔진 활용 제안: `tier`를 `hidden`이면 사례 DB 집계에서 제외, `recurred-*` 태그는 시간 상관 근거 가중.
- UI: 기본 필터 "주요(major)"만 표시 (2026-10-28 결정: 낮은 등급도 덤프는 남기고 숨김).

## B. 새 트리거 종류 (수집기가 이미 내보냄)

`trigger.kind`는 v1.1 열거형 그대로(`auto_app_crash`/`auto_system`)이고, 세부는 `event_id`로 구분한다.

| 수집기 kind | trigger.kind | event_id | symptom.type | 비고 |
|---|---|---|---|---|
| hang-1002 | auto_app_crash | 1002 | freeze | **신규** — 응답 없음 |
| hang-1001 | auto_app_crash | 1001 | freeze | WER AppHang 보고 (1002와 중복 제거됨) |
| kernel-live | auto_system | 1001 | freeze | **신규** — LiveKernelEvent (GPU TDR 등) |
| bsod-1001 | auto_system | 1001 | unexpected_reboot | **신규** — BugCheck 기록 (미확인: 이 PC에 사례 없음) |

`events[]`에 `wer_event_type`(APPCRASH/RADAR_PRE_LEAK_64/…)와 `exception_code`를 추가하면 엔진이 비크래시 보고를
구분할 수 있다 — 수집기 버퍼에는 이미 `werEventType`, `exceptionCode`로 들어간다.

## C. 느려짐 증상 (엔진 추가 필요 — 현재 `unknown`으로 읽힘)

```json
"symptom": {"type": "slow", "scope": "system_wide", "target_process": "", "started_at": "..."}
```

트리거는 수동 마킹(`hotkey`). 느려짐은 시작 시점이 불분명하므로 `started_at`은 마킹 시각이며 엔진은 시간 상관 대신
**베이스라인 대비 변화**를 본다.

## D. 자원 신호 (수집기 11/13 목표)

```json
"resource_top": [{"name": "SignKoreaWD", "pid": 1234, "cpu_pct": 22, "ws_mb": 33, "io_bps": 0}],
"memory": {"available_mb": 48483, "commit_pct": 72, "pages_per_sec": 0},
"disk":   {"busy_pct": 0, "queue": 0, "bytes_per_sec": 2780876, "health": [{"name": "SanDisk Ultra 3D", "status": "Healthy"}]},
"startup": {"run_keys": 17, "logon_tasks": 32, "items": ["..."]},
"av_resource": {"process": "MsMpEng", "cpu_pct": 0, "ws_mb": 591},
"baseline_diff": {"changed": [{"name": "SignKoreaWD", "cpu_pct_before": 2, "cpu_pct_after": 22}]}
```

- `baseline_diff`는 v1.1에 이미 있는 필드 — 수집기가 24시간 분 단위 롤업(메모리)과 마킹 창을 비교해 채운다.
- 출처 스파이크(10/28): PerfProc/PerfOS/PerfDisk/Get-PhysicalDisk/Run 키·예약 작업 **검증됨**, SMART WMI 미지원.
- 규칙 후보(엔진 rules.json): `resource_top[0].cpu_pct − baseline ≥ 15`이면 "상주 프로세스 과부하" 후보, 시작 항목 ≥15면 "시작 항목 과다".

## E. 도구 API 확장 (에이전트 멀티 턴용, 수집기 로컬 도구 — 별도 문서 예정)

| 도구 | 입력 | 출력 |
|---|---|---|
| `dump_search_events` | dump_id, from, to, source?, pid? | 구간 내 이벤트 목록(요약) |
| `dump_process_modules` | dump_id, pid 또는 name | 마지막 모듈 목록 + 매칭 근거 |
| `dump_sensor_window` | dump_id, from, to | 센서 샘플 요약(최대·최소·추세) |

서버 에이전트가 호출 → 소켓으로 클라이언트(수집기/UI)가 응답. 원본 로그는 PC 밖으로 나가지 않고 요약만 나간다.
