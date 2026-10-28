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

## D. 자원 추세 (수집기 v0.6에서 구현 — `resource_trends`, 2026-10-29)

수집기가 24시간 분 단위 롤업(메모리에만)을 유지하고, 덤프 시 **최근 60분 기울기 요약**을 엔진 스냅샷에 넣는다.
전체 시계열은 `snapshot.json`의 `rollup24h`에만 있고 엔진에는 요약만 간다. 원천: PerfOS Memory·GPU Adapter Memory(VidMm)·
Process 카테고리(CPU%)·LibreHardwareMonitor GPU 코어·ProcessListSource(Private Bytes/핸들). 스파이크 I(10/29, ko-KR)로 카운터 검증.

```json
"resource_trends": {
  "window_minutes": 60, "samples": 58,
  "commit_slope_pct_per_hour": 2.4, "commit_pct_now": 74.3, "avail_mb_slope_per_min": -35.2,
  "gpu_dedicated_mb_slope_per_min": 0.8, "gpu_temp_max": 78.0, "gpu_clock_min": 1395.0, "gpu_clock_max": 2505.0,
  "focus_process": {"pid": 4242, "name": "Unity", "samples": 58, "privMBNow": 4697.2, "privMBStart": 3990.0,
                    "privSlopeMBPerMin": 12.1, "privR2": 0.97, "handleSlopePerMin": 4.9, "cpuAvg": 8.3},
  "focus_process_last15": {"privSlopeMBPerMin": 14.0, "r2": 0.95, "samples": 15},
  "top_memory_growers": [{"pid": 4242, "name": "Unity", "privSlopeMBPerMin": 12.1, "privR2": 0.97, "...": "..."}],
  "top_cpu_60": [{"pid": 1234, "name": "SignKoreaWD", "samples": 58, "cpuAvg": 13.9}]
}
```

- `focus_process` = 크래시 프로세스(자동 트리거) 또는 마킹 시점 전경 프로세스(수동). 없으면 null.
- 기울기는 최소제곱(`Trend.Fit`), `r2`가 낮으면(<0.5) 단조 증가가 아니라 들쭉날쭉 — 엔진이 "누수형"으로 단정하지 않도록 함께 제공.
- 엔진 규칙(`docs/slowdown-mechanisms.md` 2절의 분기표; 10/29 엔진 구현):
  - A 앱 메모리 증가: `focus_process.privSlopeMBPerMin ≥ 5 && privR2 ≥ 0.7` 또는 `commit_pct_now ≥ 85 && commit_slope_pct_per_hour > 0` → `other_sw:<focus>` "메모리 증가" 근거
  - C 열 스로틀: `gpu_temp_max ≥ 83 && gpu_clock_min < 0.75 × gpu_clock_max` → `thermal` 근거 (CPU는 Event 37 이벤트로)
  - E 외부 간섭: `top_cpu_60[*]`이 초점 프로세스가 아니고 `cpuAvg ≥ 10` → 해당 프로세스를 hints로 분류(antivirus/fin_security/other_sw)해 "상주 과부하" 근거
  - B VRAM 압박: 어댑터 총량이 아직 없어 보류(host에 `gpu_dedicated_total_mb` 추가 예정)
- 이전 초안의 `resource_top/memory/disk/startup/av_resource`는 `rollup24h`·`resources` 이벤트로 흡수. `disk.health`(SMART)는 WMI 미지원으로 보류, `startup`은 11/13.
- `baseline_diff`(v1.1 기존 필드)는 롤업의 "24시간 전 같은 시각 vs 지금" 비교로 채우는 것을 다음 단계로 둔다.

## E. 도구 API 확장 (에이전트 멀티 턴용, 수집기 로컬 도구 — 별도 문서 예정)

| 도구 | 입력 | 출력 |
|---|---|---|
| `dump_search_events` | dump_id, from, to, source?, pid? | 구간 내 이벤트 목록(요약) |
| `dump_process_modules` | dump_id, pid 또는 name | 마지막 모듈 목록 + 매칭 근거 |
| `dump_sensor_window` | dump_id, from, to | 센서 샘플 요약(최대·최소·추세) |

서버 에이전트가 호출 → 소켓으로 클라이언트(수집기/UI)가 응답. 원본 로그는 PC 밖으로 나가지 않고 요약만 나간다.
