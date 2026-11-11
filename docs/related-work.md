# 유사 연구·도구 재조사 (2026-10-29)

중간발표 4쪽("왜 기존 도구로는 안 되는가")의 비교를 넓히고, 10/28 조사(`post-midterm-plan.md` 5절)에 없던 것을 더했다.
등급: A 학술(동료 심사) · B 공식 문서·논문 사전 공개 · C 오픈소스 저장소 · D 보도·커뮤니티. 숫자는 출처에 적힌 것만.

## 1. 가장 가까운 것 — 직접 비교 대상

| 이름 | 종류 | 무엇을 하나 | 우리와 다른 점 | 등급·출처 |
|---|---|---|---|---|
| **usausa/pc-agent** | 오픈소스 (.NET 10, C#, MIT) | Windows PC 정보 수집(CPU·GPU·메모리·디스크·SMART·배터리·Wi-Fi·이벤트 로그·시스템) → **외부 JSON 규칙·임계값으로 결정적 진단** → LLM 채팅·RAG → **승인 후 수리 조치**(임시 파일·bin/obj 삭제, 허용 목록 셸) | 구조가 우리와 매우 비슷하다(규칙 핵심 + LLM 보조 + 승인 게이트). 차이: ① **상시 블랙박스 수집·링버퍼·크래시 순간 덤프가 없다**(실행 시점 스냅샷만) ② 시간·공존 상관 추론이 없다(임계값 규칙) ③ 조치가 정리 수준(Lv1)이고 위험도 단계가 없다 ④ 느려짐 추세·한국 보안모듈 없음. 별 0, README 일본어 — 참고 설계로만. | C — https://github.com/usausa/pc-agent |
| **mughalh/EventLogAnalyzer** | 오픈소스 GUI | .evtx 파일을 열어 BugCheck(1001)·예기치 않은 종료·디스크 오류를 한 번에 필터, CSV 내보내기 | 뷰어·필터이지 추론이 아니다. 1000(앱 크래시)보다 1001 중심. | C — https://github.com/mughalh/EventLogAnalyzer |
| **ManageEngine EventLog Analyzer** | 상용 | 1000과 1001을 자동 짝지어 오류 모듈을 보여줌 | 서버 관리자용 로그 서버. 원인 후보·조치 없음 | D — https://www.manageengine.com/products/eventlog/kb/event-1000-application-error-help.html |
| **Dynatrace SuperDump** | 오픈소스 서비스 | 덤프 파일을 REST로 받아 WinDbg 명령을 자동 실행·요약 | 서버 측 덤프 분석기. 우리의 "미니덤프 보강" 후보 도구(10/28 mcp-windbg·udmp-parser와 같은 자리) | C — https://github.com/Dynatrace/superdump |

결론: **"상시 수집한 증거로 시간·공존 상관을 계산해 후보를 내고, 위험도 단계로 실행을 가르는"** 조합은 조사 범위에서 찾지 못했다. 가장 가까운 pc-agent 는 규칙+LLM+승인은 있지만 증거 수집이 일회성이다.

## 2. 학술 — 설계 근거로 쓸 것

| 논문 | 핵심 | 우리 설계에 주는 것 |
|---|---|---|
| Wang et al., **PeerPressure** (OSDI 2004) | 아픈 PC의 레지스트리 설정을 다른 PC 들의 통계와 비교해 잘못된 설정을 순위화(경험적 베이즈). 20건 중 12건 원인 특정, 나머지는 후보를 3자릿수 줄임 | "정상 기준"을 사람이 고르지 않아도 되는 **또래 비교**. 지인 PC 데이터(L3)가 모이면 `baseline_diff` 를 또래 통계로 대체할 수 있다 — A https://www.usenix.org/legacyurl/automatic-misconfiguration-troubleshooting-peerpressure |
| Wang et al., **Strider** (LISA 2003) | 정상/비정상 상태 diff + 변경 빈도 역수(Inverse Change Frequency)로 노이즈 제거 | 우리가 10/29 넣은 **autoupdate_noise**(자주 바뀌는 항목의 가중 하향)와 같은 아이디어의 원조 — A |
| Glerum et al., **Debugging in the (Very) Large** (SOSP 2009) | WER 10년: 10억 대의 오류 보고를 **버킷팅**해 우선순위화, 통계 기반 디버깅 | 1000/1001 의 오류 모듈·예외 코드가 "버킷 키"라는 근거. 우리 `significance` 의 재발 카운트는 소형 버킷팅 — A https://www.microsoft.com/en-us/research/?p=157688 |
| Dang et al., **ReBucket** (ICSE 2012) | 호출 스택 유사도로 크래시 버킷팅 | 미니덤프 보강 시 스택 기반 묶기 — A (10/28 조사) |
| Basu et al., **Why Did My PC Suddenly Slow Down?** (SysML 2007) | 느려짐 원인을 시스템 지표로 진단 | `slow` 증상의 선행 연구 — A (10/28 조사) |
| Machida et al., **Mann-Kendall for software aging** (IM 2013) | Mann-Kendall 추세 검정은 **거짓 양성이 많고**, 데이터가 많아야 줄지만 탐지가 느려짐 | 롤업 기울기 하나로 "누수"를 단정하지 않는 이유. 우리는 기울기 + R² + 15분/60분 이중 창 + **제안만**(자동 덤프 아님) — A https://aip.ifi.uni-heidelberg.de/fileadmin/papers/2013/IM_2013_-_Langner__Andrzejak.pdf (같은 그룹) |
| Matias et al., **Software Aging Detection Based on Differential Analysis** (WoSAR 2016) | 누수 탐지엔 RSS(작업 집합)보다 **힙 사용량**이 낫고 CUSUM·EWMA 가 효과적 | 우리가 Working Set 이 아니라 **Private Bytes** 를 기울기 원천으로 쓴 선택의 근거. CUSUM 은 다음 후보 — A https://aip.ifi.uni-heidelberg.de/fileadmin/papers/2016/Software_Aging_Detection_Based_on_Differential_Analysis_WoSAR_2016.pdf |
| Dorner & Klausner, **If It Looks Like a Rootkit…** (arXiv 2024) | 커널 안티치트 4종을 루트킷 기준으로 평가: FACEIT·Vanguard 는 루트킷 유사, BattlEye·EAC 는 경미 | 안티치트가 **커널에서 무엇을 하는지**의 학술 근거(M2 메커니즘). 충돌 측정은 아님 — B https://arxiv.org/abs/2408.00500 |
| Wang et al., **RCAgent** (2023) / **OpenRCA** (ICLR 2025) | 도구 증강 LLM 에이전트의 클라우드 원인 분석. OpenRCA 벤치 335건에서 최고 모델도 **11.34%** 만 해결 | "핵심 판단은 알고리즘, Agent 는 Lv3 설명·계획"이라는 경계(PPT 7·10쪽)의 외부 근거 — A https://arxiv.org/abs/2310.16340 , https://iclr.cc/virtual/2025/poster/32093 |

## 3. 10/28 조사에서 유지되는 것

EnSilo 후킹 연구(2016, M1 메커니즘), osquery `windows_crashes` 테이블(수집 스키마 참고), mcp-windbg·udmp-parser(미니덤프 보강), LibreHardwareMonitor(센서, 사용 중). 벤더 공식 "알려진 충돌"은 `docs/conflict-test-plan.md` 출처 [1]~[12].

## 4. 찾지 못한 것 (정직하게)

- 안티치트↔백신, 금융보안 모듈↔게임의 **충돌률을 측정한** 학술·공식 자료 — 없음. 이 공백이 우리 L2·L3 데이터의 가치.
- Windows 데스크톱 프로세스의 메모리 누수 추세를 Private Bytes 로 탐지한 논문 — 없음(서버·클라우드 대상뿐).
- 2025~2026 에 널리 쓰이는 "Windows PC 수리 에이전트" — 초기 저장소(pc-agent, pc-support-agent)뿐.

## 5. 발표·보고서에 반영할 문장 (초안)

- "규칙 기반 진단 + LLM 보조 + 승인 게이트" 구조는 오픈소스(pc-agent)에도 등장하지만, **상시 블랙박스 수집과 시간·공존 상관 추론**을 결합한 것은 조사 범위에서 우리뿐이다.
- 느려짐 판정에 기울기 하나를 쓰지 않는 이유는 Mann-Kendall 거짓 양성(Machida 2013)이고, Private Bytes 를 쓰는 이유는 힙 지표가 RSS 보다 낫다는 결과(Matias 2016)다.
- LLM 단독 원인 분석의 한계(OpenRCA 11%)가 "알고리즘 Lv0~2 / Agent Lv3~4" 경계의 근거다.
