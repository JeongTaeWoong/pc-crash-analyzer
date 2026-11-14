# 실제 사례 조사 — 분야별 알려진 문제와 사례로 쓸 수 있는지 (2026-11-14)

정태웅 11/14 요구("독립 런처 게임, Steam, 포토샵, 블렌더, 마야, VS, VS Code 부터 모든 분야의 이슈를 확인하고 사례로 쓸 수 있는지, 비슷한 프로그램과 AI 방법도 참고")에 대한 조사 결과다. 11/10 조사(`external-survey.md`, `collector/experiments/repro/candidates.json` 15건)에 없던 것만 적는다.

- **조사 방법**: 분야를 다섯으로 나눠 동시에 조사했다. 아래 링크는 조사 때 실제로 연 페이지다. **실행해 본 것은 "상태" 칸에 적은 것뿐이고, 나머지 재현 절차는 문서에서 읽은 예상이다.**
- **근거 등급**: A 제조사 공식 문서·릴리스 노트 · B 공식 이슈 기록(담당자 확인 또는 수정 커밋) · C 언론·커뮤니티.
- **분류**: **골드** = 우리 가상 머신에서 사람 손 없이 다시 만들 수 있다(옛 버전을 공식 경로로 받을 수 있고 명령으로 일으킬 수 있다) · **실버** = 근거는 좋지만 로그인·유료 라이선스·특정 하드웨어·특정 Windows 업데이트가 필요하다 · **팩** = 사례로는 못 쓰고 지식 팩 항목("버전 X 에 문제 Y, Z 에서 수정")으로만 쓴다.
- 조사한 한 건이 세 군데에 쓰인다: 사례의 정답, 지식 팩 항목, "공식 해결책과 일치하는가" 채점의 기준.

## 1. 지금 계획에 넣은 것 (`lab/vm/plans/real-1.json`, 게스트 미실행)

| 시나리오 | 무엇 | 근거 | 고친 버전 | 상태 |
|---|---|---|---|---|
| `real-ahk-alttab` | AutoHotkey 2.0.29, `a & b::AltTabMenu` 단축키에서 크래시 | [변경 기록](https://www.autohotkey.com/docs/v2/ChangeLog.htm) (A) | 2.0.30 | 11/10 호스트 3/3 |
| `real-godot-svg` | Godot 4.3, JPEG 가 든 SVG 를 가져오다 크래시 | [이슈 98341](https://github.com/godotengine/godot/issues/98341) (B) | 4.5-dev4 | 11/10 호스트 3/3 |
| `real-npp-column` | Notepad++ 8.6.7, Column Editor 에서 종료 | [이슈 15144](https://github.com/notepad-plus-plus/notepad-plus-plus/issues/15144) (B) | 8.6.8 | 11/10 호스트 3/3 (키 입력 필요) |
| `real-python-setlocale` | CPython 3.12~3.13.5, 긴 로캘 이름으로 `setlocale` 하면 종료 | [이슈 137273](https://github.com/python/cpython/issues/137273) (B) | 3.13.6 | 11/14 호스트 스파이크: 0xC0000409, 이벤트 1000, 오류 모듈 ucrtbase.dll |
| `real-git-maintenance` | Git for Windows 2.47.0, `git maintenance start` 크래시 | [PR 5198](https://github.com/git-for-windows/git/pull/5198) (B), 릴리스 노트 (A) | 2.47.0(2) | 미실행 — 첫 실행이 게스트 |
| `real-blender-bgscene` | Blender 5.1.0, 배경 장면이 있는 장면을 Cycles 로 렌더하면 크래시 | [5.1 수정 릴리스 노트](https://developer.blender.org/docs/release_notes/5.1/corrective_releases/) (A), [#155851](https://projects.blender.org/api/v1/repos/blender/blender/issues/155851) (B) | 5.1.1 | 미실행 — 명령 줄 렌더에서도 나는지, Windows 크래시 기록이 남는지 모른다. 공식 보관본의 SHA-256 은 확인 |
| `sys-defender-scan` | Defender 전체 검사 중의 느려짐(Windows 자체 작업) | [Microsoft 문서](https://learn.microsoft.com/en-us/defender-endpoint/configure-advanced-scan-types-microsoft-defender-antivirus) (A): 수동 검사는 CPU 제한 없이 돈다 | 기다리거나 예약 | 미실행 |
| `bg-steam-update` | Steam 이 큰 꾸러미를 내려받고 검증하는 동안의 느려짐(런처 자체 작업). SteamCMD 익명 로그인 — 개인 계정 없음 | 공식 파일의 서명 확인(Valve) | 기다리거나 속도 제한·예약 | 미실행 |
| `work-7zip`, `work-blender` | 사용자가 직접 돌린 무거운 작업(범인 없음) | — | — | 미실행 |
| `busy-mem-pressure`, `busy-hang`, `busy-crash-plugin` | 사용자 작업 중에 다른 프로그램의 문제가 겹침 | — | — | 미실행 |

## 2. 다음에 넣을 골드 후보

### 개발 도구

| 프로그램·버전 | 증상 | 근거 | 고친 버전 | 일으키는 방법(예상) |
|---|---|---|---|---|
| TypeScript 5.1.3 + `@types/lodash` 4.14.195 | CPU·메모리 폭주(검사 239초, 2.38GB, 이후 메모리 부족) | [이슈 54542](https://github.com/microsoft/TypeScript/issues/54542) (B) | 5.1.5 무렵(5.2.2 정상) | `npm i` 로 세 꾸러미 고정, `import "lodash";` 한 줄, `npx tsc`. **같은 프로그램 이름으로 정상 버전과 짝지을 수 있다** |
| Godot 4.3 | UV 가 없는 `.obj` 를 가져오다 크래시 | [이슈 97152](https://github.com/godotengine/godot/issues/97152) (B) | 4.4 | 지금 꾸러미의 실행 파일 그대로, 이슈에 붙은 프로젝트만 바꾼다 |
| .NET SDK 10.0.100 | 12줄짜리 부분 이벤트 파일에서 컴파일러 스택 오버플로 | [이슈 80986](https://github.com/dotnet/roslyn/issues/80986) (B) | 수정 SDK 버전 미확인 | `winget install Microsoft.DotNet.SDK.10 --version 10.0.100`, `dotnet build`. 영향 버전은 추정 |
| Node.js 22.6.0 | 비ASCII 이름 폴더에 `fs.cpSync` 하면 출력 없이 종료 | [이슈 54476](https://github.com/nodejs/node/issues/54476) (B) | 22.10.0 | [공식 zip](https://nodejs.org/dist/v22.6.0/). 한국어 코드 페이지에서의 동작 미확인 |
| Node.js 23.0.0~23.3.0 | `fs.rmSync('速')` 크래시 | [이슈 56049](https://github.com/nodejs/node/issues/56049) (B) | 수정 릴리스 미확인 | [공식 zip](https://nodejs.org/dist/v23.3.0/) |
| MSVC 17.10 이후로 빌드한 프로그램 × 옛 `msvcp140.dll` | `std::mutex` 에서 크래시 | [STL 변경 기록](https://github.com/microsoft/STL/wiki/VS-2022-Changelog) (A), [이슈 4730](https://github.com/microsoft/STL/issues/4730) (B) | 재배포 패키지 갱신 | 옛 재배포 패키지 고정 설치 + 이슈의 12줄 프로그램. **두 구성 요소의 충돌 사례** |
| asciidoctor-vscode 3.4.1 | 폴더가 많은 작업 공간에서 키 입력마다 `rg.exe` 여러 개, 전 코어 100% | [이슈 921](https://github.com/asciidoctor/asciidoctor-vscode/issues/921) (B) | 3.4.2 | 확장 버전 고정 설치 방법 미확인, 키 입력 필요 |
| Defender 실시간 검사 × 빌드·꾸러미 복원 | 작업 프로세스 옆에서 `MsMpEng.exe` 가 CPU 를 쓴다 | [성능 모드](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-endpoint-antivirus-performance-mode), [Dev Drive](https://learn.microsoft.com/en-us/windows/dev-drive/) (A) | 제외 설정·Dev Drive | `npm install` 수백 개. **공식 문서가 있는 두 프로그램 사례** |

### 사무·브라우저·Windows 자체

| 프로그램·버전 | 증상 | 근거 | 고친 버전 | 일으키는 방법(예상) |
|---|---|---|---|---|
| LibreOffice 26.2.0.3 | 나뉜 떠 있는 표가 든 DOCX 를 열다 멈춤 | [tdf#170381](https://bugs.documentfoundation.org/rest/bug/170381/comment) (B) | 26.2.1 | `soffice --writer <첨부 파일>`. winget 에 그 버전이 있다 |
| LibreOffice 26.2.0.3·26.2.1.2 | 클립보드가 비었을 때 하이퍼링크 삽입에서 크래시 | [tdf#170803](https://bugs.documentfoundation.org/rest/bug/170803/comment) (B) | 26.2.3 | 클립보드 비우고 Ctrl+K (키 입력 필요) |
| LibreOffice Calc (25.8.5 이전) | 1E-21 을 분수 서식으로 바꾸면 멈춤 | [tdf#167892](https://bugs.documentfoundation.org/rest/bug/167892/comment) (B) | 25.8.5 | 키 입력 여러 번 |
| LibreOffice Calc × 닿지 않는 네트워크 프린터 | `.ods` 를 열 때 20초~2분 멈춤 | [tdf#42673](https://bugs.documentfoundation.org/rest/bug/42673/comment) (B) | 27.2.0(미출시) | 죽은 주소로 프린터를 만들어 기본 프린터로. **지금 설치된 버전도 해당. 두 프로그램 사례.** 절차 미확인 |
| Windows Search 색인 | 파일이 많이 생긴 뒤 SearchIndexer 등의 CPU·디스크 | [Microsoft 문서](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/windows-search-performance-issues) (A) | 폴더 제외 | 서비스 멈춤, 작은 파일 3만 개 생성, 서비스 시작 |
| Defender 실시간 검사 × 파일을 많이 쓰는 프로그램 | 다른 프로그램이 느려지고 MsMpEng 가 CPU 1위 | [Microsoft 문서](https://learn.microsoft.com/en-us/defender-endpoint/troubleshoot-performance-issues) (A) | 제외 설정 | 작은 파일 수만 개가 든 압축 풀기. 11/10 호스트 `dotnet build` 에서는 약 1% 였으므로 2코어 게스트에서 |
| .NET NGEN, 구성 요소 정리(DISM) | Windows 자체 작업의 CPU·디스크 | [NGEN](https://learn.microsoft.com/en-us/dotnet/framework/tools/ngen-exe-native-image-generator), [WinSxS 정리](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder) (A) | — | 부하 크기 미확인 |

### 게임 런처·오버레이·주변기기 유틸

이 분야는 다시 만들 수 있는 것이 적다. 런처는 스스로 업데이트하고 로그인이 필요하며, 주변기기 유틸은 장치가 필요하다. 안티치트 게임은 실행하지 않는다는 원칙도 그대로다.

| 프로그램·버전 | 증상 | 근거 | 고친 버전 | 비고 |
|---|---|---|---|---|
| OBS Studio 33.0.0 | 옛 방식 이미지 슬라이드 쇼에서 메모리 약 10GB | [이슈 14076](https://github.com/obsproject/obs-studio/issues/14076) (B), [33.0.1](https://github.com/obsproject/obs-studio/releases/tag/33.0.1) (A) | 33.0.1 | winget 으로 버전 고정 가능. 장면 모음 파일을 한 번 손으로 만들어야 한다 |
| RTSS 7.3.7 이전 × AMD OpenGL 드라이버 × DaVinci Resolve | Resolve 크래시 | [RTSS 변경 기록](https://www.guru3d.com/files_details/rtss_rivatuner_statistics_server_download.html) (A) | RTSS 7.3.7 | Radeon 분할이 붙은 video1 에서만. 옛 RTSS 를 받을 수 있는지 등 미확인 여러 건 |
| SteamCMD | 큰 내려받기 동안의 디스크·네트워크 부하(버그 아님) | winget 매니페스트 (A) | — | 익명 로그인을 쓴다 — "게스트에 로그인 금지" 원칙에 맞는지 정태웅 결정 필요 |

## 3. 실버·팩 — 지식 팩 항목으로 쓸 것

| 항목 | 내용 | 근거 |
|---|---|---|
| Steam 클라이언트(2026-10-13 이전) × `EZFRD64.DLL` | 실행 직후 크래시(게임패드 진동 드라이버 DLL) | [Steam 클라이언트 소식](https://store.steampowered.com/feeds/news/app/593110) (A) |
| Wallpaper Engine × Razer `RzChromaSDK64.dll`, Nahimic `MMDevAPI.dll`, 백신 | 크래시 | [공식 도움말](https://help.wallpaperengine.io/en/crash/application.html) (A) |
| 새 Teams·WebView2 × 끼어드는 DLL | 느려짐·종료. Avast `aswhook.dll`, Fasoo `f_nxa.dll`, Nahimic, NVIDIA `nvspcap64.dll`, SEP `sysfer.dll` | [Microsoft 문서](https://learn.microsoft.com/en-us/microsoftteams/troubleshoot/teams-administration/include-exclude-teams-from-antivirus-dlp) (A) — **DLL 이름과 제품의 대응표로 바로 쓸 수 있다** |
| Firefox 131.0~131.0.2 × Avast/AVG | 특정 사이트에서 크래시 | [131.0.3 릴리스 노트](https://www.firefox.com/en-US/firefox/131.0.3/releasenotes/) (A) |
| Playnite × Avast·AVG·Malwarebytes·Nahimic·RTSS | 크래시·화면 깨짐 | [공식 문제 해결](https://api.playnite.link/docs/manual/gettingStarted/helpAndTroubleshooting/troubleshooting.html) (A) |
| Windows 11 KB5067036(2025-10-28) | 작업 관리자를 닫아도 `taskmgr.exe` 가 남아 쌓인다 | [릴리스 상태](https://learn.microsoft.com/en-us/windows/release-health/resolved-issues-windows-11-24h2) (A) |
| 클래식 Outlook 2406 이후 | 입력 중 CPU 30~50% | [Microsoft 지원](https://support.microsoft.com/en-US/support/known-issues/cpu-spikes-when-typing-in-classic-outlook-for-windows) (A) |
| VS Code 1.97.0 / 1.125.0 | 멈춤 / 확장 호스트 응답 없음 | [이슈 239838](https://github.com/microsoft/vscode/issues/239838), [이슈 321901](https://github.com/microsoft/vscode/issues/321901) (B) |
| Git for Windows 2.50.0 | SSH 복제가 멈춤 | [이슈 5688](https://github.com/git-for-windows/git/issues/5688) (B) |
| WSL2 `vmmem`, Docker, Android 에뮬레이터 | 메모리 사용, 백신 검사 중 멈춤 | [WSL 설정](https://learn.microsoft.com/en-us/windows/wsl/wsl-config), [Docker 백신 안내](https://docs.docker.com/engine/security/antivirus/) (A) — 중첩 가상화가 필요해 사례로는 못 쓴다 |
| **Defender × WinRing0 드라이버**(2025-03 부터) | Fan Control·OpenRGB·**LibreHardwareMonitorLib** 등의 드라이버를 `HackTool:Win32/Winring0` 로 차단 | [Fan Control 논의](https://github.com/Rem0o/FanControl.Releases/discussions/3017) (C) — **우리 수집기도 LibreHardwareMonitorLib 를 쓴다. 고정한 버전이 어느 드라이버를 싣는지 확인해야 한다(미확인)** |

## 4. 찾았지만 없던 것

- Steam 의 steamwebhelper CPU·메모리, 셰이더 미리 만들기, 내려받기 디스크 부하에 대한 공식 글.
- Epic Games Launcher, EA app, Ubisoft Connect, GOG Galaxy, Xbox 앱, 넥슨·STOVE·카카오게임즈 런처의 공식 알려진 문제(포럼 글뿐).
- Logitech G HUB, Razer Synapse, Armoury Crate 의 제조사 인정 문제.
- Visual Studio 2022/2026 에서 사람 손 없이 일으킬 수 있는 버전 고정 문제. JetBrains 도 같다.
- Zoom, Slack, 카카오톡, 한컴오피스의 공식 알려진 문제 페이지. Chrome·Edge 의 "버전 X 가 문제"라고 회사가 확인한 릴리스.
- ESET, Kaspersky, Bitdefender, AhnLab V3 의 Windows 11 24H2/25H2 충돌 공식 글.

## 5. 비슷한 프로그램에서 가져올 것

| 이름 | 무엇을 하나 | 가져올 것 |
|---|---|---|
| [Copilot PC Insights](https://support.microsoft.com/en-us/microsoft-copilot/pc-insights) | 물었을 때만 사양·백신 상태·현재 CPU 등을 읽고 설명만 한다. 기록을 쌓지 않고 고치지 않는다 | 질문마다 동의(이번만/항상/나중에)와 "무엇을 읽었다" 표시. 과거 기록이 없다는 점이 우리와의 차이 |
| [Windows 설정 에이전트](https://learn.microsoft.com/en-us/windows/configuration/settings/agent) | 설정만 바꾸고 되돌리기가 있다. 확신이 낮으면 보통 검색으로 물러난다 | 확신이 낮을 때 규칙 쪽으로 물러나는 구조, 되돌리기 |
| [신뢰성 모니터(WMI)](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/racwmiprov/win32-reliabilitystabilitymetrics) | Windows 가 이미 쌓아 두는 안정성 지수(1~10)와 기록 | "이 PC 는 평소에도 불안정한가"의 무료 기준선 후보. 새 신호이므로 스파이크 먼저 |
| Microsoft [ETW MCP](https://devblogs.microsoft.com/performance-diagnostics/etw-mcp-intro/) · WinDbg MCP · WPA MCP (2026) | 추적 파일·덤프를 AI 가 질의. 결과는 묶어서 준다. 두 추적 비교 도구가 있다 | 원본이 아니라 정리된 결과를 주는 방식(우리 요약과 같은 방향), 평소와 사건의 비교, AI 가 한 일을 화면에 남기기 |
| [mcp-windbg](https://github.com/svnscha/mcp-windbg) | 덤프 분석 + 개인정보를 지우는 필터 스크립트 | 내보내기 전 가리기 |
| [TroubleScout](https://github.com/sasler/TroubleScout) | `Get-*` 는 자동, `Set/Stop/Remove-*` 는 예/아니오/설명 | 승인 창의 "설명" 선택지 |
| macOS `tailspin`, WPR 메모리 기록 모드 | 운영 체제 수준의 상시 기록기 | 링버퍼 크기 기준(WPR: 메모리의 1~5%) |
| HP·Dell·Lenovo·Samsung 도우미, ControlUp·Nexthink·Lakeside·NinjaOne | 대부분 장치 정보 설명이나 기업용 원격 조치 | 조치 기록 화면, 전체 정지 스위치, 하드웨어 자가 진단을 "하드웨어 한계" 확인에 쓰는 것 |

찾지 못한 것: 덤프 하나로 "이 PC 는 늘 이렇다"와 "프로그램이 원인이다"를 가르는 제품이나 논문 · 상시 링버퍼와 AI 진단을 합친 개인용 도구.

## 6. AI 방법에서 가져올 것

| 순위 | 무엇 | 근거 | 우리 자료로 재는 방법 | 품 |
|---|---|---|---|---|
| 1 | **평소 상태와의 대비를 요약에 넣는다** | 미해결 문제(늘 포화인 PC). Microsoft ETW MCP 도 두 추적 비교를 둔다 | 늘 포화인 실행을 5개 이상 만들고, 느린 프로필 사례와 함께 다시 돌려 "부품/프로그램" 답을 센다. 기존 23/23 이 유지되는지 확인 | 1~2일 |
| 2 | **Haiku 3회가 일치하면 받아들이고, 갈리면 Sonnet** | [Self-consistency](https://arxiv.org/abs/2203.11171), [FrugalGPT](https://arxiv.org/abs/2305.05176). 말로 한 확신은 과신이다([Xiong 외](https://arxiv.org/abs/2306.13063)) | 이미 받은 답으로 무료 계산: 일치/불일치별 잘못된 지목률, 상향 비율, 정답 1건당 요금 | 0.5일 |
| 3 | **답 형식을 조인다**: 조치를 허용 목록의 ID 로만 고르게 하고 출력 상한을 4천 토큰 근처로 | [구조화 출력 문서](https://platform.claude.com/docs/en/build-with-claude/structured-outputs.md) | 답 600개당 응답 실패(지금 2), 허용 밖 권고 0 | 0.5일 |
| 4 | **증거를 믿을 수 없는 자료로 다룬다** + 속임 사례 묶음 | 프로세스 이름·이벤트 글은 공격자가 쓸 수 있다. [AgentDojo](https://arxiv.org/abs/2406.13352), [CaMeL](https://arxiv.org/abs/2503.18813), [Rule of Two](https://ai.meta.com/blog/practical-ai-agent-security/) | 기존 사례 10개에 지시문을 심어 1순위 변화와 허용 밖 조치(목표 0)를 센다 | 1일 |
| 5 | **평가 실행은 일괄 처리(50% 할인)** | [일괄 처리 문서](https://platform.claude.com/docs/en/build-with-claude/batch-processing.md) | 개발용 Sonnet 실행을 일괄로 다시 돌려 요금 비교 | 0.5일 |
| 6 | **조치로 확인하고, 나빠지면 되돌린다** | [STRATUS](https://arxiv.org/abs/2506.02009), [AutoSD](https://arxiv.org/abs/2304.02195) | 가상 머신에서 의심 프로그램을 30초 멈추고 지표가 돌아오는지: 진짜 원인에서만 돌아오는가 | 2~3일 |

우리 구조를 뒷받침하는 외부 결과:
- OpenRCA 순위표의 최고 점수는 여전히 11.34% 다([순위표 자료](https://raw.githubusercontent.com/microsoft/OpenRCA/main/docs/src/data/modelData.ts)). 이 점수를 올린 방법은 "증거를 미리 한 번 계산해 모든 방식에 똑같이 주는 것"이었다(Full Score 14.10 → 35.90, [Gopal & Krishnan 2026](https://arxiv.org/abs/2607.13548)) — 우리의 "수집기 요약 → AI" 와 같은 구조다.
- 에이전트가 원본을 직접 뒤지게 하면 "자료를 잘못 읽음·탐색 부족"이 모든 모델에서 나오고 지시문만 고쳐서는 해결되지 않는다([Kim 외 2026](https://arxiv.org/abs/2602.09937)).
- 클라우드 논문에서 옮겨 올 수 없는 것: 서비스 연결 구조, 수만 건의 과거 사고 자료, 다중 에이전트 구성, 라벨이 필요한 로그 탐지기.

Claude API 에서 확인한 것(11/14, 공식 문서):
- 토큰 수 세기는 무료다 — 사용자가 넣은 키가 맞는지 확인하는 데 쓸 수 있다([문서](https://platform.claude.com/docs/en/build-with-claude/token-counting.md)).
- 공식 C# SDK: NuGet 꾸러미 `Anthropic`([문서](https://platform.claude.com/docs/en/api/sdks/csharp)).
- 승인이 필요한 조치는 도구 실행기 대신 직접 짠 반복문으로 하라고 문서가 권한다([문서](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-runner.md)).
- Haiku 5.5 는 입력이 10만 토큰을 넘으면 요금이 오른다 — 원본 전체 방식이 Haiku 에서도 0.17달러인 이유다([요금](https://platform.claude.com/docs/en/about-claude/pricing.md)).

## 7. 창작·디자인·3D·영상 도구

**Adobe 와 Autodesk 의 공식 지원 페이지는 열리지 않았다(403).** 그래서 Photoshop·Premiere·After Effects·Maya·AutoCAD 의 공식 알려진 문제는 확인된 근거로 적지 못했다. 열린 Maya 2026·2027 알려진 문제 목록에는 크래시·멈춤 항목이 없었다. 이 두 회사 제품은 계정이 필요해 어차피 실버다.

| 프로그램·버전 | 증상 | 근거 | 고친 버전 | 분류·일으키는 방법(예상) |
|---|---|---|---|---|
| Blender 5.1.0 | 배경 장면이 지정된 장면을 Cycles 로 렌더하면 크래시 | [5.1 수정 릴리스 노트](https://developer.blender.org/docs/release_notes/5.1/corrective_releases/) (A), [#155851](https://projects.blender.org/api/v1/repos/blender/blender/issues/155851) (B) | 5.1.1 | 골드. `blender -b <첨부 파일> -E CYCLES -f 1`. 보고는 화면 미리 보기였고 배경 실행은 미확인. [5.1.0 공식 보관소](https://download.blender.org/release/Blender5.1/) |
| Blender 5.1.0 | 옛 버전의 Complex Solidify 파일을 열면 크래시 | 같은 노트 (A), [#155792](https://projects.blender.org/api/v1/repos/blender/blender/issues/155792) (B) | 5.1.1 | 골드. 같은 설치로 한 건 더 |
| Blender 5.0.x, 4.5.0~4.5.5 | 합성기의 Denoise 노드에서 프레임마다 메모리가 늘다 메모리 부족 크래시(보고자: 1,500프레임 뒤 약 80GB) | [#151940](https://projects.blender.org/api/v1/repos/blender/blender/issues/151940) (B) | 4.5.6, 5.1 | 골드. `blender -b … -a`. **정상 렌더와 겉모습이 같아 헷갈리는 사례로 좋다.** 8GB 게스트 권장 |
| Blender 5.0.1·5.1.2·5.2.0 LTS | 위치 속성이 없는 점 구름에 Points to SDF Grid 를 쓰면 크래시 | [#162961](https://projects.blender.org/api/v1/repos/blender/blender/issues/162961) (B) | 수정 릴리스 미확인 | 골드. **지금 게스트에 깔린 버전도 해당할 수 있다** |
| Krita 5.2.15 | AVIF 를 열다 크래시 | [버그 515714](https://bugs.kde.org/show_bug.cgi?id=515714) (B), [5.2.16 발표](https://krita.org/en/posts/2026/krita-5.2.16-released/) (A) | 5.2.16 | 골드, 단 보고가 전부 Linux 라 Windows 미확인. `krita --export …` |
| Inkscape 1.3~1.4 × Noto Color Emoji 글꼴 | 메모리가 수십 GB 로 불어 PC 전체가 멈춤 | [1.4.2 노트](https://wiki.inkscape.org/wiki/Release_notes/1.4.2) (A), [#5368](https://gitlab.com/inkscape/inkscape/-/issues/5368) (B) | 1.4.2 | 골드. **프로그램과 글꼴의 충돌 사례.** 명령으로 일어나는지는 미확인 |
| Krita × Sandboxie, 사전 프로그램 | 끊김·멈춤 | [Krita FAQ](https://docs.krita.org/en/KritaFAQ.html) (A) | 그 프로그램을 끈다 | 골드 가능성. 효과 크기를 몰라 스파이크 필요 |
| GIMP 3.0.0 | Gradient Flare 크기 0, 없는 글꼴 선택에서 크래시 | [NEWS](https://gitlab.gnome.org/GNOME/gimp/-/raw/gimp-3-0/NEWS) (A) | 3.0.2 | 팩 |
| Premiere Pro 26.2.0~26.5.2 × Windows 돋보기·텍스트 커서 표시기 | 메뉴를 열 때 5~7분 멈춤 | [Adobe 커뮤니티](https://community.adobe.com/bug-reports-728/p-adobe-premiere-pro-v26-2-0-build-65-severe-system-hang-when-accessing-menus-and-panels-1558171) (C, Adobe "확인함") | 없음 | 실버(계정) |
| Wacom 드라이버 6.4.1-2 × Maya·3ds Max | 크래시 | [Wacom 6.4.1-3 노트](https://cdn.wacom.com/u/productsupport/drivers/win/professional/releasenotes/Windows_6.4.1-3.html) (A) | 6.4.1-3 | 실버(라이선스·장치) |
| Clip Studio Paint 3.0.0 × 오래된 Edge | 시작할 때 멈춤 | [Celsys FAQ](https://support.clip-studio.com/en-us/faq/articles/20240010) (A) | Edge 갱신 | 실버·팩 |

헷갈리는 정상 작업(버그 아님, 미실행): Blender 시연 장면 [classroom](https://download.blender.org/demo/test/)의 Cycles CPU 렌더 · EXR 500장 생성 · Inkscape 의 고해상도 내보내기.

찾지 못한 것: Huion·XP-Pen 의 공식 크래시 안내 · 글꼴 관리자·색 관리 유틸의 공식 문제 · GIMP 의 명령 줄 재현 절차가 있는 크래시.
