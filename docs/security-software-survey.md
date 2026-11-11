# 게임 보안 · 백신 · 논문 · 공개 데이터 조사 (2026-11-10)

정태웅 11/10 요구 1·2번의 결과다. 금융 보안 프로그램은 `korea-ksa-research.md`, 앞선 충돌 사례와 취약점 대조는 `external-survey.md`에 있다. 이 조사가 설계에 준 영향은 `product-architecture-v2.md`·`capstone-plan-v2.md`에 반영했다.

- 등급은 `conflict-test-plan.md` 4.1 기준이다: **A** 벤더·Microsoft·정부·시험 기관 공식 / **B** 학술 / **C** 전문 매체·독립 보안 연구·관리되는 오픈소스 / **D** 커뮤니티.
- ✓는 원문을 열어 확인한 것이다. ◎는 조사 결과를 받은 뒤 원문을 다시 열어 대조한 것이다. 표시가 없으면 검색 요약만 본 것이므로 인용 전에 원문을 확인한다.
- 모두 **보고된 자료**다. 팀의 측정값은 0절의 스파이크뿐이다.

## 0. 요약과 이 PC에서 확인한 것

1. **안티치트의 성능 영향을 잰 A·B·C 자료는 이번에도 찾지 못했다.** 벤더 표현은 "미미함"(EA), "프레임이 다소 줄 수 있음"(Bungie)뿐이고 수치가 없다. 금융 보안 모듈과 마찬가지로 측정 공백이다.
2. **안티치트 드라이버는 실제 공격에 쓰인다.**
   - 원신의 `mhyprot2.sys`는 2022년 랜섬웨어가 백신을 끄는 데 쓰였다.
   - XIGNCODE3의 `xhunter1.sys`(CVE-2026-3609)는 2026-09 랜섬웨어 조직이 썼다고 보고됐다.
   - 공개 목록 LOLDrivers(713건)에 `mhyprot*`, `xhunter1/2`, `ACE-BASE`, `BEDaisy` 등이 올라 있다.
3. **백신은 제품 이름만으로 "느리다"고 할 수 없다.** 2026-09 AV-Comparatives에서 주요 제품의 영향 점수는 대부분 3~10점대이고, V3는 AV-TEST 성능에서 2023-08 이후 계속 만점이다. 큰 사고는 프로그램 자체보다 **엔진·정의 파일 업데이트 직후**에 났다.
4. **플랫폼 방향:** 게임사는 요구 조건을 올리고 있다(Secure Boot, TPM 2.0, 메모리 무결성, IOMMU). Riot은 2026-06부터 조건을 만족하는 PC에서 드라이버를 게임 실행 때만 올리는 방식을 시작했다. Microsoft는 백신을 커널 밖으로 옮기는 계획을 발표했지만 아직 비공개 미리보기다. 안티치트에 대한 공식 발표는 찾지 못했다.
5. **지식 팩으로 쓸 공개 데이터가 있다:** LOLDrivers(Apache-2.0), CISA KEV(CC0), 식탁보 카탈로그(Apache-2.0), KISA 취약점 RSS, Microsoft 미니필터 고도 목록.
6. **공식 확인된 충돌 사례 48건을 모았다(6절).** 브라우저 벤더가 차단한 DLL 목록에 한국 보안 제품(AhnLab, 한컴, 테르텐, Print Guard)이 들어 있다. 계정·커널 위험 없이 재현할 수 있는 후보도 8건 골랐다. 지금 0건인 "공식 근거가 있는 충돌 사례"를 채울 재료다.

### 이 PC 스파이크 (11/10, 비관리자, 읽기 전용)

| 항목 | 결과 |
|---|---|
| 등록된 커널 드라이버 전체 SHA-256 계산 | 410개, 316.5MB, **1.5초**. 읽지 못한 파일 0개 |
| LOLDrivers 목록(713건, 해시 2,221개) 읽기 | 0.75초 |
| 해시가 정확히 일치하는 드라이버 | **0개** |
| 파일 이름만 같은 것 | 3개: `afd.sys`·`KslD.sys`(Windows 정상 파일), `xhunter2.sys` |
| `xhunter2.sys` (Wellbia XIGNCODE3, 로스트아크 실행 중이라 동작 중) | 이 PC 버전 **2026.9.16.465**. 공개된 취약 버전은 2026.6.1.192(CVE-2026-15430 ◎, CVSS 6.2, 수정 버전 표기 없음). 이 PC 버전이 더 새롭지만 **고쳐졌는지는 공개 자료로 판정할 수 없다** |
| 10/28에 있던 `xhunter1`·`ACE-BASE` | 포맷 뒤 지금은 등록돼 있지 않음 |

얻은 것은 두 가지다.
- 해시 대조는 검진 때마다 해도 될 만큼 싸다(검증됨).
- **이름만으로 대조하면 Windows 정상 파일이 걸린다.** 판정은 해시나 버전으로 해야 하고, 이름 일치는 "확인 필요" 표시까지만 써야 한다.

## 1. 게임 보안 프로그램

### 1.1 제품별 구조

| 제품 | 드라이버·서비스 | 올라오는 시점 | 요구 조건 | 등급 |
|---|---|---|---|---|
| Riot Vanguard | `vgk.sys`, `vgc.exe` | 기본은 부팅 시. 2026-06-24부터 조건을 만족하면 게임 실행 시(On-Demand, 선택) | On-Demand: Windows 11 25H2 이상, Secure Boot, TPM 2.0, VBS·메모리 무결성, IOMMU. Riot에 따르면 플레이어의 약 35%가 충족 | A ◎ |
| BattlEye | `BEDaisy.sys`, `BEService.exe` | 게임 실행 중에만 | — | A ✓ |
| Easy Anti-Cheat | (기존 조사와 같음) | 게임 실행 시 | — | B |
| EA anticheat (Javelin) | 이름 비공개 | 게임 실행 중에만 | Battlefield 6: Secure Boot 필수 | A ✓ |
| Activision RICOCHET | 이름 못 찾음 | — | TPM 2.0 + Secure Boot | A ✓ |
| Wellbia XIGNCODE3 | `xhunter1.sys`, `xhunter2.sys` | 못 찾음(이 PC에서는 게임 실행 중 동작, 시작 유형 수동) | — | A ◎(CVE 기록) |
| Tencent ACE | `ACE-BASE.sys` | 못 찾음 | — | C ◎ |
| HoYoverse | `mhyprot2.sys` 등 5종 | 게임 없이도 드라이버만 따로 로드 가능 | — | C ✓ |
| nProtect GameGuard | (이름은 D등급 자료뿐) | 게임 실행 중에만(검색 요약) | Helldivers 2가 2026-10-20 EAC로 교체 | C ✓ |
| Steam / VAC | 커널 구성요소를 확인한 자료 없음 | — | Steam은 2024-10부터 상점 페이지에 커널 안티치트 표시를 요구 | 검색 요약 |
| Google Play Games (PC) | Windows 하이퍼바이저 플랫폼 사용 | — | 하드웨어 가상화 켜기, RAM 8GB, 물리 코어 4개 | A ✓ |

STOVE·로스트아크 한국 서버, 넥슨 NGS, NC Purple, 카카오게임즈, Battle.net, Denuvo Anti-Cheat, HackShield는 공식 자료로 드라이버 이름이나 로드 시점을 확인하지 못했다.

### 1.2 문서화된 충돌 (새로 찾은 것)

| 충돌 | 증상 | 해결·상태 | 등급 |
|---|---|---|---|
| Vanguard × OEM 저장장치·무선랜 드라이버(`iastorvd.sys`, `secnvme.sys`, `solidnvm.sys`, `asstahci64.sys`, `mtkwl6ex.sys`) | "VAN: INCOMPATIBLE OEM DRIVER" | Microsoft 기본 드라이버로 교체, MediaTek 드라이버 갱신 | A ✓ |
| Vanguard × 오버레이·캡처 도구·추가 백신 | VAN 289 | 클린 부팅, 도구 종료, 타사 백신 임시 제거 | A ✓ |
| Vanguard × TPM·UEFI 미설정 | VAN 9005 | TPM 2.0·Secure Boot 켜기 | A ✓ |
| Vanguard × 메인보드 펌웨어(ASUS·Gigabyte·MSI·ASRock, 부팅 전 DMA 보호) | 실행 차단 | BIOS 업데이트(2025-12-18) | A ✓ |
| Vanguard × Windows 11 KB5041585(하드웨어 강제 스택 보호) | 드라이버 로드 실패(2024-08) | 해당 보호 기능 끄기 | C ✓ |
| Vanguard × 이름 미공개 타사 앱 | 재시도 반복으로 "클릭 지연·FPS 저하" | Riot 회고(2024-08-22) | A ✓ |
| BattlEye × SpeedFan류 도구 / NVIDIA 저지연 모드 / Driver Verifier | 강제 퇴장, 도구 중단, 블루스크린, 실행 실패 | 제거하거나 기능 끄기 | A ✓ |
| Google Play Games × 다른 에뮬레이터 | "일부 에뮬레이터가 동작하지 않을 수 있음" | Hyper-V 하이퍼바이저 끄기 | A ✓ |
| Defender × WinRing0 드라이버(Fan Control·Razer·SteelSeries) | 2025-03 해킹 도구로 격리 | 벤더 업데이트 | C(검색 요약) |

찾지 못한 것:
- 안티치트끼리의 충돌에 대한 벤더 공식 발표
- 한국 금융 보안 모듈과 안티치트의 충돌 문서
- Vanguard가 막는 드라이버의 전체 목록

### 1.3 취약점과 악용

| 드라이버 | 권고 | 실제 악용 | 공개 목록 | 등급 |
|---|---|---|---|---|
| `mhyprot2.sys` | CVE-2020-36603 | 있음 — 2022-07 랜섬웨어가 백신 종료에 사용 | LOLDrivers(해시 77개) | A·C ✓ |
| `xhunter1.sys` | CVE-2026-3609: 10.0.10011.16384~2023.12.7.78, 임의 프로세스에 전체 접근 권한. 이전 수정(KVE 2023-5589)이 불완전 | 있음 — 2026-09 INC 랜섬웨어 계열 조직(Beazley 보고) | LOLDrivers | A ◎ / C ✓ |
| `xhunter2.sys` 2026.6.1.192 | CVE-2026-15430: 일반 사용자가 SYSTEM 권한 획득, 보호 프로세스 종료 가능 | 공개 시연 코드 | LOLDrivers | A ◎ |
| `ACE-BASE.sys` 1.0.2202.6217 | CVE-2024-22830: 접근 제어 미흡 → 권한 상승 | 기록 없음 | LOLDrivers | C ◎ |
| `GameDriverX64.sys`(Tower of Fantasy) | CVE-2025-61155 | 있음 — DragonForce 랜섬웨어 | LOLDrivers | C ✓ |
| `BEDaisy.sys` | CVE 없음, 권한 상승 항목으로 등재 | 기록 없음 | LOLDrivers(해시 272개) | C ✓ |
| `vgk.sys`, EAC, GameGuard | **LOLDrivers에 없음.** vgk.sys CVE는 찾지 못함 | — | — | — |

### 1.4 성능

A·B·C 등급의 측정값은 없다. 간접 자료는 메모리 무결성(HVCI)의 영향뿐이다(`external-survey.md` 6절: 평균 약 6%). Vanguard On-Demand와 FACEIT가 이 기능을 요구하므로, 앞으로는 "안티치트가 요구하는 Windows 보안 기능의 비용"이 실질적인 성능 비용이 된다.

## 2. 백신·엔드포인트 보안

### 2.1 측정된 성능

| 자료 | 내용 | 등급 |
|---|---|---|
| AV-Comparatives 성능 시험 2026-09 (i7·16GB·SSD) | 영향 점수(낮을수록 가벼움): McAfee 3.1, K7 3.2, Kaspersky 3.3, Norton 4.9, Avast 5.2, AVG 5.3, Bitdefender 8.7, ESET 9.5, **Microsoft 13.5**, Trend Micro 17.5, Malwarebytes 19.1, TotalAV 30.5, Sophos 39.8. 점수는 (90 − 파일 작업 점수) + (100 − Procyon 점수)로 계산하면 맞는다(공식 설명은 없음). Defender의 감점은 파일 작업 쪽이다(80/90) | A ◎ |
| 같은 시험 2026-04 (i3·8GB 저사양) | Defender 12.9, Sophos 33.4 등 순위가 비슷함 | A ✓ |
| AV-TEST 가정용 Windows, AhnLab V3 | 성능 점수: 2023-08부터 2026-08까지 모두 6점 만점. 알약은 시험 목록에 없음 | A ◎ |
| Microsoft Dev Drive (2023) | Defender 비동기 검사로 빌드 시간 "최대 30% 개선" | A ✓ |
| Al-Saleh & Hamdan, JUCS 2019 | 미니필터 단위 측정: 백신 부담은 주로 파일 **열기**에 걸림 | B ✓ |

금융 보안 모듈과 달리 백신에는 공인된 측정 체계가 있다. 우리 실험의 방법(재부팅, 첫 실행 분리, 보안 SW 없는 기준)은 이 체계를 따른 것이다.

### 2.2 충돌과 사고

| 사례 | 내용 | 등급 |
|---|---|---|
| 실시간 백신 2개 | 타사 백신이 등록되면 Defender는 자동으로 꺼진다. 보안 센터 서비스(`wscsvc`)가 꺼져 있으면 둘 다 켜진 채로 남고 "성능에 영향, 지원 안 함" | A ✓ |
| CrowdStrike 2024-07-19 | 내용 업데이트 파일(`C-00000291-*.sys`) 오류로 `csagent.sys`가 범위 밖 메모리를 읽음. 850만 대 영향 | A ✓ |
| Defender 2023-01-13 | 정의 버전 1.381.2134.0~2163.0이 바로가기 파일을 삭제. 1.381.2164.0에서 수정 | A ✓ |
| 알약 2022-08-30 | 공개용이 Windows 기본 프로세스를 랜섬웨어로 오탐, 검은 화면·부팅 불가 사례. 이용자 약 1,600만 명. 피해 대수는 찾지 못함 | C ✓ |
| V3 2008-07 | Windows XP SP3의 `lsass.exe` 삭제, 부팅 불가 | C ✓ |
| McAfee 2010-04-21 | DAT 5958이 `svchost.exe`를 격리 | A ✓ |
| Windows 11 24H2 업데이트 보류 | `sprotect.sys`(SenseShield) 1.0.2.372·1.0.3.48903: 블루스크린·검은 화면. 보류 ID 56318982, 2025-04-04~2025-10-15 | A ✓ |
| Docker × Avast 강화 모드 | Hyper-V·WSL 차단, 오류 `…/CreateVm/HCS/ERROR_NOT_SUPPORTED` | A ✓ |

사고의 공통점은 **업데이트 직후**라는 것이다. 사건 시점의 백신 엔진·정의 버전과 갱신 시각을 남기면 엔진이 "방금 업데이트됨"을 근거로 쓸 수 있다. 수집기 추가 후보이고, 스파이크가 먼저다.

### 2.3 백신 자체의 취약점

| 드라이버 | 내용 | 등급 |
|---|---|---|
| `aswArPot.sys`(Avast·AVG 안티 루트킷) | CVE-2022-26522, 22.1 이전. 랜섬웨어가 다른 백신을 끄는 데 사용 | A ✓ |
| `truesight.sys` 2.0.2(RogueKiller) | 서명된 변종 2,500개 이상이 악용됨. Microsoft 차단 목록 2024-12-17 갱신 | C ✓ |
| `zam64.sys`(Zemana) | CVE-2024-1853, 임의 프로세스 종료 | A ✓ |
| ESET 2026-03 조사 | 보안 제품을 끄는 도구 약 90종 중 54종이 취약 드라이버 35개를 악용 | C ✓ |

### 2.4 우리가 쓸 수 있는 읽기 전용 진단 도구

| 도구 | 용도 | 권한 |
|---|---|---|
| `New-MpPerformanceRecording` / `Get-MpPerformanceReport` | Defender가 어떤 파일·프로세스 검사에 시간을 썼는지 | 관리자. 기록 파일을 쓰므로 마킹 이후에만 |
| `Get-MpComputerStatus` | Defender 동작 상태(정상·수동), 엔진·정의 버전 | 미확인 |
| `fltmc` | 미니필터 목록과 고도 | 관리자 |
| Microsoft 미니필터 고도 목록 | 드라이버 이름 → 제조사. 백신 대역은 320000~329998 | 공개 문서 |

구조만으로 잡을 수 있는 신호:
- 백신 대역에 드라이버가 둘 이상 있음
- 타사 백신이 등록돼 있는데 Defender가 정상 모드임
- `wscsvc`가 꺼져 있음

### 2.5 플랫폼 방향

- 2025-06-26 Microsoft(A ◎): "다음 달 Windows 엔드포인트 보안 플랫폼의 비공개 미리보기를 일부 협력사에 제공", 백신이 "앱처럼 사용자 모드에서 실행"될 수 있게 함. 이 글에 안티치트·게임 언급은 없다.
- 2026-03 기준 공개 API 일정은 없다(C ✓). 커널 드라이버·미니필터를 열거하는 방식은 프로젝트 기간에 계속 유효하다.

## 3. 논문

`related-work.md`에 없는 것만 적는다.

| 논문 | 핵심 | 우리에게 주는 것 |
|---|---|---|
| Yu et al., ASPLOS 2014 ◎ | 실제 실행 기록 19,500건: 드라이버가 전체 성능 영향의 38%, 드라이버 간 상호작용이 26% | 느려짐에서 드라이버를 봐야 하는 가장 강한 근거 |
| Mozilla Hacks 2023 (A ◎) | Windows 사용자 70% 이상에 외부 DLL 주입. Firefox 크래시의 2% 남짓이 외부 코드 안에서 발생(간접 원인은 더 많음). 가장 흔한 주체는 백신, 일부 국가에서는 뱅킹 | 주입 모듈에 가중치를 주는 근거 |
| Ganapathi et al., LISA 2006 ✓ | 617명의 OS 크래시 2,528건: 7개 벤더가 75%, 다수가 장치 드라이버 | 오래됐지만 드라이버 우선 가정의 출처 |
| RETracer, ICSE 2016 (초록) | 스택 맨 위 프레임이 범인이 아닌 경우가 많음 — 분류 오류 3분의 2 감소 | 오류 모듈만 보고 단정하지 않는 이유 |
| RCACopilot, EuroSys 2024 (초록) | 정해진 수집 단계 + LLM으로 정확도 최대 0.766 | "수집·판단은 코드, 설명은 AI" 구조의 근거 |
| Roy et al., FSE 2024 (초록) | 도구로 증거를 더 가져올 수 있는 에이전트가 사실 정확도가 높음 | 2단계 도구 호출 대화의 근거 |
| AutoCrashFL, arXiv 2510.22530 (초록, 심사 중) | LLM 크래시 원인 지목 1순위 30%(기준선 17%) | AI 단독 정확도의 현실적 수준 |
| OWASP LLM06:2025 (A ◎) | "영향이 큰 조치는 사람 승인", "허용 여부를 LLM이 정하게 두지 말고 하위 시스템에서 권한을 강제" | 실행기 설계와 같은 원칙 |
| Anthropic 에이전트 프레임워크 2025-08 (A ✓) | 기본은 읽기 전용, 시스템을 바꾸기 전에는 사람 승인 | 조치 수준 Lv0~Lv3의 외부 근거 |

**위험 점수의 선례** (A ✓)

| 선례 | 참고할 점 |
|---|---|
| CVSS v4 | 기본 점수와 사용자 환경 보정을 분리 |
| CISA KEV | 실제 악용 여부를 별도 표시 |
| CISA SSVC | 결정 나무로 조치 수준을 도출 |
| OpenSSF Scorecard | 항목별 0~10점에 위험도 가중 평균 |
| Windows 안정성 지수 | 최근 실패에 더 큰 가중 |

우리 점수는 이들과 달리 계산 과정을 전부 공개하는 방식이다. Microsoft Smart App Control은 클라우드 예측이라 사용자가 이유를 볼 수 없다.

## 4. 지식 팩으로 쓸 공개 데이터

| 출처 | 라이선스 | 형식 | 쓸 곳 | 주의 |
|---|---|---|---|---|
| LOLDrivers ◎ | Apache-2.0 | JSON·CSV, 713건(취약 578, 악성 124 — README 기준 702건), 계속 갱신 | `vuln-feed`: 드라이버 해시 대조 | 고지문 유지 |
| Microsoft 드라이버 차단 목록 ✓ | 명시 없음 | XML, 분기별 갱신 | `vuln-feed` | 재배포하지 말고 실행 시 받기 |
| CISA KEV ✓ | CC0 | JSON·CSV, 1,773건 | "실제 악용" 표시 | — |
| CVE 목록(cvelistV5) ✓ | CVE 이용 약관 | JSON | 버전 범위 확인 | — |
| KISA 취약점 정보 RSS ✓ | 명시 없음 | RSS | 국내 권고 알림 | 제품·버전은 사람이 옮겨야 함 |
| 식탁보 카탈로그 ◎ | Apache-2.0(로고·상표 제외) | `Catalog.xml`: 사이트별 필요 프로그램 | `product-catalog`: 은행 → 보안 모듈 | 커뮤니티 유지, "최선 노력" 수준 |
| Microsoft 미니필터 고도 목록 ✓ | 명시 없음 | 문서 표, 1,000행 이상 | `product-catalog`: 드라이버 → 제조사 | 번들 전 라이선스 확인 |
| Firefox DLL 차단 목록 ✓ | MPL-2.0 | 소스 파일, 약 200건, 버그 번호 주석 | `conflict-kb` 참고 | 파일 단위 카피레프트 — 그대로 싣지 말고 버그 번호를 인용해 직접 작성 |
| winget-pkgs ✓ | MIT | 매니페스트 | 최신 버전 확인 | — |
| 구라제거기 ✓ | 명시 없음 | 제거 대상 목록 **비공개** | 인용만 | 점수 입력으로 쓰지 않고 검증 기준으로만(`capstone-plan-v2.md` E2) |

레이블이 붙은 Windows 크래시·블루스크린 공개 데이터셋은 찾지 못했다(Loghub의 Windows 로그 1억 1,460만 줄은 레이블이 없다).

## 5. 비슷한 도구와의 차이

| 도구 | 하는 일 | 우리와 다른 점 |
|---|---|---|
| Windows 설정 AI 에이전트, Copilot PC Insights (C ✓) | 설정 변경(허락 후), 현재 상태 요약 | 사건 기록·원인 순위·되돌리기 없음. Copilot은 "자동으로 해결할 수 없음" |
| Dell SupportAssist, HP Support Assistant, Lenovo Vantage (A ✓) | 제조사 진단 | 진단 도구 자체에 권한 상승 취약점이 반복됨(CVE-2025-36613, CVE-2026-19915, CVE-2025-6230) — 우리가 상시 관리자 서비스를 두지 않는 이유 |
| WhoCrashed, BlueScreenView (C ✓) | 덤프 → 드라이버 이름 | 블루스크린만, 맥락 없음, 스스로 "정확하지 않을 수 있음"이라고 밝힘 |
| Process Lasso (C ✓) | 우선순위 조정 | 원인 분석 없음 |
| 공개 저장소의 LLM PC 진단 도구 3종 (D ✓) | 이벤트 로그 + LLM, 일부는 LLM이 명령 실행 | 점수·권한 수준·승인 단계가 없거나 문자열 차단 목록뿐 |

## 6. 버그 리포트·공식 알려진 문제 — 규칙과 시험 사례로 쓸 것

원문에서 확인한 사례는 48건이다. Microsoft·Firefox·Chromium 자료는 원본 파일을 직접 읽었다. GitHub·은행 페이지는 요약 도구를 거쳤으므로, 규칙에 문자열을 넣기 전에 다시 확인한다. Firefox 차단 목록의 한국 관련 항목 4건은 받아 둔 원본 파일에서 직접 대조했다(◎).

### 6.1 범주별 대표 사례

| 범주 | 피해 → 원인 (파일) | 증상 | 해결 | 등급 |
|---|---|---|---|---|
| 금융·문서 보안 | Firefox → AhnLab `nzbrcom.dll`(전 버전) | 접근성 경로 크래시(버그 1311969) | Firefox가 로드 차단 | A ◎ |
| | Firefox → 한컴오피스 셸 확장 `hncshellext64.dll` ≤1.0.0.3 | 파일 선택 창을 열 때 크래시(버그 1581092) | 차단 | A ◎ |
| | Firefox → 테르텐 `T_Prevent64.dll`, `TDCommonLib64.dll` | 콘텐츠 프로세스 크래시(버그 1904087) | 차단 | A ◎ |
| | Firefox → MWStory Print Guard `printguard64.dll` ≤25.11.21.1 | 읽기 접근 위반(버그 2011909) | 차단 | A ◎ |
| | IPinside(하나은행) → Chrome 샌드박스, TAP-Windows 가상 어댑터 | 설치 안내 반복, 설치 확인 지연 | 어댑터 삭제 등 은행 안내 | A ✓ |
| | Windows 11 24H2 → SenseShield `sprotect.sys` | 멈춤, 블루스크린·검은 화면 | 앱 업데이트, 보류 2025-10-15 해제 | A ✓ |
| 백신 | Outlook 등 32비트 앱 → Trellix <35.31.25 × KB5027231 | 실행 안 됨, 접근 위반 "module is unknown" | Trellix 업데이트 | A ✓ |
| | Chrome·Edge 78+ → Symantec Endpoint Protection ≤14.2 RU2 | "Aw, Snap!", STATUS_INVALID_IMAGE_HASH | SEP 업데이트 | A ✓ |
| | Firefox → Trend Micro·McAfee DLP·Webroot·360·Kingsoft·Cylance DLL | 크래시·교착 | 차단(버그 번호 있음) | A ✓ |
| | Windows 부팅 → CrowdStrike `C-00000291*.sys` | 블루스크린 0x50·0x7E 반복 | 안전 모드에서 파일 삭제 | A ✓ |
| 오디오 주입 | Firefox·Godot 4.2·VS Code·ImGui 앱 → Nahimic(`NahimicOSD.dll` 등, MSI·ASUS 보드에 기본 설치) | GPU 프로세스 크래시, 창 없이 멈춤, 시작 20~50초, 0xC0000005 | Nahimic 서비스 중지 | A·C ✓ |
| | 오디오 장치 → Dirac `cridspapo.dll` | 소리 안 남 | 업데이트 보류 | A ✓ |
| 모니터링·RGB 드라이버 | EA 게임 → `winring0x64.sys`, `hwinfo64a.sys`, `cpuz141_x64.sys`, `rzpnk.sys`, `eneio64.sys` 등 18종 | 오류 #119(차단 목록) | 해당 도구 끄기·제거 | A ✓ |
| | LibreHardwareMonitor·FanControl·Razer Synapse → Defender | WinRing0를 해킹 도구로 격리(2025-03) | 새 드라이버로 교체 | C ✓ |
| | ARC Raiders·THE FINALS → `inpoutx64` | 접근 위반, 재부팅(2026-08 업데이트 이후) | 2026-08-26부터 드라이버 차단 | A ✓ |
| | Corsair iCUE → Armoury Crate·MSI Center·RGB Fusion·Synapse 등 | 자원 충돌 | 안 쓰는 것 제거 | A ✓ |
| 셸 확장·꾸미기 | 시작 메뉴·부팅 → ExplorerPatcher, StartAllBack | 시작 메뉴 안 열림, 부팅 실패 | 앱 업데이트 | A ✓ |
| | Chrome → TortoiseSVN, 가상 카메라 필터 | 크래시 | Chrome이 로드 차단 | A ✓ |
| OS 업데이트 | Windows 11 24H2 → Voicemeeter 드라이버 <3.3.1.9 | 블루스크린 MEMORY_MANAGEMENT | 앱 업데이트 | A ✓ |
| | 24H2 → Intel SST `IntcAudioBus.sys` 10.29.0.5152 | 블루스크린 | 드라이버 업데이트 | A ✓ |
| | OBS·NDI → KB5063878(2025-08) | 심한 끊김 | KB5065426 | A ✓ |
| 오버레이 | 게임 → NVIDIA 앱 게임 필터 | "최대 15% 느려짐"(Tom's Hardware, 제목만 확인) | 게임 필터 끄기 | C |

나머지(AutoCAD 2022, Safe Exam Browser, Ubisoft 게임, Git for Windows × 강제 ASLR, PowerToys × Malwarebytes 등)까지 포함한 48건 전체와 "파일 이름 → 제품" 목록은 `engine/data/external/_reports/bug-reports-20261110.md`에 원자료로 두었다. `conflict-kb`·`product-catalog` 팩의 첫 재료다.

이 PC와 직접 관련된 것: NVIDIA 앱이 설치돼 있고 오버레이 프로세스가 상시 CPU를 쓰고 있다(11/10 실측, 스레드 하나). 위 Tom's Hardware 기사와 같은 계열의 현상일 수 있으나 기사 본문은 확인하지 못했다.

### 6.2 기계로 읽을 수 있는 출처

| 출처 | 형식 | 비고 |
|---|---|---|
| Firefox DLL 차단 목록 | `WindowsDllBlocklistDefs.in`, 약 180건, 버그 번호 주석 | MPL-2.0 |
| Chromium 고정 차단 목록 | `hardcoded_blocklist.cc`, 34건 | 더 큰 목록은 소스에 없음 |
| EA anticheat 차단 드라이버 목록 | 일반 HTML, 경로 → 소프트웨어 | 18종 |
| Windows 릴리스 상태 페이지 | HTML. 오래된 항목은 사라지므로 웹 아카이브 필요 | 기계용 피드는 기업 계정 전용 |

### 6.3 계정·커널 위험 없이 재현할 수 있는 후보 (E3의 공개 사례 묶음용, 아직 실행하지 않음)

1. LibreHardwareMonitor 0.9.4 × Defender: WinRing0 탐지·격리
2. Git for Windows × Exploit Protection "강제 임의화": fork 오류. 설정을 되돌릴 수 있음
3. Windhawk ≤1.6.1.0 × Firefox: DLL 차단 기록
4. AhnLab 모듈 × Firefox: `nzbrcom.dll` 차단 기록 — 금융 보안 설치 후 상태에서 바로 확인 가능
5. IPinside × TAP-Windows 어댑터(OpenVPN 설치): 설치 확인 지연
6. Safe Exam Browser 3.7 × Windows 11 24H2 이상: 실행 실패
7. NVIDIA 앱 게임 필터 켬·끔: 프레임 차이(크래시 없는 느려짐 사례)
8. Nahimic × Godot 4.2~4.3: Nahimic이 기본 설치된 메인보드에서만

Voicemeeter, `sprotect.sys`, `inpoutx64`, 안티치트 사례는 커널·계정 위험 때문에 제외한다.

### 6.4 주변기기·모니터 유틸 (11/10 밤 추가, 정태웅 요청)

로지텍 G HUB, 와콤 드라이버, LG 모니터 앱처럼 장치와 함께 깔리는 프로그램이 PC를 느리게 하거나 화면을 깜빡이게 한다는 이야기가 커뮤니티와 유튜브에 많다. 공식 자료를 찾아본 결과는 다음과 같다.

| 프로그램 | 찾은 근거 | 등급 |
|---|---|---|
| Logitech G HUB | CPU가 갑자기 치솟는다는 사용자 보고(Blizzard·Steam 포럼). 2021년 Logitech 직원이 원인을 찾아 핫픽스를 냈다는 포럼 글. 릴리스 노트의 메모리 누수 수정 1건(Discord 연동 기능 한정). **공식 "알려진 문제" 문서는 못 찾음** | D (검색 요약) |
| Wacom 드라이버 | Windows Ink를 켜면 Photoshop·Substance Painter에서 펜이 밀린다는 Adobe 커뮤니티 글 여러 건. 끄면 필압이 사라졌다는 보고도 있음. **Wacom 공식 문서는 못 찾음** | D (검색 요약) |
| LG OnScreen Control | 켜 두면 Lightroom 화면이 멈추고 깜빡이며, 끄면 정상이 된다는 Adobe 커뮤니티 글(2022). Intel 커뮤니티에 게임 멈춤 보고. **LG 공식 문서에는 깜빡임·지연 언급 없음** | D (검색 요약) |
| (비교) Corsair iCUE, Nahimic | 벤더가 직접 충돌 프로그램 목록·제거 안내를 냄(6.1절) | A |

**이 PC에서 잰 값(11/10 밤, 30초, 비관리자).** Logitech G HUB 2026.7이 설치돼 동작 중이다.

| 프로세스 | CPU (PC 전체 기준) | 전용 메모리 | 부팅 후 53.7시간 동안 쓴 CPU 시간 |
|---|---|---|---|
| `lghub_agent.exe` | 0.003% | 143.5MB | 2.4분 |
| `lghub_system_tray.exe` | 0.003% | 87.2MB | 3.0분 |
| `lghub_updater.exe` | 0% | 32.8MB | 0.1분 |
| `logi_lamparray_service` | 0.01% | 4.7MB | 1.5분 |
| (비교) NVIDIA Overlay + FvContainer + PresentMon | 6.1% + 6.1% + 6.1% | 1.6GB + 14MB + 20MB | 각각 약 48시간 |

- G HUB는 지금 이 PC에서 CPU를 거의 쓰지 않는다. 메모리는 합쳐서 약 270MB다. 같은 PC에서 NVIDIA 앱의 통계 기능은 스레드 세 개를 부팅 이후 내내 쓰고 있다.
- G HUB는 로컬 포트 3개(127.0.0.1의 45654, 9010, 9180)를 열고, 서비스 2개와 드라이버 4개를 둔다.
- 커뮤니티 보고는 "가끔 갑자기 치솟는다"는 형태라, 30초 측정으로는 반박도 확인도 되지 않는다. 수집기의 24시간 분 단위 기록이 이런 간헐 증상을 잡는 용도다.
- 이 PC에는 Wacom 태블릿 드라이버(Windows 기본 `WacomPen`만 있고 정지 상태)와 LG 모니터 앱이 없어 확인하지 못했다. DTS 오디오 처리 서비스(`DtsApo4Service`)가 동작 중인데, Nahimic과 같은 "오디오 효과" 계열이라 감시 목록에 넣는다.

**설계에서 다루는 방법**

- 이런 프로그램에는 공식 근거가 없으므로 "이 프로그램은 느리다"는 규칙을 만들지 않는다(D등급은 점수를 올리지 못한다). 대신 **이 PC에서 잰 자원 사용(C축)과 침투 깊이(F축: 서비스·드라이버·포트·주입 DLL)** 로 점수가 나온다. 문제가 있는 PC에서는 높게, 없는 PC에서는 낮게 나오는 것이 맞다.
- `product-catalog` 팩에 이름을 넣어 "미분류"가 아니라 제품으로 묶이게 한다: `lghub_*`, `logi_*`(Logitech), `WTabletServicePro`·`Wacom*`(Wacom), `OnScreen Control`·`LG Switch`(LG), `DtsApo4Service`(DTS). 엔진 규칙에는 이미 `logi`(입력 유틸)와 `onscreen`(모니터 유틸) 범주가 있다. Wacom과 오디오 효과 계열은 없다.
- 깜빡임은 엔진에 증상과 범주(`flicker` × 모니터 유틸)가 이미 있다. 점검은 "해당 유틸을 끄고 증상이 사라지는지"이고, Microsoft의 화면 깜빡임 안내도 같은 방법(의심 앱을 하나씩 제거)을 쓴다.
- 영향 실험에서는 G HUB를 포함한 지금 상태가 "설치 전" 기준이다. 실험 기간에 G HUB를 켜거나 끄거나 업데이트하면 안 된다.

## 7. 못 찾은 것

- Logitech G HUB·Wacom·LG 모니터 앱의 성능 문제에 대한 벤더 공식 문서

- 국내 게임사(넥슨·스마일게이트·카카오게임즈·엔씨)의 공식 "충돌 프로그램" 목록 — 백신 예외 등록 안내뿐
- 카카오톡·네이버 웨일의 보안 모듈 크래시 공지, nProtect Online Security 충돌에 대한 은행 FAQ
- 안티치트의 독립 성능 측정
- 금융 보안 모듈과 안티치트의 충돌 문서
- 국내 가정용 백신 점유율, 알약 2022 사고의 피해 대수
- Fasoo·SoftCamp·MarkAny 등 문서 보안 제품의 공개 장애 보고
- Microsoft의 최근 "크래시 중 서드파티 드라이버 비율"(2004년의 70%만 있음)
- WinDbg식 결정적 분석과 LLM을 결합해 Windows 덤프로 평가한 논문

## 출처 (6.4절, 검색 요약만 확인)

- G HUB: https://us.forums.blizzard.com/en/wow/t/psa-logitech-g-hub-cpu-bug/798091 · https://steamcommunity.com/discussions/forum/11/3315110799617977308 · https://support.logi.com/hc/ko/articles/37661444333207-Logitech-G-HUB
- Wacom × Windows Ink: https://community.adobe.com/questions-712/several-problems-including-brush-lag-with-new-wacom-tablet-windows-ink-photoshop-1113211 · https://community.adobe.com/t5/substance-3d-painter-bugs/substance-painter-11-1-0-pen-lagging/idi-p/15616740
- LG OnScreen Control: https://community.adobe.com/questions-675/lightroom-is-lagging-and-freezing-due-to-lg-onscreen-control-965415 · https://community.intel.com/t5/Intel-ARC-Graphics/Valorant-crashes-Freezes-in-buy-menu-50-of-the-time/m-p/1645252
- Microsoft 화면 깜빡임 안내: https://support.microsoft.com/zh-TW/Windows/Hardware/Display-Graphics/troubleshoot-screen-flickering-in-windows

## 출처 (6절)

- Windows 11 24H2 해결된 문제: https://learn.microsoft.com/en-us/windows/release-health/resolved-issues-windows-11-24h2
- Firefox DLL 차단 목록: https://raw.githubusercontent.com/mozilla-firefox/firefox/main/toolkit/xre/dllservices/mozglue/WindowsDllBlocklistDefs.in
- Chromium 차단 목록: https://raw.githubusercontent.com/chromium/chromium/main/chrome/chrome_elf/third_party_dlls/hardcoded_blocklist.cc
- EA anticheat: https://help.ea.com/en/articles/platforms/pc-ea-anticheat/
- 하나은행 IPinside FAQ: https://biz.kebhana.com/cont/customer/customer04/customer047/customer0471/index.jsp
- Symantec × Chrome: https://knowledge.broadcom.com/external/article/175842
- CrowdStrike 복구: https://support.microsoft.com/en-us/help/5042421
- Nahimic: https://docs.signalrgb.com/troubleshooting/software-conflicts/nahimic/ · https://github.com/godotengine/godot/issues/85608 · https://github.com/microsoft/vscode/issues/56644 · https://github.com/ocornut/imgui/issues/3321
- WinRing0: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/1660
- Corsair iCUE: https://www.corsair.com/us/ko/explorer/gamer/accessories/list-of-programs-known-to-conflict-with-corsair-icue/
- NVIDIA 앱: https://www.tomshardware.com/pc-components/gpus/we-tested-nvidia-app-performance-problems-games-run-up-to-15-percent-slower-with-the-app

## 출처 (다시 확인한 것 ◎)

- Riot Vanguard On-Demand: https://www.riotgames.com/en/news/vanguard-on-demand
- CVE-2026-3609 / CVE-2026-15430: https://cveawg.mitre.org/api/cve/CVE-2026-3609 · https://cveawg.mitre.org/api/cve/CVE-2026-15430
- CVE-2024-22830: https://www.tenable.com/cve/CVE-2024-22830
- LOLDrivers: https://github.com/magicsword-io/LOLDrivers
- AV-Comparatives 2026-09: https://www.av-comparatives.org/tests/performance-test-september-2026/
- AV-TEST AhnLab: https://www.av-test.org/en/antivirus/home-windows/manufacturer/ahnlab/
- Windows Resiliency Initiative: https://blogs.windows.com/windowsexperience/2025/06/26/the-windows-resiliency-initiative-building-resilience-for-a-future-ready-enterprise/
- Yu et al. ASPLOS 2014: https://www.microsoft.com/en-us/research/publication/comprehending-performance-from-real-world-execution-traces-a-device-driver-case/
- Mozilla Hacks 2023: https://hacks.mozilla.org/2023/03/letting-users-block-injected-third-party-dlls-in-firefox/
- OWASP LLM06: https://owasp.github.io/www-project-top-10-for-large-language-model-applications/2_0_vulns/LLM06_ExcessiveAgency
- 식탁보 카탈로그: https://github.com/yourtablecloth/TableClothCatalog

그 밖의 출처(✓)

- BattlEye FAQ: https://www.battleye.com/support/faq/
- Riot OEM 드라이버: https://support.riotgames.com/riot/performance/van-incompatible-oem-driver
- EA anticheat: https://www.ea.com/security/news/eaac-deep-dive
- Trend Micro mhyprot2: https://www.trendmicro.com/en_us/research/22/h/ransomware-actor-abuses-genshin-impact-anti-cheat-driver-to-kill-antivirus.html
- Beazley xhunter1: https://labs.beazley.security/articles/an-inc-ransomware-affiliates-byovd-buffet
- Defender 호환성: https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-compatibility
- CrowdStrike 기술 설명: https://www.crowdstrike.com/en-us/blog/falcon-update-for-windows-hosts-technical-details/
- Microsoft 850만 대: https://blogs.microsoft.com/blog/2024/07/20/helping-our-customers-through-the-crowdstrike-outage/
- Check Point truesight: https://research.checkpoint.com/2025/large-scale-exploitation-of-legacy-driver/
- ESET EDR killers: https://www.welivesecurity.com/en/eset-research/edr-killers-explained-beyond-the-drivers/
- 미니필터 고도 목록: https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/allocated-altitudes
- Defender 성능 분석기: https://learn.microsoft.com/en-us/defender-endpoint/tune-performance-defender-antivirus
- CISA KEV: https://www.cisa.gov/known-exploited-vulnerabilities-catalog
- Anthropic 에이전트 프레임워크: https://www.anthropic.com/news/our-framework-for-developing-safe-and-trustworthy-agents
- 알약 2022: https://www.ajunews.com/view/20220831135040030
