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

[다음 점검] 오버레이 종료 → Steam 오버레이 (GameOverlayRenderer64.dll)
  id: pause_overlay@overlay:gameoverlayrenderer64.dll
  원인이 드러날 확률 19%, 예상 20초
```

1순위는 Vanguard지만, 엔진은 **20초짜리 오버레이 끄기**부터 시킨다. Vanguard 점검은 재부팅이 필요해 5분이
걸리므로, 싼 점검으로 다른 후보를 먼저 걷어내는 쪽이 기대 비용이 작기 때문이다(v1 비용 가중 그룹 테스팅).

## 2. 원인 확인 — 점검 결과를 하나씩 알려 준다

```bash
./build/crash_engine session report --session s1_session.json --check pause_overlay@overlay:gameoverlayrenderer64.dll --result not_solved
./build/crash_engine session report --session s1_session.json --check pause_overlay@overlay:discordhook64.dll --result not_solved
```

결과가 '아님'일 때마다 베이즈 갱신으로 확률이 바뀐다. 두 번째 뒤:

```
[현재 원인 후보 확률]
 1. Riot Vanguard (커널 안티치트)  69%
 2. Microsoft Defender  15%
 ...
[다음 점검] 안티치트 서비스 중지 후 재부팅 → Riot Vanguard (커널 안티치트)
  id: pause_anticheat@anticheat:vgk.sys
  원인이 드러날 확률 55%, 예상 5분
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

## 2차 범위 예시: GPU 드라이버 블루스크린 (s7)

같은 흐름이 드라이버·하드웨어에서도 그대로 동작한다. 파일: `s7_gpu_driver_bsod.json` (증상),
`s7_gpu_driver_after_fix.json` (해결 후 이틀 관찰).

* 11/5 아침 NVIDIA 드라이버가 565.90 → 566.14 로 자동 업데이트됐다.
* 그날 저녁 게임 중 화면이 멈췄다가(4101) 블루스크린 0x116 VIDEO_TDR_FAILURE 로 두 번 재부팅됐다.
* Kernel-Power 41 도 남았지만 BugcheckCode 가 278(=0x116)이라 **전원 문제로 세지 않는다**.

```bash
./build/crash_engine session start data/samples/s7_gpu_driver_bsod.json --session s7_session.json
```

```
[현재 원인 후보 확률]
 1. NVIDIA 디스플레이 드라이버 (NVIDIA Display Driver)  66%
 2. Realtek 네트워크 드라이버 (Realtek PCIe GbE Family Controller)  17%
 3. 전원·전력 공급 문제  11%
 ...
[다음 점검] GPU 드라이버 롤백(장치 관리자) → NVIDIA 디스플레이 드라이버 (NVIDIA Display Driver)
  id: rollback_gpu_driver_quick@gpu_driver:nvlddmkm.sys
  원인이 드러날 확률 49%, 예상 7분
```

DDU 재설치(30분)보다 장치 관리자 롤백(7분)을 먼저 시킨다. 증상이 블루스크린이라 '랜카드 절전 끄기'
같은 네트워크 점검은 순서에 넣지 않는다(점검의 `symptoms` 제한).

```bash
./build/crash_engine session report --session s7_session.json --check rollback_gpu_driver_quick@gpu_driver:nvlddmkm.sys --result solved
```

```
[확인된 원인] NVIDIA 디스플레이 드라이버 (NVIDIA Display Driver) (2차 범위)
  · 증상 시작 직전에 변경되었습니다 (2026-11-05T08:50:00+09:00, 565.90 → 566.14).
  · 블루스크린(0x116 VIDEO_TDR_FAILURE)의 원인 드라이버로 기록되었습니다.
  · 디스플레이 드라이버 응답 중지·복구(4101)가 3번 있습니다.

[해결 안내]
 ▶ 1. GPU 드라이버 이전 버전 유지  (id r_gpu_rollback, 기대 해결률 60%)
      - 장치 드라이버 이전 버전으로 되돌리기   ← 점검에서 이미 한 조치라 그대로 두면 된다
      - 이 드라이버의 자동 업데이트 일시 보류  ← Windows 업데이트가 566.14 를 다시 깔지 않게
   2. GPU 드라이버 완전 삭제 후 클린 설치  (기대 해결률 50%)
```

```bash
./build/crash_engine session apply-fix --session s7_session.json --at 2026-11-06T10:00:00+09:00
./build/crash_engine session verify --session s7_session.json --after data/samples/s7_gpu_driver_after_fix.json
```

```
[최근 검증] resolved (관찰 2870분, 재발 0건)
  · 해결책 적용 이후 2870분 동안 PC를 사용했고 재발이 없었습니다.
  · 원인 드라이버(NVIDIA 디스플레이 드라이버 (NVIDIA Display Driver))는 버전 565.90(으)로 설치되어 있습니다.
    드라이버는 지우지 않고 버전을 바꾸는 것이라 정상입니다.
```

검증은 증상 신호(41·블루스크린)와 **원인 신호**(4101·커널 덤프)를 함께 센다. 적용 전(11/5)의 기록은 세지 않는다.

3차 범위(하드웨어)는 s5 과열로 같은 흐름을 볼 수 있다. '팬 속도 높여 재현'(5분)으로 확인 →
'팬 속도 높게 유지' 해결책 → 사후 스냅샷 `s5_thermal_after_fix.json` 의 센서 샘플에 스로틀링이 없으면 해결.
프레임 저하는 로그로 셀 수 없지만 원인 신호(스로틀링)로 검증한다.

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
