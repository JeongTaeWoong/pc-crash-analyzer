# 국내 금융 보안 프로그램 — 논문·뉴스·정책·실측 자료 조사 (2026-11-10)

`security-impact-design.md` 의 근거 자료다. 앞선 정리는 `external-survey.md`(충돌·성능 사례, 7절 취약점)와 `related-work.md`(진단 도구·학술).

- 등급은 `conflict-test-plan.md` 4.1 기준이다: **A** 정부·벤더·은행 공식 / **B** 학술 / **C** 언론·독립 보안 연구 / **D** 커뮤니티. (`related-work.md` 는 A=학술로 등급 체계가 다르다 — 인용할 때 주의)
- ✓는 원문을 열어 확인한 것이다. ◎는 조사 결과를 받은 뒤 원문을 다시 열어 대조한 것이다.

## 0. 요약 — 세 가지

1. **측정한 자료가 없다.** 금융 보안 프로그램의 CPU·메모리·부팅·앱 시작·입력 지연을 통제 조건에서 잰 학술·공식·언론 자료를 국내외 어디서도 찾지 못했다. 느려짐 주장은 모두 일화(삼성 서비스 FAQ, 블로그, 수리업체)다. → 우리 측정이 처음일 가능성이 높다.
2. **문제는 2026년 11월에도 현재진행형이다.** 정부는 2025-10에 "설치 강요 보안 SW 단계적 제한('26년~)"을 발표했다. 그러나 2026-10 국정감사 자료로 은행 19곳 중 9곳(47.4%)은 아직 무설치 뱅킹이 없다. 공격도 계속됐다(AnySign4PC 2026-06 권고, CrossWeb EX "FlashHole" 2026-10).
3. **실험 설계에 직접 영향을 주는 사실이 있다.** ① 모듈은 은행 페이지에서만 깨어나고, 키보드 보안은 보호된 입력칸에 초점이 있을 때 동작한다. ② Chrome 142+·Edge 143+의 "로컬 네트워크 액세스" 권한 때문에 모듈 연결이 막힐 수 있다. ③ `SendInput` 은 커널 키보드 필터를 거치지 않는다. ④ 반복 횟수는 시범 측정의 "부팅 간·부팅 내 변동"으로 정해야 한다(Kalibera & Jones 2013).

## 1. 학술 (B)

| 자료 | 연도 | 내용 (수치는 원문 그대로) | 관점 |
|---|---|---|---|
| Yun et al., "Too Much of a Good Thing", USENIX Security ✓ | 2025 | 7개 제품, 취약점 19개. **제품명은 "한국 정부 기관의 요청으로" 익명화**(PRODUCT A~G: 인증서 관리·키보드 보안·방화벽·이상거래 탐지). 사용자당 평균 설치 "9.06"개(SD "6.96", 최대 24). 은행 17곳 중 "13"곳이 설치 필수라고 안내. 제거 후 루트 인증서 노출 "86.5%"(발표 슬라이드는 89.5%로 다름). **성능·크래시 측정 없음** | 취약점·보급 |
| Kim, Huh, Anderson, Oxford RR-10-01 ✓ | 2010/11 | 설문 401명: 느리다 "11.6%", 호환성 문제 "45.5%", 플러그인 설치로 시스템 크래시를 언급한 응답 25명. 2차 설문 261명: 설치 실패 경험 "83.7%". 로그인 트래픽: 한국 은행 "8,669 / 8,961" 패킷, "3,653,648 / 7,787,551" 바이트 vs 영국 은행 "1,301" 패킷, "801,297" 바이트 | 사용자 인식·네트워크 |
| 전정훈, 융합보안논문지 12(6) ✓ | 2012 | 은행별 설치 제품 표. 안정성 사례 6가지: 보안 프로그램이 종료되지 않아 수동 종료도 안 됨, 메모리에서 지워지지 않음, 시스템 사용 불능. 외산 백신이 은행 모듈 설치 파일을 악성으로 탐지. **CPU 수치 없음** | 충돌(정성) |
| 이성훈 외, 정보보호학회논문지 25(4) | 2015 | JavaScript 변조로 키보드 암호화 모듈 우회(초록만 확인) | 취약점 |
| 박혜승 외, 한국인터넷정보학회논문지 18(4) ✓ | 2017 | 플러그인 설치 "불편", 정성 분석, 측정 없음 | 정책 |
| 이정호 외(KAIST), 정보보호학회논문지 33(5) | 2023 | **백신 대상**(금융 보안 아님). 성능 평가 기준: CPU 증가 0.2% 이내 양호, 1% 보통, 메모리 20%/40% — **측정 기준 참고용** | 방법 |
| Kalibera & Jones, ISMM ✓ | 2013 | 시범 측정으로 단계별 분산을 구해 단계별 반복 수를 정한다. 신뢰구간은 최상위 단계(우리는 **부팅**)의 평균들로 계산 | 방법 |
| Georges et al., OOPSLA ✓ | 2007 | 시작 시간은 프로세스를 여러 번 새로 띄워 잰다. 30회 미만이면 t 분포. 신뢰구간이 평균의 1~2% 안이 되면 멈춘다 | 방법 |
| Mytkowicz et al., ASPLOS ✓ | 2009 | 환경 편향으로 "7% 느려짐"이 실제로는 "8% 빨라짐"일 수 있다 → 환경 무작위화 | 방법 |

못 찾은 것: KSA 성능 수치를 다룬 국내 학위논문·KISA·금융보안원 보고서, 한국소비자원 조사, USENIX 2025 제품명 공개 자료. 다만 PRODUCT D의 "암호화 끄기 API"는 Palant의 TouchEn nxKey 글을 인용하고 있어 nxKey로 **추정**된다(저자 확인 아님).

## 2. 정책·사건 연표 (요약)

| 날짜 | 내용 | 등급 |
|---|---|---|
| 2014-03 | 규제개혁 끝장토론 "천송이 코트" — 액티브X 문제 공론화 | C ✓ |
| 2015-02-03 | 전자금융감독규정 개정 — 보안 프로그램 설치 의무 조항 삭제 | C ✓ |
| 2019-12-30 | 민간 500대 사이트 플러그인 "2,266 → 408 (82.0%)" 감소. 그중 EXE 1,456→265 | A ✓ |
| 2020-05-20 | 전자서명법 전부개정 통과(공인인증서 독점 폐지) | A ✓ |
| 2020-11 | 라자루스가 Veraport를 쓰는 사이트를 통해 악성코드 배포(ESET) | C ✓ |
| 2023-03~11 | INISAFE CrossWeb EX ≤3.3.2.40 권고, 국정원 "60여 곳 PC 210여 대". MagicLine4NX 악용 "50여 기관", 백신이 구버전 자동 삭제 | A/C ✓ |
| 2024-01-10 | 삼성전자서비스 FAQ: 금융 보안 SW 충돌로 PC가 "현저히 느려지거나 멈춤", 제거 안내 | **A** ✓ |
| 2025-09-24 | KB국민은행: Chrome 141 로컬 네트워크 권한 창 — ASTx "로컬 통신" 때문 | A ✓ |
| **2025-10-22** | **범부처 정보보호 종합대책: "금융‧공공기관 등이 소비자에게 설치를 강요하는 보안 SW를 단계적으로 제한('26년~)"** | **A ◎** |
| 2025-11 | KISA 보안 취약점 클리닝 서비스 시범(MagicLine4NX 1.0.0.0~1.0.0.30 제거) | A ✓ |
| 2025-11~12 | 하나은행 FAQ: Chrome 142+·Edge 143+에서 로컬 네트워크 액세스 '차단' 시 보안 프로그램 설치 오류. 권한 허용 또는 `chrome://flags/#local-network-access-check` 비활성화 안내 | A ◎ |
| 2026-03-29 | 보안뉴스 단독: 정부가 금융권에 "4월까지 대체 계획안을 제출하고 연말까지 전면 전환" 통보(익명 취재원) | C ◎ |
| 2026-03-31 | SBS 자체 조사: 은행 18곳 중 무설치 뱅킹 "단 6곳", 지방은행은 많게는 5개 필수 | C ◎ |
| 2026-04-14 | 한겨레: 설치 필수 은행 — 하나·신한·농협·수협·부산·경남·전북·광주·제주 | C ✓ |
| 2026-06-01 | KISA: AnySign4PC 1.1.4.4~1.1.4.6 버퍼 오버플로·원격 코드 실행 → 1.1.5.0 | A ✓ |
| 2026-07-30 | KISA·국정원·경찰청·금융보안원 합동 권고: 워터링홀 + "보안소프트웨어 취약점" | A ✓ |
| 2026-10-21 | Kaspersky "Operation FlashHole"(라자루스): CrossWeb EX 악용, 국내 "at least three organizations", "Active since at least July 2025" | C ◎ |
| 2026-10-22 | 국정감사(손명수 의원 자료): 은행 19곳 중 17곳 설치 권장, 9곳(47.4%) 무설치 뱅킹 없음. AnySign 취약점 10건(2025-03~2026-08, KISA) | C ◎ |
| 2026-11-06 | KISA 클리닝 서비스: 점검 "8030만건", 탐지 "1231만건", 조치 "555만건" | C ✓ |

## 3. 실측 수치 — 찾은 것 전부

| 자료 | 등급 | 수치 |
|---|---|---|
| Palant, IPinside ✓ | C | 로컬 서버 응답 "each request taking half a second", 단일 스레드 |
| Kim·Huh·Anderson ✓ | B | 위 1절 네트워크 트래픽(한국 은행이 영국 은행보다 바이트 4.6~9.7배) |
| AV-TEST, Acronis 성능 보고서 ✓ | C | **일반 백신**(금융 보안 아님) 영향 — 웹사이트 로드 13.52%/16.45%, 앱 실행 3.81%/2.29%, 파일 복사 2.59%/1.84%(표준/고성능 PC). 우리가 잡아야 할 효과 크기의 참고치 |
| 블로그·나무위키 | D | "부팅 최대 30%", "CPU 100%" — 조건·근거 없음 |
| 이 PC 10/28 | 팀 | SignKoreaWD 등 — **논리 CPU 1개 기준이었음(11/10 정정)**. 전체 기준 약 0.8~1.4% |

## 4. 모듈 동작 방식 (설계 근거)

- **언제 깨어나나:** "KSA is intended to be active only on specific websites… employs JavaScript… (e.g., onfocusout event in DOM)"(Yun 2025 ✓). ASTx는 "sits idly in the background… only activates when you are on a banking website"(Palant ✓).
- **키보드 보안 경로:** nxKey는 "Device driver now intercepts all keyboard input"(Palant ✓), "device driver hook"(Yun ✓). TouchEn Key는 가상 OS·원격 세션을 지원하지 않는다(우리은행 안내 ✓) — 물리 장치 스택에서 동작한다는 뜻.
- **`SendInput` 경로:** 하드웨어 입력 큐, 즉 커널 드라이버 스택 **다음**에 들어간다(Microsoft 문서, Raymond Chen 블로그 ✓). 저수준 훅(`LLKHF_INJECTED`)에는 보이지만 kbdclass 필터 드라이버는 거치지 않는다. **→ Notepad++ 입력 지연은 일반 간섭(CPU 경쟁, 사용자 모드 훅)만 잰다. 키보드 보안의 암호화 경로는 재지 못한다.**
- **감시 목록 후보(커뮤니티 자료라 설치 전후 diff로 확정):**

| 제품 | 포트 | 이름 |
|---|---|---|
| ASTx | 55920 등 | StSess.exe |
| TouchEn nxKey | 127.0.0.1:34581 (34580~34583) | CKAgentNXE.exe, CrossEXService.exe |
| Veraport G3 | 16106(TLS), 16116 | veraport.exe, WizveraPMSvc |
| IPinside | 21300, 21400 | I3GMainSvc, I3GProc.exe |
| nProtect Online Security | 16100, 14440~14443 | nossvc.exe, npkcsvc 등 |
| AnySign4PC | 10531 | AnySign4PC.exe |

- Microsoft 미니필터 고도(altitude) 목록(A ✓)에 잉카인터넷(TkPcFtCb64.sys 등)·안랩(AszFltNt.sys)·마크애니(maProtector.sys) 드라이버가 등록돼 있다. 어느 제품 것인지는 미확인.

## 5. 측정 방법 기준 (업계)

| 출처 | 규칙 |
|---|---|
| AV-Comparatives ✓ | 최소 10회 반복, **매 실행 후 재부팅**, 앱 실행은 첫 회를 빼고 이후 점수만, 테스트 중 Windows Update 차단, 기준은 "보안 SW 없는 동일 PC" |
| AV-TEST ✓ | 각 동작 여러 번, 자동 업데이트·검사·백업을 테스트 전에 끔 |
| Microsoft Windows Assessment Toolkit ✓ | 부팅 측정 전 **6회 재부팅**으로 prefetch 학습, 기본 3회 반복, 부팅 후 "5초간 낮은 우선순위 CPU·저장장치 사용 누적" 시점부터 |
| Microsoft CLR 성능 블로그 ✓ | 마우스를 고정 위치(0,0)에, 창은 최대화. 잡음원: 백신, 백그라운드 서비스, CPU 클럭 변화 |
| Kalibera & Jones 2013 ✓ | 시범으로 부팅 간 분산 T²(부팅)과 부팅 내 분산 T²(런)을 구하고, 부팅당 런 수 r = ⌈√((부팅 비용/런 비용)·(T²런/T²부팅))⌉. 신뢰구간은 부팅 평균들로 |

## 6. 우리 프로젝트에 주는 의미

- **기여:** "금융 보안 프로그램의 성능 영향을 통제 실험으로 잰 첫 자료"라고 말할 근거가 있다(찾은 범위에서). 단 PC 1대라는 한계를 같이 말한다.
- **시의성:** 정부가 2026년 중 전환을 지시했으므로 **전환 전 기록**이 된다. 반대로 실험 도중 은행 페이지가 무설치로 바뀔 수 있다 → 런마다 모듈이 실제로 깨어났는지(프로세스·포트) 기록해 확인한다.
- **발표 문장 근거:** 삼성전자서비스 FAQ(A, "현저히 느려지거나 멈춤"), 범부처 대책(A), 국정감사 47.4%(C), USENIX 2025(B, 평균 9개·19개 취약점).
- **재현 후보 충돌 사례:** Chrome 142+ 로컬 네트워크 차단으로 모듈 연결 실패(A, 날짜 확인됨), nxKey 드라이버 크래시 → 시스템 전체 재부팅(Palant C).

## 출처

- USENIX 2025: https://www.usenix.org/conference/usenixsecurity25/presentation/yun
- Kim·Huh·Anderson: https://www.cs.ox.ac.uk/files/2916/RR-10-01.pdf
- 전정훈 2012: https://koreascience.kr/article/JAKO201206737754393.pub
- 이성훈 외 2015: https://koreascience.kr/article/JAKO201527358961995.page
- 박혜승 외 2017: https://koreascience.kr/article/JAKO201726163353577.pdf
- 이정호 외 2023: https://koreascience.kr/article/JAKO202331137857685.page
- Kalibera & Jones: https://kar.kent.ac.uk/33611/45/p63-kaliber.pdf
- Georges et al.: https://dri.es/files/oopsla07-georges.pdf
- 범부처 정보보호 종합대책: https://www.fsc.go.kr/no010101/85452
- 2019 플러그인 현황: https://www.fsc.go.kr/no010101/74070
- 삼성전자서비스 FAQ: https://www.samsungsvc.co.kr/solution/33921
- 하나은행 FAQ: https://biz.hanabank.com/cont/customer/customer04/customer047/customer0478/index.jsp
- 보안뉴스 2026-03-29: https://www.boannews.com/news/articleView.html?idxno=142897 (Daum: https://v.daum.net/v/20260329172355851)
- SBS 2026-04-10: https://news.sbs.co.kr/news/endPage.do?news_id=N1008512158
- 한겨레 2026-04-14: https://v.daum.net/v/20260414154127678
- 딜사이트 2026-10-22: https://dealsite.co.kr/articles/171164
- Kaspersky FlashHole: https://www.kaspersky.com/about/press-releases/kaspersky-identifies-new-lazarus-apt-campaign-utilizing-ai-and-post-quantum-encryption
- Palant: https://palant.info/2023/01/09/touchen-nxkey-the-keylogging-anti-keylogger-solution/ · https://palant.info/2023/01/25/ipinside-koreas-mandatory-spyware/
- AV-Comparatives: https://www.av-comparatives.org/performance-test-methodology/
- Microsoft 미니필터 고도 목록: https://learn.microsoft.com/windows-hardware/drivers/ifs/allocated-altitudes
