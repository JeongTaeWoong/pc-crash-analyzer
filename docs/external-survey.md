# 외부 사례 조사 — 핵심 충돌·성능 저하 (2026-11-10)

공개 자료에서 찾은 **프로그램 간 충돌**과 **PC 성능 저하** 원인을 근거 강도순으로 정리했다. 원본은 `engine/data/external/_reports/*.json`, 표의 ID는 각 항목의 `id`다.

- 등급은 `conflict-test-plan.md` 4.1 기준: **A** 벤더 공식 / **C** 전문 매체 / **D** 커뮤니티. B(학술)는 해당 없음.
- ✓ 는 조사 요약이 아니라 원문을 직접 열어 대조한 항목이다. 나머지는 웹페이지 추출 결과이므로 인용 전에 원문을 확인한다.
- 모두 **보고된 사례**다. 팀의 측정값이 아니며 3.4 지표에 넣지 않는다.

## 1. 핵심 충돌

| # | 충돌 | 내용 | 등급 | ID |
|---|---|---|---|---|
| 1 | Easy Anti-Cheat × Windows 11 24H2 | EAC 게임 실행 시 예기치 않은 재시작. Microsoft 긴급 업데이트 KB5063060 | A ✓ | sec-001~003 |
| 2 | RGB 장치 드라이버 inpoutx64 × 특정 게임 | 게임 실행 시 문제. Microsoft가 드라이버 차단으로 해결. 안티치트 관련 여부는 문서에 없음 | A ✓ | sec-004 |
| 3 | WSL/Docker × VPN·보안 제품 | Microsoft 문서에 Bitdefender 26.0.2.1, OpenVPN 2.6.501 등을 비호환으로 명시. Cisco AnyConnect 경로 문제 | A ✓ | dk-02, dk-01 |
| 4 | 한글 × 보안·DRM·DLP | KERNELBASE.dll 오류(보안 프로그램의 파일 차단), c0000005(문서 보안 개입). HwpPlugin.dll 항목은 보안 프로그램과 연결되어 있지 않다 | A ✓ | hw-01 |
| 5 | TouchEn nxKey × 매크로 게이밍 키보드·OfficeKeeper·Kaspersky | 뱅킹 사이트 입력 지원 제외·변조 오류. **게임 내 입력 언급은 없다** | A ✓ | sec-024 |
| 6 | AhnLab Safe Transaction × 알약·McAfee·360·Fasoo, N키 롤오버 입력 오류 | 은행 안내 페이지 | A | sec-026 |
| 7 | Riot Vanguard × 비호환 드라이버·HVCI | "요건 미달 드라이버" 안내. ene.sys·rtcore64.sys 같은 구체 이름은 D등급뿐 | A / D | sec-005, 019, 011 |
| 8 | Vanguard × 다른 보안 프로그램(Fasoo) | Riot VAN-83 | A | sec-006 |
| 9 | BattlEye × 하드웨어 강제 스택 보호, MSI Dragon Center | BattlEye FAQ | A | sec-007, 008 |
| 10 | GameGuard × 백신·보안 프로그램, HackShield 게임 | 오류 114·500 | A | sec-009, 010 |
| 11 | Unity × 오버레이(Discord·Steam·Afterburner), GPU 드라이버 DLL | Unity 문서, nvwgf2umx 크래시를 제3자 문제로 종결 | A | un-01, un-02 |
| 12 | Premiere 24.2 × 일부 NVIDIA GPU | Adobe가 자사 코드 문제로 인정, 배포 중단 | A | pr-01 |
| 13 | Blender × Intel 3~5세대 내장 GPU 드라이버 | 공식 매뉴얼의 문제·정상 드라이버 빌드 목록 | A | bl-01 |
| 14 | RTSS 후킹 × Windows Terminal·Unreal | RTSSHooks64.dll 크래시 ✓ / Unreal 약 120→20 FPS | D | ext-015, ue-03, ue-04 |
| 15 | Photoshop 실행 크래시 OpenCLOn12.dll 0xc0000409 | NVIDIA·Intel·AMD 모두 보고. 원인 미확정 | D | ps-01, ps-02 |
| 주의 | vgk.sys 블루스크린(13·14세대 Intel) | Riot의 해결책은 BIOS·CPU 쪽이다. 안티치트 충돌로 오판하기 쉬운 사례 | A | sec-020 |

## 2. 성능 저하

| # | 원인 | 내용 | 등급 | ID |
|---|---|---|---|---|
| 1 | 백신 실시간 검사 × 빌드·쿡·에뮬레이터 | Epic, CloudBees ✓(Windows 명시 없음, 수치 없음), Android, Microsoft가 제외 설정 권고. 수치는 Microsoft 1건. Unity 포럼 Defender 20~25% CPU(D) | A / D | ue-02, jk-01, as-01, vs-01, vs-03, un-04 |
| 2 | 메모리 증가 | Jenkins Support Core 힙 95%+(A), Premiere 25.4 64GB 소진(D, 담당자 "known issue"), WSL Vmmem 32GB 중 29GB+(D) | A / D | jk-02, pr-04, dk-03 |
| 3 | 확장·애드인 | VS·VS Code 공식 안내(A), Copilot Chat 50%+ CPU(D) | A / D | vs-02, vc-01, vc-02 |
| 4 | Discord가 NVIDIA 메모리 클럭 200MHz 낮춤 | 현재 수정 여부 미확인 | C | dc-01 |
| 5 | 금융 보안 상주 프로그램 | 미사용 시에도 자원 점유, 업계 관계자 인정(C, 수치 없음). AnySign4PC 약 90% CPU·FPS 150→20~30(약 5초), nProtect 99%(10초 이상), ASTx 100%는 모두 D등급 | C / D | sec-023, 034~037, 030 |
| 6 | SGuard64.exe 디스크 읽기 | 3~4MB/s, 30분 2.5GB 이상 | D | sec-021 |

**금융 보안 모듈의 CPU 수치를 제시한 A·B 출처는 없다.** 팀의 10/28 수치(유휴 13~22%, `post-midterm-plan.md` 2절)는 **11/10 정정: 논리 CPU 1개 기준**이다. 같은 줄의 값을 더하면 181%라 PC 전체 기준일 수 없다. 16스레드 PC 전체로는 각각 약 0.8~1.4%, 세 프로세스 합 약 3.6%다. 또 셋 중 금융 보안은 SignKoreaWD뿐이다(SKBspmtmon은 SK브로드밴드 속도 측정, Goji는 정체 미확인). 11/10 이후 측정(`perf-measure.ps1`, 수집기)은 전체 기준으로 나눈다.

## 3. 엔진 규칙(`rules.json`)에 없는 이름

조사에서 실제로 나온 이름 중 규칙에 없는 것: `bedaisy` `xhunter` `ace-base` `sguard` `fasoo` `inpoutx64` `ene.sys` `rtcore` `denuvo` `faceit` `hackshield` `magicline` `inisafe` `aswnethub` `dlkmd` `displaylink` `rtsshooks` `klif` `portmaster` `rzdev` `openclon12` `nvwgf2um`

제품 이름은 규칙에 있어도 모듈 이름이 없으면 연결되지 않는다(예: avast는 있으나 `aswNetHub.sys` 없음, kaspersky는 있으나 `klif.sys` 없음).

## 4. 외부 사례로 본 엔진 (탐색용, 지표 아님)

라벨이 있는 18건(작성자 확인 12, 벤더 6): 층 판정 50%, 원인 1순위 17%, 3순위 안 22%. 팀 자체 시나리오의 100%·92%와 큰 차이가 난다. 다만 글에서 얻을 수 있는 필드가 적어 불리한 조건이고, 표본도 작다. 위 3절의 이름 누락이 원인의 상당 부분을 차지한다.

## 5. 재현 결과 (실측, 비관리자 콘솔 수집기)

| 대상 | 결과 |
|---|---|
| AutoHotkey 2.0.29 `a & b::AltTabMenu` | **3/3 재현.** 종료 코드 0xC000041D, 이벤트 1000·1001, 덤프 3건(`cases/repro/`) |
| AutoHotkey 2.0.20 `StrGet` | 크래시 없음. UTF-16은 정상 종료, UTF-8 경로는 40초 이상 종료되지 않음 |
| CPython 3.13.5 `os.execve` | 크래시 없음(3회 정상 종료). 원 보고도 재현이 불규칙하다고 함 |

재현 스크립트: `collector/experiments/repro/`.

재현 추가(11/10): Godot 4.3 SVG 임포트 3/3(0xC0000005), Notepad++ 8.6.7 Column Editor 3/3(0xC000041D). 덤프 합계 9건.

## 6. 측정 자료 (`_reports/perf-measurements.json`)

- **안티치트:** FPS·CPU·메모리·부팅 시간을 측정한 A·B·C 자료를 찾지 못했다. 88편 PRISMA 리뷰(arXiv 2512.21377)도 벤치마크 부재를 지적한다. 로드 시점과 충돌은 arXiv 2408.00500 ✓: Vanguard·FACEIT 부팅 시, EAC·BattlEye 게임 시작 시, BEDaisy.sys 실행 중이면 EAC 게임이 시작되지 않음(측정값 없음).
- **HVCI:** ComputerBase 2023 ✓(5800X, RX 7900 XTX, 720p, 14개 게임) 평균 151.0→142.0 FPS, 1% 저점 100.7→92.9, Diablo II Resurrected 1% 저점 227.2→168.8. ComputerBase 2024(9700X, RTX 4090, 720p, 7개) 24H2 133→128, 23H2 127→121. XDA 2026(7600X, 4070 Ti S) 평균 1.7~2.5%.
- **금융 보안 모듈:** A·B의 CPU·메모리 측정 없음. USENIX 2025의 "PC당 평균 9개"는 보급 수치다. 키보드 필터 순서·복수 설치 충돌을 측정한 자료도 찾지 못했다.
- **측정 방법 참고:** AV-Comparatives(10회 이상, 매번 재부팅, 첫 실행 분리), AV-TEST(파일 복사 기준 대비). 우리 측정은 3회 중앙값이라 이보다 약하다.

## 7. 공개 취약점 — 받은 설치본 버전 대조 (11/10 오전)

크래시 판정과는 별개다. 다만 엔진 조치 Lv3(보안 모듈 업데이트·제거)를 안내할 근거가 된다. "설치본 버전"은 설치 파일의 버전이라, 실제 모듈 버전은 설치 뒤 `conflict-evidence.ps1` 의 `binaries` 로 다시 확인한다. ✓는 원문을 직접 연 것.

| 받은 것 | 공개 권고 (영향 → 수정) | 판정 | 등급 | 출처 |
|---|---|---|---|---|
| TouchEn nxKey 1.0.0.94 | ≤1.0.0.78 → 1.0.0.82 (2023) · ≤1.0.0.87 임의 DLL 인젝션 → 1.0.0.88 (2024) · ≤1.0.0.89 버퍼 오버플로·원격 코드 실행 → 1.0.0.90 (2025-03) | 세 건 모두 수정 이후 버전 | A ✓ | ASEC 82372, 87004 |
| Veraport G3 3.8.6.9 | 2020 Lazarus가 Veraport를 쓰는 사이트를 통해 서명된 악성코드 배포(서명 존재만 확인) · ≤3.8.6.4 직접 공격 가능, 3.8.6.5 수정. MD5·1024비트 루트 인증서 등 설계 문제는 남음 | 수정 이후 버전. 설계 문제는 설치 후 루트 인증서·포트로 확인 | C ✓(Palant) | ESET 2020, Palant 2023-03 |
| IPinside (I3GManager 1.0.1.23) | localhost:21300 웹 서버, IP·하드웨어 ID·실행 프로세스 노출, 약한 암호화, 스택 버퍼 오버플로. 2023-02 수정 예정이라 했으나 버전 번호 공개 없음 | **판정 불가.** 설치 후 21300 포트 확인 | C ✓ | Palant 2023-01 |
| CrossCert UniSign (버전 미상) | CVE-2019-12806 ≤2.0.4.0 스택 버퍼 오버플로 → 원격 코드 실행, CVSS 8.8 | 설치 후 버전 확인 | A ✓(집계 사이트) | KrCERT 35111 |
| SignKorea SKCertService (설치본 9.20) | CVE-2018-5202 ≤2.5.5 DLL 로딩 | 설치본과 서비스의 버전 체계가 달라 설치 후 확인 | A | NVD (검색 요약) |
| ezPDF Reader (S30, 버전 미상) | CVE-2020-7870 메모리 손상 → Reader 3.0.0.2 · CVE-2021-26605 런처 JSON-RPC 임의 명령 실행 | 설치 후 확인 | A | NVD·KrCERT (검색 요약) |
| TouchEn Key 3.1.0.24 (x86) | 찾지 못함. 검색 결과는 모두 nxKey | 11/10 아침 문서의 "취약점 보고"는 근거 미확인 | – | – |
| ASTx 1.18.1, nProtect Online Security 2026.1.7.1, MarkAny ePageSAFER | 찾지 못함("없음"이 아니라 "못 찾음") | – | – | – |

받지 않았지만 같은 계열: AnySign4PC ≤1.1.4.3 → 1.1.4.4, CrossEX ≤1.0.2.16 → 1.0.2.17 (ASEC 87004 ✓), KeySharp(Key#) biz ≤2.2.0.3099 → 2.2.0.3110 (ASEC 83622 ✓). Lazarus는 2023~25년 Innorix Agent·Cross EX 취약점을 실제 공격에 썼다(Kaspersky "Operation SyncHole", C).

학술(B): USENIX Security 2025 "Too Much of a Good Thing" — 7개 제품에서 19개 취약점(키로깅, 원격 코드 실행, 중간자 공격, 인증서 유출, 추적). 400명 설문 97% 설치, 48대 PC 평균 9개 설치, 상당수 수년 묵은 버전. 일부는 패치됐으나 근본 원인 상당수 미해결. **분석한 7개 제품 이름은 초록·학교 보도에 없고, PDF 본문은 이 PC에 추출 도구가 없어 확인하지 못했다.**

출처: https://asec.ahnlab.com/en/82372/ · https://asec.ahnlab.com/en/87004/ · https://asec.ahnlab.com/en/83622/ · https://palant.info/2023/03/06/veraport-inside-koreas-dysfunctional-application-management/ · https://palant.info/2023/01/25/ipinside-koreas-mandatory-spyware/ · https://www.welivesecurity.com/2020/11/16/lazarus-supply-chain-attack-south-korea · https://cvefeed.io/vuln/detail/CVE-2019-12806 · https://nvd.nist.gov/vuln/detail/CVE-2018-5202 · https://nvd.nist.gov/vuln/detail/CVE-2020-7870 · https://nvd.nist.gov/vuln/detail/CVE-2021-26605 · https://securelist.com/operation-synchole-watering-hole-attacks-by-lazarus/116326/ · https://www.usenix.org/conference/usenixsecurity25/presentation/yun · https://ee.kaist.ac.kr/en/research-achieve/in-south-korea-mandatory-financial-security-software-becomes-a-backdoor-and-a-target
