# 프로그램 간 충돌 검증 계획 · 실데이터 수집 · 전문성 확보 (2026-10-28)

정태웅 10/28 요구 두 가지에 대한 계획이다.

> 2. 포토샵·영상편집·코드에디터·게임·보안프로그램·업무 프로그램 등 다양한 프로그램 간 충돌 및 검증이 필요
> 3. 실제 테스팅·데이터 수집·전문성 확보에 대한 고려 필요

원칙은 `CLAUDE.md` 그대로다. 수집기는 **충돌을 판정하지 않는다.** 크래시한 프로세스 안에 어떤 비시스템 모듈이 함께 있었는지,
그 시각에 어떤 상주 프로그램·드라이버가 있었는지를 **공존 증거**로 기록하고, 추론은 엔진이 한다. 그래서 "검증"의 뜻도
"충돌을 맞췄다"가 아니라 **"재현 가능한 충돌 시나리오에서 수집기가 필요한 증거를 빠짐없이 남기고, 엔진이 그 증거로 올바른 가설을
세우는가"**다.

---

## 0. 왜 충돌이 생기는가 — 기술적 메커니즘 4가지 (여기에 매트릭스를 건다)

프로그램 이름은 수백 개지만 충돌 메커니즘은 몇 가지로 수렴한다. 매트릭스는 **프로그램 × 프로그램**이 아니라
**프로그램 종류 × 개입 메커니즘**으로 짠다. 그래야 설치돼 있지 않은 프로그램도 같은 메커니즘의 다른 프로그램으로 대신 검증할 수 있다.

| 메커니즘 | 무엇이 어디에 들어가나 | 대표 프로그램 (출처) | 수집기가 남길 증거 |
|---|---|---|---|
| **M1 그래픽 후킹(오버레이·캡처)** | DX/OpenGL 함수를 가로채는 DLL이 게임·편집 프로세스 안에 로드됨 | OBS graphics-hook, Discord·TeamSpeak 오버레이 — OBS 공식 KB가 "같은 그래픽 함수를 서로 후킹해 크래시·이상 동작" 명시 [1]; Steam 오버레이·RivaTuner(RTSS)는 커뮤니티 보고 [6] | 크래시 프로세스의 ETW ImageLoad 모듈 목록(비시스템 경로) — **이미 구현·검증됨(9/9)** |
| **M2 커널 드라이버 간 충돌(안티치트·RGB·모니터링)** | 안티치트가 서명·무결성 요건 미달 드라이버를 거부하거나, 드라이버끼리 충돌 | Riot Vanguard: 공식 문서는 "요건 미달 드라이버 발견 시 파일명 표시" [2]; ene.sys(RGB)·rtcore64.sys(Afterburner)는 커뮤니티 보고 [3]. EAC: EA 공식은 "백신 허용목록 협업" 언급 [4], RGB 소프트웨어 사례는 보도 [5] | `drivers.json`(Win32_SystemDriver) + 이벤트 로그 메시지의 파일명 — **Vanguard는 미설치라 미확인** |
| **M3 파일시스템 미니필터·실시간 검사(백신·금융보안)** | 모든 파일 열기/쓰기를 가로채 지연 또는 차단 | JetBrains 공식: 실시간 검사가 빌드·인덱싱 속도 저하 → 제외 권고 [7]; Rider 공식: msbuild 프로세스 제외 권고 [8]; Microsoft Dev Drive "성능 모드" [9] | `fltmc` 필터 목록(관리자) + `PerfProc` 프로세스 CPU/IO — **느려짐(slow) 신호, 11/13 구현 예정** |
| **M4 GPU 드라이버 버전·TDR** | 특정 드라이버 범위에서 앱 크래시 또는 디스플레이 드라이버 재시작 | Adobe 공식: Premiere 15.2/AE 18.2가 NVIDIA 441.08–451.67 범위에서 크래시 [10]; Blender 공식 매뉴얼: 드라이버 갱신·클린 설치가 1차 조치 [11] | 4101(디스플레이 드라이버 응답 중지) 트리거 + `drivers.json` 버전 + GPU 센서(온도·클럭) — **4101 트리거 구현됨, 실수신 미확인** |

이 PC에서 10/28 측정된 **M3의 실사례**가 이미 하나 있다: 금융보안 상주물(SignKoreaWD·Goji·SKBspmtmon)이 유휴 시 CPU 13–22%
(`post-midterm-plan.md` 2절). 이것이 12/11 "느려짐 진단 시연" 후보다.

---

## 1. 검증 매트릭스 — 프로그램 종류 × 메커니즘

○ = 이 PC에서 지금 재현 가능 · △ = 설치/준비 필요 · ✕ = 팀 PC로는 불가(외부 PC 필요) · 괄호는 재현 수단.

| 프로그램 종류 | 이 PC에 설치됨 (10/28 인벤토리) | M1 후킹 | M2 드라이버 | M3 필터·검사 | M4 GPU |
|---|---|---|---|---|---|
| **영상·그래픽 편집** (Premiere·Photoshop·DaVinci·Blender) | Adobe Bridge·CC 데스크톱만. **Photoshop·Premiere 미설치** | △ (OBS 캡처 + Discord 오버레이 켜고 편집) | – | ○ (Defender 실시간 검사 켜고/끄고 내보내기 시간) | △ (Blender는 무료라 설치 가능; 드라이버 롤백은 **하지 않음** — 읽기 전용 원칙) |
| **코드 에디터·IDE** (VS Code·Unity·Android Studio·VS) | ○ VS Code, Unity 2021/6000 ×3, Android Studio, VS 2022 | – | – | ○ (`dotnet build` 이 저장소: Defender 제외 전/후 빌드 시간 + MsMpEng CPU) | △ (Unity 셰이더 컴파일 중 4101 — **미확인**) |
| **게임** (Steam·Epic·Battle.net·Lost Ark·STOVE) | ○ 런처 5종, Lost Ark(+TEST). 드라이버 **xhunter1/2**(Wellbia, 중지 상태), **ACE-BASE**(Tencent ACE, **실행 중**) 관찰 | ○ (게임 + OBS 게임 캡처 + Discord 오버레이 + NVIDIA 앱 오버레이) | △ (EAC/BattlEye 게임은 Epic 무료 타이틀로 설치 가능; **Vanguard는 ✕** — LoL/Valorant 미설치, 설치 시 상시 커널 드라이버라 팀원 동의 필요) | ○ (게임 중 금융보안 상주물 CPU 점유) | ○ (게임 중 GPU 온도→클럭 하락 시나리오, 센서 2초 — 구현됨) |
| **보안·백신** | Defender(기본) + **금융·인증 11종**(AhnLab Safe Transaction·nProtect Online Security·TouchEn nxKey·Veraport·Delfino·IPinside·AnySign·SignKorea·CrossCert·WIZVERA) | △ (nxKey는 키보드 후킹 — 게임 입력 충돌은 **공식 사례 없음, 미확인** [12]) | ○ (드라이버 목록 수집) | ○ (실측 13–22% 있음) | – |
| **업무·사무** (Office·브라우저·메신저·화상회의) | 브라우저·Discord | △ (Discord 화면 공유 + 다른 캡처) | – | ○ | – |
| **오버레이·캡처·튠** (OBS·Discord·NVIDIA App·Afterburner) | ○ OBS, Discord, NVIDIA App. **Afterburner/RTSS·RGB 소프트웨어 미설치** | ○ 위 M1 재현의 "가해자" 역할 | △ (RGB·Afterburner는 설치하면 재현 가능하나 EAC 밴 위험 보도 [5] — **계정 걸린 게임에서는 하지 않음**) | – | – |

읽는 법: ○가 많은 행(게임·IDE·보안)이 **1차 검증 대상**이고, 편집 프로그램은 Blender(무료)로 M1·M4를, 보안 프로그램은
이미 설치된 금융보안 11종으로 M3를 대신 검증한다. Photoshop·Premiere는 학교 라이선스 또는 7일 체험판 가능 여부를 팀에서 확인(결정 필요).

### 각 셀의 "검증 통과" 기준 (공통)

1. **트리거**가 발생했는가(1000/1002/4101…), 또는 크래시 없는 시나리오면 수동 마킹(`m`)으로 덤프가 남았는가.
2. **증거**: 크래시 프로세스 모듈 목록에 개입 모듈(예 `graphics-hook64.dll`, `DiscordHook64.dll`)이 들어 있는가. 느려짐이면 `PerfProc` 상위 프로세스에 개입 프로세스가 있는가.
3. **엔진 가설**이 그 모듈/프로세스를 지목하는가(`crash_engine` 출력의 evidence에 모듈명 포함).
4. **유의성**이 hidden이 아닌가(사용자 대면 + 서드파티 모듈이면 설계상 major).
5. 1–4 결과를 `collector/NOTES.md`에 **검증됨/미지원/권한 필요/미확인**으로 기록. 못 재현한 것도 기록.

---

## 2. 이 PC에서 바로 할 수 있는 1차 검증 4건 (10/29~11/5)

추가 설치 없이 되는 것만 골랐다. **설정 변경 금지는 수집기 코드에 대한 원칙**이다. 실험자가 자기 PC에서 Defender 제외를 손으로 켜고 끄는 A/B는 통제 실험으로 허용하되, 실험 후 반드시 되돌리고 NOTES에 기록한다(10/28 수정 — 이전 문구는 과도했음).

| # | 시나리오 | 절차 | 기대 증거 | 아직 모르는 것 |
|---|---|---|---|---|
| V1 | **M1: 게임 + OBS 게임캡처 + Discord 오버레이** | 수집기(관리자) → Lost Ark 또는 Steam 게임 → OBS 게임 캡처 ON → Discord 오버레이 ON → 20분 플레이. 크래시 없으면 `m` 수동 마킹 | 게임 프로세스 모듈에 `graphics-hook64.dll` + `DiscordHook64.dll` 공존 기록(크래시 안 나도 **공존 데이터는 남음**) | 실제 크래시 발생률 — 공식 KB는 "할 수 있다"지 확률은 아님 |
| V2 | **M3: Defender 실시간 검사 vs 빌드 (A/B)** | 수집기 → `dotnet build collector/BlackBox -c Release` 클린 빌드 3회 → 저장소 폴더를 Defender 제외에 **손으로** 추가 → 3회 → 제외 **제거**. `MsMpEng.exe` CPU·IO를 `PerfProc`로 기록 | 빌드 시간 차이, `resource_top`에 MsMpEng 상위 등장. Rider(비상업 무료)로 같은 실험 반복 → IDE 자체 백신 알림과 교차 검증 | 차이가 측정 오차 안이면 이 PC(RAM 94GB·NVMe)에선 재현 안 됨으로 기록 |
| V3 | **M3: 금융보안 상주물 24시간** | 수집기 24시간 유휴 가동 → 분 단위 롤업(11/13 구현 후) | SignKoreaWD·Goji·SKBspmtmon CPU 시계열, 어떤 시간대에 튀는지 | 롤업 미구현 — 그 전엔 10초 스냅샷 수동 집계 |
| V4 | **M4: 게임 중 GPU 열·클럭** | 수집기 → 게임 30분 → GPU 온도·클럭·전력·스로틀 플래그 | "온도↑ → 클럭↓" 구간 존재 여부 | 프레임 저하와의 연결은 PresentMon 미연동 |

실행 순서(수율순, 10/28 질의 결과): **Rider(무료) A/B → OBS + Afterburner/RivaTuner(비안티치트 Steam 게임: Balatro·Skul·Palworld) → 일부러 크래시하는 OBS/Unity 네이티브 플러그인(실제 호스트 + 서드파티 모듈 패턴을 매번 재현, 라벨은 official 아닌 `injected`) → Adobe 체험판 7일(11월 중순 이후, OBS 공식 목록의 "Adobe Dynamic Link" 충돌과 묶어서)**.
OBS 공식 알려진 충돌 목록 [1]에 있는 것 중 이 PC에 이미 있는 가해자: Discord 오버레이(`DiscordHook64.dll` 확인), Adobe Dynamic Link(CC 데스크톱). 같은 메커니즘: Lossless Scaling(Steam, 설치됨).

V1은 **"크래시가 안 나도 가치가 있다"**가 핵심이다. 수집기가 하는 일은 공존 기록이고, 실제 충돌은 외부 PC 데이터에서 온다.

---

## 3. 실데이터 수집 프로토콜 (요구 3번 — 테스팅·데이터)

### 3.1 데이터는 3층으로 나눠 모은다

| 층 | 무엇 | 양(목표) | 정답(ground truth) 출처 | 용도 |
|---|---|---|---|---|
| **L1 합성** | FaultLab(crash-a/b/c·plugin·hang·cpu·disk·leak) + CrashDummy | 시나리오 9종 × 3회 = 27건, 이미 생성 가능 | 우리가 만든 것이니 100% | 회귀 테스트(검출률·모듈 포착률·지연) — **현재 9/9** |
| **L2 재현 실충돌** | 1절 매트릭스의 ○·△ 셀 | 10/29~11/27, 셀당 3회 | 공식 KB가 "알려진 충돌"로 명시한 것만 정답으로 인정 | 엔진 가설 정확도 |
| **L3 관찰(상시 가동)** | 팀원 3 PC + 지인 PC 상시 가동 | 3 PC × 4주 + 지인 ≥5 PC × 2주 | 사용자 설문(아래) | 유의성 분포·거짓 트리거율·실세계 다양성 |

L3 양 추정 근거: 이 PC 30일 로그 127건 중 재분류 후 트리거 대상은 소수(`post-midterm-plan.md` 0절). PC당 월 5–20건 major/minor로 보면
3 PC × 4주 ≈ 15–60건 + 지인 PC. **그 이상은 외부 PC 없이는 안 나온다** — 그래서 지인 PC 모집이 11월 핵심이다.

### 3.2 케이스 하나의 포맷 (케이스 내보내기 — **10/29 구현** `tools/export-case.ps1 -Dump <덤프 폴더> [-Symptom slow]`; `cases/` 는 gitignore)

```
case-<id>/
  engine-snapshot.json      # 수집기 산출 (이미 %USERPROFILE% 치환)
  ground_truth.json         # {"symptom":"crash|freeze|slow|bsod", "cause_category":"M1..M4|unknown",
                            #  "culprit":"<모듈/프로그램 또는 null>", "confidence":"official|user|guess",
                            #  "what_user_did":"...", "what_fixed_it":"...", "source":"<KB URL 또는 설문>"}
  engine-output.json        # crash_engine 결과 (가설·근거)
```

`confidence` 3단계가 중요하다. **official**(벤더 KB에 있는 충돌)만 정량 평가에 쓰고, user·guess는 탐색용으로 분리한다.
이렇게 하면 "우리가 정답을 정해놓고 맞췄다"는 지적을 피할 수 있다.

### 3.3 지인 PC 참여 절차 (개인정보)

1. 참여자에게 **무엇을 수집하고 무엇을 안 하는지** 1쪽 안내: 경로·사용자명은 `%USERPROFILE%` 치환, 패킷·키 입력 **절대 수집 안 함**, 마킹 전엔 디스크 쓰기 없음.
2. 설치: `docs/setup.md` + 로그온 자동 시작 옵션(UI, 송민석 담당) — 동의 체크 후에만.
3. 2주 후 `dumps/` 폴더를 참여자가 **직접 열어보고** 보내기. 보낸 파일은 `dumps_archive/`에만(gitignore) — **저장소에 절대 안 올림**(10/28 사고 재발 금지).
4. 설문 5문항: 그때 무엇을 하고 있었나 / 어떤 프로그램이 함께 켜져 있었나 / 증상(꺼짐·멈춤·느려짐) / 스스로 해결했나, 어떻게 / 보안·오버레이 프로그램 목록.
5. 모집 대상 우선순위: **Vanguard 사용자(LoL·Valorant)**, **Adobe 사용자(디자인·영상 전공)**, **RGB·Afterburner 사용자** — 우리 PC로 못 하는 M2·편집 프로그램 셀을 메운다.

### 3.4 측정 지표 (발표에서 "검증했다"고 말할 때 쓸 숫자)

| 지표 | 정의 | 현재 값 | 목표(12/11) |
|---|---|---|---|
| 검출률 | L1·L2에서 트리거 발생 / 시나리오 수 | L1 9/9 | L1 100%, L2 ≥ 80% |
| 증거 포착률 | 크래시 프로세스 모듈 목록에 개입 모듈 포함 | L1 9/9(관리자+ETW) | L2 ≥ 80% |
| 가설 정확도 | 엔진이 official 정답 culprit을 1순위로 지목 | 미측정 | ≥ 70% (official 케이스 한정) |
| 숨김 정밀도 | hidden 등급 중 실제로 무의미한 비율(설문 대조) | 미측정 | ≥ 90% |
| 오버헤드 | 30분 평균 CPU | 0.13% (실측) | < 1% 유지 |

---

## 4. 전문성 확보 (요구 3번 — 전문성)

### 4.1 근거의 등급을 정해 놓고 쓴다

| 등급 | 예 | 발표·엔진에서의 취급 |
|---|---|---|
| **A 벤더 공식 문서** | OBS Known Conflicts [1], Riot VAN 안내 [2], EA EAC 안내 [4], JetBrains AV 영향 [7][8], Adobe 알려진 문제 [10], Blender 매뉴얼 [11], Microsoft Dev Drive [9] | 엔진 규칙(rules.json)의 근거로 사용 가능. 발표에서 "알려진 충돌"이라고 말할 수 있음 |
| **B 학술** | ReBucket(ICSE 2012), Basu et al. "Why Did My PC Suddenly Slow Down?"(SysML 2007), EnSilo 후킹 연구(2016) — `post-midterm-plan.md` 5절 | 설계 근거 |
| **C 보도·전문 매체** | igor'sLAB RGB–EAC 사례 [5] | "보고된 사례"로만 인용 |
| **D 커뮤니티** | Steam 포럼·Reddit·thespike | 재현 **후보**로만. 발표에 단독 인용 안 함 |

지금 매트릭스의 빈칸 중 **한국 금융보안 모듈 ↔ 게임 충돌은 A·B·C 모두 없음** [12]. 그러니 이건 "알려진 충돌"이 아니라
**"우리가 측정한 자원 점유"**로만 말해야 한다. 그게 오히려 우리 데이터의 가치다(남이 안 측정한 것).

### 4.2 외부 전문가 접촉 (멘토 피드백 "전문자료·인터뷰" 대응)

| 대상 | 물을 것 | 접촉 경로 |
|---|---|---|
| 중앙대 전산정보원·공대 실습실 관리자 | 학내 PC 고장 접수 상위 유형, 금융보안·수업용 보안 SW 충돌 경험 | 교내 |
| PC 수리·AS 현장(용산/동네 수리점 1–2곳) | 손님이 들고 오는 "느려짐·꺼짐"의 실제 원인 분포, 진단 순서 | 방문 인터뷰 |
| 게임사 CS/QA(인턴·선배 네트워크) | 안티치트 충돌 민원 상위 프로그램, 공개 가능한 범위 | 학과 네트워크 |
| 보안 SW 벤더 기술지원(AhnLab·라온시큐어·잉카) | 호환성 공지 유무, 충돌 신고 채널 | 공식 문의 |

인터뷰 결과는 `docs/interviews/<날짜>-<대상>.md`로 기록하되, **실명·소속은 동의 없으면 쓰지 않는다**.

### 4.3 우리가 전문성을 '보여주는' 방식

- 매 주장에 등급 표시(A/B/C/D). 숫자는 실측만, 아니면 "미측정".
- 못 한 것도 쓴다: Vanguard 미검증, 1002·BugCheck 실수신 미확인, Photoshop 미설치 — **"안 되는 것도 데이터"**(CLAUDE.md 4번).
- 벤더 문서가 권하는 조치(제외 추가·드라이버 롤백)를 **우리가 대신 실행하지 않는 이유**를 설명한다 — 자동 조치 금지는 설계 원칙이고, 그래서 Lv0~2 플레이북은 "안내"로 끝난다.

---

## 5. 일정에 끼워 넣기

| 마일스톤 | 이 문서에서 추가되는 것 |
|---|---|
| ~11/5 | 2절 V1·V2·V4 실행 → NOTES 기록. Blender 설치 여부·Adobe 라이선스 확인(팀 결정) |
| ~11/13 | 케이스 내보내기(3.2 포맷) + V3(24시간 롤업). 지인 PC 안내문 1쪽 |
| ~11/27 | L3 상시 가동 결과 1차 집계, 인터뷰 2건 이상, L2 △ 셀 중 EAC 게임 1종 |
| 12/11 | 3.4 지표 표 채워서 발표 |

## 6. 팀 결정 필요

1. Photoshop·Premiere: 학교 라이선스 있나, 없으면 체험판 7일로 L2 1회전만 할까.
2. Vanguard(LoL) 설치해 M2를 우리 PC에서 볼까, 지인 PC로만 할까 — 상시 커널 드라이버라 설치 PC 주인 동의 필요.
3. 지인 PC 모집 인원·보상(없음 vs 소액) — 남건호(사례DB·실험) 주관?

---

## 출처

[1] OBS Project Knowledge Base, "Known Application Conflicts" — https://obsproject.com/kb/known-conflicts
[2] Riot Games Support, "VAN: Incompatible Software" — https://support.riotgames.com/en-us/valorant/performance/van-incompatible-software
[3] TheSpike.gg, Valorant "incompatible software component" 사례(ene.sys·rtcore64.sys) — 커뮤니티 — https://www.thespike.gg/valorant/error-codes/fixing-the-valorant-incompatible-software-component-detected-error
[4] EA Help, "Troubleshoot Easy Anti-Cheat" — https://help.ea.com/help/faq/troubleshoot-easy-anti-cheat
[5] igor'sLAB, BattlEye·EAC와 RGB 제어 소프트웨어 — 보도 — https://www.igorslab.de/en/battleye-and-easy-anti-cheat-rgb-control-software-causes-anew-bans-and-accounts-blocks-that-make-games-worthless/
[6] OriginSteamOverlayLauncher wiki, "Steam Overlay Troubleshooting" — 커뮤니티 — https://github.com/WombatFromHell/OriginSteamOverlayLauncher/wiki/Steam-Overlay-Troubleshooting
[7] JetBrains Support, "Antivirus Impact on Build Speed" — https://intellij-support.jetbrains.com/hc/en-us/articles/360006298560
[8] JetBrains Rider Support, "How Antivirus Software Affects Rider's Performance (on Windows)" — https://rider-support.jetbrains.com/hc/en-us/articles/360006365380
[9] Microsoft Learn, "Dev Drive" (Defender 성능 모드) — https://learn.microsoft.com/windows/dev-drive/
[10] Adobe Help, "Crash in Premiere Pro 15.2 and After Effects 18.2 on Windows laptops with older NVIDIA graphics card drivers" — https://helpx.adobe.com/x-productkb/multi/directX-crash.html
[11] Blender Manual, "Troubleshooting Graphics Hardware" — https://docs.blender.org/manual/en/latest/troubleshooting/gpu/index.html
[12] TouchEn nxKey·nProtect ↔ 게임 충돌: 10/28 검색에서 공식·학술·보도 출처 **없음**. 참고로 nxKey 1.0.0.87 이하 DLL 인젝션 취약점(AhnLab ASEC) — https://asec.ahnlab.com/en/82372/
