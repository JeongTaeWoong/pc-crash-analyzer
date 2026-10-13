# 스냅샷 입력 스키마 v1.2

수집기(블랙박스)가 마킹 시점에 덤프하는 JSON 규격이다. 추론 엔진은 이 파일 하나만 읽는다.
제안서 5.2 「수집 신호」 표와 1:1로 대응하며, **없는 필드는 '확인 불가'로 처리**하고 신뢰도를 낮춘다.
즉 수집기는 구현된 항목부터 순서대로 채워 넣으면 되고, 전부 갖출 때까지 기다릴 필요가 없다.

> v1.1 변경(멘토링 1차 반영): `processes`, `modules[].version`, `drivers[].version`,
> `security.detections`, `observed_from`, `trigger.kind = "verify"`(사후 검증 스냅샷)를 추가했다.
> 모두 선택 항목이라 v1.0 스냅샷도 그대로 읽힌다.
>
> v1.2 변경(2차 드라이버·3차 하드웨어 범위): `minidumps[].kind`·`minidumps[].bugcheck`(블루스크린 커널 덤프),
> `events[].component`(WHEA 오류 부품), `storage.disks`(SMART)를 추가했다. 역시 모두 선택 항목이다.

## 수집기 MVP 최소 구성 (10/9 목표)

멘토링 1차에서 "수집기 MVP로 실제 PC에서 잡히는 신호를 먼저 목록화"하기로 했으므로,
엔진은 아래 네 가지만 있어도 동작하도록 만들었다(`data/samples/s6_mvp_av_hook_crash.json`).

| 신호 | 필드 | 없으면 |
|---|---|---|
| 앱 크래시 이벤트 1000/1001 | `events[]` | 오류 모듈 근거를 못 씀 |
| 키보드·마우스 UpperFilters | `device_filters` | 입력 누락 진단 정확도 하락 |
| 대상 프로세스에 로드된 DLL | `modules[]` | 오버레이·백신 훅 후보를 못 만듦 |
| 센서 가용 여부 | `sensors` (없으면 생략) | 하드웨어 판정 신뢰도를 낮춰 표시 |

`symptom.type`, `symptom.scope`, `symptom.target_process` 는 UI가 사용자에게 받아서 함께 넣는다.

## 최상위 구조

| 필드 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `schema_version` | string | ○ | `"1.2"` (1.0·1.1 도 그대로 읽는다) |
| `snapshot_id` | string | ○ | 스냅샷 식별자(파일명과 같게 두면 편함) |
| `marked_at` | ISO-8601 | ○ | 마킹 시각. **시간대 오프셋 포함**(예: `2026-10-16T14:32:10+09:00`) |
| `trigger` | object | ○ | `{"kind":"hotkey\|auto_app_crash\|auto_frametime\|auto_system","event_id":1000}` |
| `host` | object | | `{"os":"Windows 11 23H2","sensor_support":true}` |
| `observed_from` | ISO-8601 | | 사후 검증 스냅샷에서 '관찰을 시작한 시각' |
| `processes` | array | | 실행 중인 프로세스 이름 (문자열 또는 `{"name":...}`) |
| `symptom` | object | ○ | 아래 참조 |
| `modules` | array | | 로드된 모듈 |
| `drivers` | array | | 커널 드라이버·필터 |
| `device_filters` | object | | 키보드/마우스 UpperFilters, NDIS 필터 |
| `security` | object | | 실시간 백신, 방화벽 차단 기록, 탐지·격리 기록 |
| `change_history` | array | | 설치·업데이트·설정 변경 이력 |
| `events` | array | | Windows 이벤트 로그 |
| `sensors` | object | | `{"samples":[...]}` 온도·클럭·스로틀링 |
| `frametime` | object | | `{"samples":[{"time":...,"ms":8.3}]}` PresentMon |
| `minidumps` | array | | WER LocalDumps(앱) / 커널 덤프(블루스크린) 분석 결과 |
| `storage` | object | | `{"disks":[...]}` 저장장치 SMART 상태 (v1.2) |
| `baseline_diff` | object | | 베이스라인 스냅샷 대비 `added/removed/changed` |
| `ground_truth` | object | | **실험 시나리오 전용**. 실측 스냅샷에는 넣지 않는다 |

## symptom

```json
"symptom": {
  "type": "app_crash",          // app_crash|freeze|fps_drop|flicker|input_loss|launch_fail|network_fail|unexpected_reboot
  "target_process": "game.exe", // 없으면 빈 문자열
  "scope": "single_app",        // single_app|multiple_apps|system_wide|unknown  ← 층 판정의 1차 단서
  "started_at": "2026-10-14T20:30:00+09:00",  // 사용자가 기억하는 증상 시작 시점(시간 상관의 기준)
  "faulting_modules": ["ntdll.dll"]           // 여러 번의 크래시에서 모인 오류 모듈
}
```

`scope`와 `started_at`은 UI에서 사용자에게 한 번 물어보는 값이다. 두 값이 비면 시간 상관 근거를
쓸 수 없어 정확도가 크게 떨어지므로, UI 흐름에서 반드시 받는다.

## 배열 원소 형식

```json
"modules":  [{"process":"game.exe","name":"overlay64.dll","path":"C:\\...","company":"Discord Inc.",
              "signer":"Discord Inc.","version":"1.0.9","microsoft_signed":false,"loaded_at":"..."}],
"drivers":  [{"name":"vgk.sys","display":"Riot Vanguard","type":"kernel_filter|display|network",
              "company":"Riot Games","started_at":"..."}],
"device_filters": {"keyboard_upper":["kbfiltr.sys"],"mouse_upper":[],"ndis_filters":[]},
"security": {"realtime_av":["AhnLab V3"],
             "firewall_blocks":[{"time":"...","process":"game.exe","direction":"outbound","product":"V3"}],
             "detections":[{"time":"...","product":"AhnLab V3","target":"game.exe",
                            "action":"quarantine|block|kill|detect","threat":"탐지명"}]},
"change_history":[{"time":"...","kind":"install|update|driver_update|setting|os_update",
                   "subject":"Riot Vanguard","version":"1.16"}],
"events":  [{"time":"...","provider":"Application Error","id":1000,"subject":"game.exe",
             "faulting_module":"ntdll.dll"}],
"sensors": {"samples":[{"time":"...","cpu_temp":98,"gpu_temp":84,"cpu_clock":3400,"gpu_clock":1500,
                        "throttling":true,"gpu_power_limit_reason":"thermal"}]},
"minidumps":[{"time":"...","kind":"app","process":"game.exe","faulting_module":"ntdll.dll",
              "stack_third_party":["vgk.sys"]},
             {"time":"...","kind":"kernel","process":"System","faulting_module":"nvlddmkm.sys",
              "bugcheck":"0x116 VIDEO_TDR_FAILURE"}],
"storage": {"disks":[{"name":"Samsung SSD 860 EVO 500GB","health":"good|warning|bad",
                      "reallocated_sectors":12,"pending_sectors":24,"uncorrectable":3,"media_errors":0}]}
```

### 2차·3차 범위에서 쓰는 신호 (v1.2)

| 신호 | 넣는 곳 | 엔진이 보는 것 |
|---|---|---|
| 블루스크린 커널 덤프 | `minidumps[]` 에 `kind:"kernel"`, `bugcheck`, `faulting_module`(원인 드라이버) | 0x124·WHEA 계열이면 HW, 서드파티 `.sys` 가 원인이면 DRIVER |
| 블루스크린 기록 이벤트 | `events[]` provider `...WER-SystemErrorReporting` 또는 `BugCheck`, id 1001 | 덤프가 없어도 블루스크린이 있었음을 안다 |
| 예고 없는 종료 | `events[]` provider `Kernel-Power`, id 41, subject `"BugcheckCode 0"` | 코드가 0이고 앞뒤 10분 안에 블루스크린이 없을 때만 **전원 문제**로 센다 |
| WHEA 하드웨어 오류 | `events[]` provider `WHEA-Logger`, `component`: `memory\|cpu\|pcie\|cache` | 부품별로 메모리·연결(PCIe) 후보에 근거를 붙인다 |
| 디스플레이 드라이버 복구 | `events[]` id 4101 | GPU 드라이버 근거 + 해결 후 검증 신호 |
| 랜카드 링크 끊김 | `events[]` provider = 랜카드 드라이버 이름(예: `rt640x64`), subject `"Network link is disconnected."` | 2번 이상이면 DRIVER 근거, 랜카드 드라이버 후보에 근거 |
| 디스크 오류 | `events[]` provider `disk`/`Ntfs`, id 7·11·51·153 | 저장장치 후보 근거 + 해결 후 검증 신호 |
| SMART | `storage.disks[]` | `health` 가 warning/bad 이거나 재할당·대기·수정 불가 섹터가 있으면 경고 |

`component` 를 못 채우면 엔진이 subject 문자열(memory / pci / cache·processor)로 추정한다.

### 수집기 쪽에서 지켜야 할 약속

1. **시각은 전부 ISO-8601 + 오프셋 문자열**로 넣는다(epoch 정수도 허용하지만 표기가 불편하다).
2. `microsoft_signed`를 채워 주면 가장 정확하다. 없으면 엔진이 `signer`/`company`에 "Microsoft"가
   들어 있는지로 추정한다.
3. 센서를 못 읽는 기기는 `sensors`를 **아예 빼거나 빈 배열**로 둔다. 0이나 -1을 넣지 않는다
   (0℃가 실제 값으로 오해된다).
4. 수집에 실패한 항목은 빈 값으로 채우지 말고 필드 자체를 생략한다. 엔진이 '확인 불가'로 구분한다.
5. `ground_truth`는 실험용 시나리오 파일에만 넣는다. 실제 사용자 스냅샷에 들어가면 지표가 오염된다.

### 실험 시나리오용 정답 라벨

```json
"ground_truth": {"layer":"SW|DRIVER|HW","category":"anticheat","subject":"vgk.sys"}
```

`category`는 `data/rules.json`의 `priors` 키와 같은 값을 쓴다. `subject`는 표기가 달라도
(`vgk.sys` ↔ `Riot Vanguard`) 정규화 비교로 매칭된다.

## 사후 검증 스냅샷 (해결책 적용 후)

세션의 `session_verify` 단계에서 쓰는 스냅샷이다. 형식은 같고, 다음만 다르다.

```json
{
  "snapshot_id": "s1-anticheat-after-fix",
  "trigger": {"kind": "verify"},
  "marked_at": "2026-10-15T23:00:00+09:00",       // 관찰 끝
  "observed_from": "2026-10-15T21:00:00+09:00",   // 관찰 시작 (해결책 적용 이후)
  "symptom": {"type": "app_crash", "target_process": "othergame.exe"},
  "processes": ["othergame.exe", "..."],           // 대상 프로그램을 실제로 썼는지 확인
  "events": [ ... ]                                 // 적용 시각 이후의 1000/1001/1002 를 재발로 센다
}
```

엔진은 (1) 해결책 적용 시각 이후의 재발 신호 수, (2) 대상 프로그램 실행 여부, (3) 관찰 시간
(기본 60분 이상), (4) 원인 대상이 아직 상주하는지를 보고 `resolved / recurred / observing /
need_answer` 중 하나로 판정한다.

재발 신호는 **증상 신호**와 **원인 신호**를 함께 센다.

* 증상 신호: 앱 크래시 1000/1001/1002, 보안 탐지, 방화벽 차단, 4101(깜빡임), Kernel-Power 41·블루스크린(재부팅)
* 원인 신호(2차·3차): 과열 = 스로틀링·고온 샘플, 전원 = 전원 손실 41, 메모리 = 메모리 WHEA,
  저장장치 = 디스크 오류, 연결 = 장치 연결 해제·PCIe WHEA, GPU 등 드라이버 = 4101·블루스크린,
  랜카드 = 링크 끊김

그래서 하드웨어 원인의 사후 스냅샷에는 **센서 샘플이나 이벤트를 관찰 구간 전체에 걸쳐** 넣어 줘야 한다
(예: `s5_thermal_after_fix.json` 은 21:00~22:40 의 센서 샘플). 프레임 저하처럼 증상은 셀 수 없어도
원인 신호를 셀 수 있으면 검증할 수 있다. 둘 다 셀 수 없으면(입력 누락 등) `need_answer` 가 나오며,
이때는 사용자 응답(`recurred`, `observed_minutes`)으로 검증한다.
