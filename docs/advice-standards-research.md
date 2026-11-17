# 권고의 기준 — 조사 (2026-11-16)

AI 의 권고를 채점하는 채점표에서, 1차 사람 채점(정태웅)과 AI 채점자가 갈린 쟁점 다섯 가지에 대해 업체 문서와 문제 해결 방법론이 무엇이라고 하는지 조사한 것이다. 채점표를 이 조사만으로 고치지 않는다 — 문제지 1부(설문)의 팀 의견과 함께 본다.

> **문제지를 아직 풀지 않은 팀원은 1부 설문에 답한 뒤에 읽는다.** 먼저 읽으면 답이 이쪽으로 끌린다.

- 인용은 원문 그대로이고, †표시는 사이트가 내려받기를 막아 요약 도구를 거쳐 확인한 것이다.
- "함의"는 조사한 쪽(Claude)의 추론이다. 출처의 말이 아니다.
- 찾지 못한 것은 찾지 못했다고 적었다.

## 1. 증상도 문제도 없는 PC 에 무엇이라고 답하나

| 출처 | 내용 |
|---|---|
| [Apple Diagnostics](https://support.apple.com/en-us/102334) | 깨끗한 결과는 한 줄이고 뒤따르는 할 일이 없다: "ADP000 \| No issues found." |
| [Windows 보안 — 장치 상태](https://support.microsoft.com/en-us/windows/device-performance-and-health-in-the-windows-security-app-59d8499d-b6fd-6930-7667-ebf8ae10e08d) | "A green check means everything is fine" |
| [Windows 보안](https://support.microsoft.com/en-us/windows/stay-protected-with-windows-security-2ae0363d-0ada-c064-8b56-6a39afb6a963) | 추가 안내는 사용자가 여전히 걱정될 때만: "If the scan doesn't find any issues, but you're still concerned, you may want to check your device more thoroughly." |
| [Windows UX 지침 — 경고](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-warn) | "Limit warnings to conditions that involve risk and are immediately relevant, actionable, not obvious, and infrequent." |
| [AHRQ — 경보 피로](https://psnet.ahrq.gov/primer/alert-fatigue) | 안전을 위한 경보가 늘수록 오히려 해가 될 수 있다 |

- 찾지 못한 것: Windows 메모리 진단·문제 해결사의 "문제 없음" 공식 문구. "나중에 기록을 확인하라"는 안내를 직접 다룬 출처.
- **함의**: 업체 도구는 깨끗한 결과를 그대로 말하고 끝낸다. 근거가 가장 많은 기준은 "확인한 범위에서 문제를 찾지 못했다" + 조치 없음 + 많아야 조건부 한 줄("다시 생기면 ~"). 조건 없는 "지켜보세요"는 위 UX 지침의 "지금 관련 있고 할 수 있는 일" 기준에 맞지 않는다.

## 2. 어떤 조치에 주의·되돌리는 방법이 필요한가

| 조치 | 업체 문서의 관행 | 출처 |
|---|---|---|
| 레지스트리 편집 | 정식 경고 상자: "Serious problems might occur if you modify the registry incorrectly" | [Microsoft Learn](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/windows-registry-advanced-users) |
| 부품 수리·교체 | 정식 주의 상자(감전·손상 위험) | [Surface 수리 안내](https://learn.microsoft.com/en-us/surface/surface-service-and-repair) |
| 드라이버 되돌리기 | 경고는 없고 "관리자 권한 필요"만. 되돌리는 길은 시스템 복원 | [드라이버 안내](https://support.microsoft.com/en-us/help/4028443), [시스템 복원](https://support.microsoft.com/en-us/windows/system-restore-a5ae3ed9-07c4-fd56-45ee-096777ecd14e) |
| 보안 설정 끄기 | 결과를 한 문장으로 알림: "While real-time protection is off, files you open or download won't be scanned for threats." | Windows 보안 |
| 프로그램 제거 | 경고 없음. "먼저 복구를 시도해 볼 수 있다" | [제거 안내](https://support.microsoft.com/en-us/windows/uninstall-or-remove-apps-and-programs-in-windows-4b55f974-2cc6-2d2b-d092-5905080eaf98) |
| 프로그램 업데이트 | 경고 없음. 자동 업데이트가 기본 | [앱 자동 업데이트](https://support.microsoft.com/en-us/windows/turn-on-automatic-app-updates-70634d32-4657-dc76-632b-66048978e51b) |

- 일반 원칙: 확인을 받는 것은 "결과가 크거나 쉽게 되돌릴 수 없을 때"([Windows UX 지침](https://learn.microsoft.com/en-us/windows/win32/uxguide/mess-confirm)), "일상적인 동작에는 확인 창을 쓰지 말라"([NN/g](https://www.nngroup.com/articles/confirmation-dialog/)). Microsoft 문서 작성 지침은 주의 상자를 남발하지 말라고 한다 — 읽는 사람이 건너뛴다([Learn 작성 지침](https://learn.microsoft.com/en-us/contribute/content/markdown-reference)).
- 찾지 못한 것: 드라이버를 바꾸기 전에 복원 지점을 만들라는 Microsoft 페이지. 프로그램 제거 때 데이터가 사라질 수 있다는 안내. 서비스 변경에 관한 안내.
- **함의**: 단계가 있다. 업데이트는 주의가 없어도 된다. 재설치·제거는 데이터나 설정이 사라질 때만 한 줄. 드라이버와 보안 관련 설정은 위험 경고가 아니라 "되돌리는 방법". 레지스트리와 부품은 분명한 경고. 일상적인 조치마다 주의 문구를 붙이는 것은 오히려 감점 사유가 될 수 있다.

## 3. 사용자가 직접 돌린 무거운 작업 때문에 느릴 때

| 출처 | 내용 |
|---|---|
| [Apple — Mac 이 느릴 때](https://support.apple.com/guide/mac-help/if-your-mac-runs-slowly-mchlp1731/mac) | 고장이 아니라 용량의 문제로 설명: "An app you're using may require more memory than your Mac has easily available." "Quit any apps you're not using." |
| [Microsoft — PC 성능 팁](https://support.microsoft.com/en-us/windows/tips-to-improve-pc-performance-in-windows-b3b3ef5b-5953-fb6a-2528-4bbed82fba96) | "Close any apps or tabs you don't need." |
| [작업 관리자 효율 모드](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/) | 사용자가 고르는 선택지: 그 프로세스의 우선순위를 낮춘다 |
| [Apple — 앱 강제 종료](https://support.apple.com/en-us/102586) | 끝내기 전에: "before you decide to force an app to quit, try to quit the app normally" (저장할 기회) |
| [Windows 종료 동작](https://learn.microsoft.com/en-us/windows/win32/shutdown/shutdown-changes-for-windows-vista) | 앱이 종료를 막으면 시스템이 끝내지 않고 사용자에게 묻는다 |
| [사람-AI 상호작용 지침 G10 (Microsoft Research)](https://www.microsoft.com/en-us/research/uploads/prod/2019/01/Guidelines-for-Human-AI-Interaction-camera-ready.pdf) | 사용자의 목적이 불확실하면 되묻거나 기능을 낮춘다 |

- 찾지 못한 것: "계속 / 멈춤 / 다른 것 정리"라는 세 선택지를 그대로 제시한 출처, "그냥 기다리라"고 한 출처. 선택지 방식은 위 출처들에서 끌어낸 추론이다.
- **함의**: 업체들은 이것을 정상 부하로 보고 결정을 사용자에게 맡긴다. 선택지를 주는 것은 모든 출처와 어긋나지 않고, 사용자 대신 작업을 끝내는 것을 지지하는 출처는 없다. 멈춤·강제 종료를 말할 때는 "먼저 저장"이 따라야 한다. — 정태웅의 채점 메모("꼭 필요한 작업인지 묻고, 확인을 받은 뒤 다른 프로그램 정리", "진행할지 지켜볼지 선택지")와 같은 방향이다.

## 4. "혹시 모르니" 하는 권고

| 출처 | 내용 |
|---|---|
| [CompTIA A+ 문제 해결 순서](https://lecbyo.files.cmp.optimizely.com/download/34be017cb73211ef8985a6f347fbf652) | 가설을 세우고 → 시험하고 → 그다음에 조치 |
| [Cisco 문제 해결 모델](https://www.cisco.com/en/US/docs/internetworking/troubleshooting/guide/tr1901.html)† | "Begin with the most likely problem, and devise a plan in which only one variable is manipulated." |
| [Google SRE — Effective Troubleshooting](https://sre.google/sre-book/effective-troubleshooting/) | 가능성이 높은 순서로, 시험이 시스템에 줄 위험을 따지며 |
| [Intel](https://www.intel.com/content/www/us/en/support/articles/000094269.html)† | 시스템이 정상이면 드라이버를 업데이트하지 않아도 된다 |
| [Dell](https://www.dell.com/support/kbdoc/en-us/000123347/drivers-and-downloads-faqs)† | 잘 돌고 있고 긴급·권장 표시가 없으면 바로 업데이트하지 않아도 된다 |

- 엇갈리는 것: 업체는 최신 상태도 권한다(Microsoft: 드라이버는 Windows 업데이트로 자동으로 받는 것이 가장 좋다, Dell†: 중요 업데이트는 바로 설치). "잘 도는 드라이버는 절대 건드리지 말라"는 업체의 입장이 아니다. 메모리 검사·런타임 복구를 직접 다룬 출처는 찾지 못했다.
- **함의**: 방법론은 한목소리다 — 가장 그럴듯한 원인부터, 한 번에 하나만 바꾸고, 확인한다. 근거가 가리키는 조치 옆에 관계없는 변경을 나란히 권하면 무엇이 효과였는지 알 수 없게 된다. 다른 조치는 "그래도 안 되면"이라는 조건과 이유를 단 다음 단계로만 받아들일 수 있다.

## 5. 사람 여러 명으로 AI 채점자를 검증하는 방법

| 항목 | 출처가 말하는 것 |
|---|---|
| 채점자 수·문항 수 | 사람 3명 이상, 50~100문항(최소 30) — [Alt-test, ACL 2025](https://arxiv.org/abs/2501.10970). 미리 정한 글로 된 기준에 따라 서로 독립으로 — [Artstein & Poesio](https://aclanthology.org/J08-4004/) |
| 통계 | 단순 일치율과 우연을 보정한 값(카파)을 함께 — [McHugh](https://pmc.ncbi.nlm.nih.gov/articles/PMC3900052/). 0~2점처럼 순서가 있는 항목과 3명 이상에는 Krippendorff 알파. 한쪽 답이 대부분이면 일치도 값이 낮게 나온다 |
| 기준선 | 관례이고 서로 다르다: 카파 0.60 미만은 부족(McHugh), 알파 0.8 또는 차선으로 0.67(Krippendorff) |
| 불일치 처리 | 토론으로 합의 — [HumEval 2021](https://aclanthology.org/2021.humeval-1.15/). 반론: 사람마다 다르게 보는 것 자체가 정보다 — [Plank 2022](https://arxiv.org/abs/2211.02570) |
| 맞춰 보기 | 본 채점 전에 작은 묶음으로 기준을 다듬는다 — [arXiv:1602.05753](https://arxiv.org/abs/1602.05753). "기준이 있어야 채점하지만, 채점해 봐야 기준이 정해진다" — [Shankar 2024](https://arxiv.org/abs/2404.12272) |
| AI 채점자 판정 | 사람-AI 일치가 사람-사람 일치 범위 안에 드는가 — [Zheng 2023](https://arxiv.org/abs/2306.05685), [Thakur 2024](https://arxiv.org/abs/2406.12624) |
| 모델 제공사 | Anthropic·OpenAI·Google 모두 사람 채점으로 맞추라고 하지만 사람 수·문항 수·기준선은 주지 않는다 |

- **함의**: 채점자 한 명으로는 AI 채점자를 검증할 수 없다(정태웅의 판단과 같다). 지킬 수 있는 최소선은 — 글로 된 기준 + 독립된 3명 + 맞춰 보기 1~2회 → 기준 고정 → 50~100문항. 항목별로 일치율과 보정 값, 그리고 혼동표를 함께 적는다. 목표는 미리 정한다(바닥 0.6, 목표 0.8). 갈린 것은 토론으로 정리하고, 끝내 갈리는 것은 "기준이 모호한 곳"으로 기록한다.

## 이 조사가 우리 계획에 뜻하는 것 (제안 — 팀 의견을 받은 뒤 정한다)

1. 지금까지의 사람 채점은 맞춰 보기 1회차다. 문제지(1부 설문 + 2부 같은 30문항)로 세 사람의 답을 모으면 맞춰 보기가 끝나고, 그 뒤 채점표를 고쳐 고정한다.
2. 보고서에 쓸 일치율은 고정한 채점표로 시험용 답 50문항 이상을 세 사람이 채점해서 낸다(지금 설계의 "30문항·1명"보다 많다 — 한 사람당 약 1시간).
3. 채점표 후보 수정: 주의 안내는 조치 종류별 단계로(2절), 문제 없는 PC 는 조건부 한 줄까지 만점(1절), 관계없는 조치는 조건부 다음 단계일 때만 허용(4절), 사용자 작업은 선택지 제시를 기준으로(3절).
