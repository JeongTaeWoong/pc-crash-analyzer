# 시연 절차: 보안 프로그램 충돌 사례 1건 끝까지 (s1 안티치트)

멘토링 1차 3.3 「핀포인트 범위에서 '증상 → 원인 확인 → 해결 안내 → 실제 해결'이 끝까지 되는 사례 1건을
먼저 완성」에 해당하는 시연 절차다. 중간발표(10/16) 시연에 그대로 쓸 수 있다.

## 상황

* 사용자는 발로란트를 하려고 Riot Vanguard(커널 안티치트)를 설치했다(10/11).
* 사흘 뒤부터 **다른 게임**(`othergame.exe`)이 실행 중에 튕긴다. 오류 모듈은 `ntdll.dll` 이라 원인이 안 보인다.
* 크래시 덤프의 호출 스택에는 `vgk.sys` 가 남아 있다.
* 파일: `data/samples/s1_anticheat_crash.json` (증상), `data/samples/s1_anticheat_after_fix.json` (해결 후)

> 실험용으로 만든 시나리오다. 수집기 MVP가 나오면 같은 상황을 VM에서 재현한 실제 스냅샷으로 바꾼다.

## 1. 세션 시작 — 증상

```bash
./build/crash_engine session start data/samples/s1_anticheat_crash.json --session s1_session.json
```

```
상태: 원인 확인 중  (증상 app_crash / 대상 othergame.exe / 안내 모드)

[현재 원인 후보 확률]
 1. Riot Vanguard (커널 안티치트)  43%
 2. Discord 오버레이 (DiscordHook64.dll)  21%
 3. Steam 오버레이 (GameOverlayRenderer64.dll)  21%
 4. Microsoft Defender  9%
 5. NVIDIA 디스플레이 드라이버 (NVIDIA Display Driver)  6%

[다음 점검] 오버레이 종료 → Discord 오버레이 (DiscordHook64.dll)
  id: pause_overlay@overlay:discordhook64.dll
  원인이 드러날 확률 19%, 예상 20초
```

1순위는 Vanguard지만, 엔진은 **20초짜리 오버레이 끄기**부터 시킨다. Vanguard 점검은 재부팅이 필요해 5분이
걸리므로, 싼 점검으로 다른 후보를 먼저 걷어내는 쪽이 기대 비용이 작기 때문이다(v1 비용 가중 그룹 테스팅).

## 2. 원인 확인 — 점검 결과를 하나씩 알려 준다

```bash
./build/crash_engine session report --session s1_session.json --check pause_overlay@overlay:discordhook64.dll --result not_solved
./build/crash_engine session report --session s1_session.json --check pause_overlay@overlay:gameoverlayrenderer64.dll --result not_solved
./build/crash_engine session report --session s1_session.json --check av_pause@antivirus:microsoft_defender --result not_solved
```

결과가 '아님'일 때마다 베이즈 갱신으로 확률이 바뀐다. 세 번째 뒤:

```
[현재 원인 후보 확률]
 1. Riot Vanguard (커널 안티치트)  79%
 ...
[다음 점검] 안티치트 서비스 중지 후 재부팅 → Riot Vanguard (커널 안티치트)
  id: pause_anticheat@anticheat:vgk.sys
  원인이 드러날 확률 63%, 예상 5분
   1) 안티치트 서비스의 시작 유형을 '사용 안 함'으로 바꾸고 재부팅합니다.
   2) 안티치트를 쓰는 게임은 실행하지 않은 상태로 대상 프로그램을 실행해 봅니다.
   되돌리기: 서비스 시작 유형을 원래대로 되돌리고 재부팅합니다.
```

```bash
./build/crash_engine session report --session s1_session.json --check pause_anticheat@anticheat:vgk.sys --result solved
```

## 3. 해결 안내 — 원인 확정

```
상태: 원인 확인됨 · 해결 안내

[확인된 원인] Riot Vanguard (커널 안티치트)
  · 커널 드라이버로 상주 중입니다 (kernel_filter).
  · 설치 직후 베이스라인에는 없던 항목입니다(이후 추가됨).
  · 크래시 덤프의 호출 스택에 이 모듈이 있습니다 (vgk.sys).
  ※ 점검 때 바꾼 설정은 해결책을 적용하기 전에 되돌려 주세요: ...

[해결 안내]
 ▶ 1. 안티치트를 해당 게임을 할 때만 켜기  (id r_anticheat_autostart_off, 기대 해결률 70%)
        1) 안티치트 서비스 시작 유형을 '수동'으로 바꿉니다 ...
   2. 안티치트·게임 복구 및 최신 버전 업데이트  (기대 해결률 55%)
   3. 안티치트(와 해당 게임) 재설치  (기대 해결률 45%)
```

점검(일시 중지)은 '원인 확인'용이고, 해결책은 **원래 게임은 그대로 쓰면서** 충돌을 없애는 방법이다.

## 4. 실제 해결 — 적용하고 검증한다

```bash
./build/crash_engine session apply-fix --session s1_session.json --at 2026-10-15T20:55:00+09:00
# ... 사용자가 2시간 동안 othergame.exe 를 사용한 뒤, 수집기가 사후 스냅샷을 만든다
./build/crash_engine session verify --session s1_session.json --after data/samples/s1_anticheat_after_fix.json
```

```
상태: 해결 완료

[최근 검증] resolved (관찰 120분, 재발 0건)
  · 해결책 적용 이후 120분 동안 대상 프로그램을 사용했고 재발이 없었습니다.
  · 원인 대상(Riot Vanguard (커널 안티치트))이 더 이상 상주하지 않습니다.
  · 해결됨 피드백을 반영했습니다: Riot Vanguard (커널 안티치트) (app_crash/anticheat) 가중치 0.700 → 0.760
```

검증은 "크래시가 안 났다"만 보지 않는다.

1. 적용 시각 **이후**의 크래시 이벤트(1000/1001/1002) 수 — 적용 전 크래시는 세지 않는다
2. 그동안 대상 프로그램을 **실제로 실행했는지** — 안 썼으면 '관찰 중'
3. 관찰 시간이 기준(60분) 이상인지
4. 원인 대상이 아직 상주하는지 (참고 정보)

재발하면 다음 해결책(2번)으로 넘어가고, 모두 실패하면 '해결책 모두 실패' 상태가 된다.
사례 DB를 파일에 남기려면 `export-rules my_rules.json` 으로 사본을 만들고, 세션 시작 때 `--rules my_rules.json`,
검증 때 `--save-rules` 를 붙인다(원본 `data/rules.json` 은 사람이 근거를 남기고 고치는 파일이라 직접 덮어쓰지 않는다).

## 자동 점검 모드로 같은 흐름 보기

```bash
./build/crash_engine session start data/samples/s3_av_launch_fail.json --session s3_session.json --mode auto
```

* 백신 실시간 감시 중지 점검 → `confirm` (되돌릴 수 있지만 보안 공백이 생겨 기본은 확인)
* 원인 확정 후 해결책 "백신 예외 등록" → `auto` (되돌릴 수 있고 기본 허용) — 실행기가 원상복구 지점을 남기고 실행
* "실시간 백신 하나만 남기기" → `confirm`, "오탐 신고" → `manual`

## 에이전트(도구)로 같은 흐름

같은 단계를 `tool session_start / session_report / session_apply_fix / session_verify` 로 부르면 된다.
`docs/tool_api.md` 의 파이썬 예시 참고. 테스트 `testTool()` 이 이 흐름을 그대로 검사한다.
