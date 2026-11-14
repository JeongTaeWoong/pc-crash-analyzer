# lab — 보안 프로그램 영향 실험 (무인 실행)

설계: `docs/security-impact-design.md`(특히 11절). 앱·브라우저를 띄우고 재부팅하는 코드라서 읽기 전용 원칙이 있는 `collector/` 밖에 둔다.

> **상태(11/14):** 세 번째 사례 세션이 끝났다(02:29~05:42, `plans\cases-3.json` 25개 작업 모두 완료). 실행 25개 중 **쓸 수 있는 것 24개**(office1 `disk-hog` 는 빠른 디스크라 피해가 없어 제외). 11/13 에 고친 스크립트는 게스트에서 모두 동작했다. **0x193 은 수집기 탓이 아니다** — GPU 복제본 9개 실행 모두 부팅 후 180~184초에 났고 수집기를 3분 늦게 켜도 같았다. office2 는 디스크 제한 3000 IOPS 에서 문제 없이 두면 디스크 평균 7.6% 이고, `disk-hog` 가 처음으로 사례가 됐다. 지금까지의 사례는 **개발용**이다(`eval\sets\dev-1.txt` 36개). 다음 차례는 **시험용 사례 생산** `plans\test-1.json`(52개 작업, 약 7시간): 끝나면 실행 폴더를 `cases-test` 로 옮기고 `eval\review_cases.py --no-engine` 으로만 본다 — 동결(11/23) 전에는 엔진·모델을 돌리지 않는다. 자세한 것은 `collector/NOTES.md` "11/14" 절.

> **상태(11/12 밤):** 수집기와 엔진을 고쳤다 — 엔진이 읽는 요약에 "마킹 직전 구간"(`resource_recent`), 디스크 신호, 새로 켜진 프로세스의 CPU. 오늘 사례 13개를 가상 머신 없이 재생한 결과 느려짐 6건 중 4건에서 범인이 1순위가 됐다(전에는 0건). 나머지 2건(video1 CPU, 디스크)은 새 수집기로 만든 덤프가 있어야 확인된다 → `plans\cases-2.json`(17개 작업, 약 5시간)에 넣었다. 테스트 프로그램은 정적으로 다시 빌드해 런타임 DLL 이 필요 없다. 정답 표기 `injected` 는 확정(`docs/conflict-test-plan.md` 3.2). 자세한 것은 `collector/NOTES.md` "11/12 밤" 절.
>
> **상태(11/12 저녁):** 첫 사례 세션이 끝났다(12:28~16:36). 사례 17개 중 **쓸 수 있는 것 13개**, "성공"으로 표시됐지만 덤프에 문제가 담기지 않은 것 3개(`F:\pca-vm\cases\_invalid\`로 옮김), 시간 초과 1개. 원인(느린 프로필에서 마킹이 부하보다 먼저 나감, 시험 프로그램의 DLL 누락, "덤프가 하나라도 있으면 성공")을 고쳤고, 다음 세션 계획은 `plans\cases-2.json`(15개 작업, 약 4.5시간)이다. 자세한 결과는 `collector/NOTES.md` "11/12 저녁" 절과 아래 "사례 만들기".
>
> **상태(11/12 낮):** 첫 무인 세션이 끝났다(03:24~04:08, 44분). 복제본 4개에 묶음이 설치돼 체크포인트로 남아 있다: `office1`(`bundle-office`, 2코어·4GB), `dev1`(`bundle-dev`), `design1`(`bundle-design`, 내장 GPU), `video1`(`bundle-video`, 8코어·16GB, 내장 GPU, DaVinci Resolve 21.2 포함). 카카오톡이 "설치 실패"로 보고된 것은 확인 방법의 오류였고 고쳤다. 게스트 안에서 사례를 만드는 틀(`case.ps1`, 아래 "사례 만들기")을 만들어 office1에서 시험했고, 첫 사례 세션 계획은 `plans\cases.json`(22개 작업)이다.
>
> **상태(11/12 새벽):** 원본을 봉인했고(Windows 11 Enterprise 평가판, 계정 `lab`, 자동 로그온·절전 없음·자동 업데이트 끔) 첫 복제본 `try1`이 동작한다. 게스트 안 명령 실행과 내장 GPU 분할이 실제 게스트에서 확인됐다(체크포인트 `gpu-ready`). 다음은 무인 실행기와 프로그램 설치 묶음이다.
>
> **상태(11/12 밤):** 호스트의 설치 전·후 측정을 **중단**했다(정태웅 결정, `docs/capstone-plan-v2.md` "11/12 밤 결정"). 호스트에는 보안 프로그램을 설치하지 않고, 아래 "사용법"의 호스트 세션은 돌리지 않는다(기록으로 남김). 사례는 가상 머신에서 만들고, 가상 머신 시험도 PC를 쓰지 않는 시간에 돌린다. 원본 가상 머신 `pca-base`에 Windows를 설치하는 중이다.
>
> **상태(11/12):** 가상 머신을 사례 생산에 쓰기로 했고 스크립트를 만들었다(아래 "가상 머신" 절 — 실제 Windows 설치는 ISO를 받은 뒤). 호스트에는 보안 프로그램을 맨 마지막에 한 번만 설치한다(11/24~11/26 시작).
>
> **상태(11/11 저녁):** Hyper-V를 켰다(18:19 재부팅). 그 전에 잰 첫 실험(시범 3블록 + 설치 전 4블록, 재부팅·자동 로그인·자동 재개 동작 확인)은 `runs-before-hyperv-20261111\`에 보관했고, 이후 측정과 섞지 않는다. `runs\`가 비어 있으므로 **다음 세션은 시범부터 새 실험을 시작한다**(시범 약 75분 + 설치 전 최대 6블록, 하룻밤). **관리자 설치 단계는 아직 한 번도 돌리지 않았다.** 보안 프로그램은 설치되지 않았고, 설치는 미뤄 둔 상태다(11/11 결정).

## 한눈에

```
start-session.ps1 ──재부팅──▶ (로그온) run-next.ps1 ──▶ run-block.ps1 한 블록 ──▶ 재부팅 ──▶ … ──▶ 끝낼 시각이 되면 화면 잠금
                                   │
                                   └ 단계: 시범(pilot) → 반복 수 계산 → 설치 전(s0) → [-AllowInstall일 때만] 설치 → 적응 재부팅 → 설치 후(s1) → 완료
```

- **블록** = 부팅 한 번 안에서 하는 측정 전부. 앱마다 워밍업 1회 뒤, 앱 3종 × 배경 4종을 무작위 순서로 돈다.
- **세션** = 한 번의 취침·외출. 끝낼 시각 전에 끝낼 수 있는 블록만 돈다. 세션 사이에는 PC를 평소처럼 쓴다.
- 설치는 `-AllowInstall`을 준 세션에서만, 설치 전 측정이 다 모였을 때만 한다.

## 사용법

### 한 번의 준비 (관리자 PowerShell, 약 10분)

```powershell
powershell -ExecutionPolicy Bypass -File "F:\2026CAU\Capstone2\pc-crash-analyzer\lab\setup-once.ps1"
```

관리자 창은 `C:\WINDOWS\system32`에서 시작하므로 전체 경로로 실행한다. 아래의 다른 명령도 전체 경로를 쓰거나 먼저 `cd F:\2026CAU\Capstone2\pc-crash-analyzer`를 한다.

설치 스크립트를 관리자만 쓸 수 있는 폴더(`C:\Program Files\PcaLab`)에 복사하고, 설치용 예약 작업을 등록한 뒤, 직접 해야 할 일을 체크 목록으로 보여 준다.

1. **자동 로그인 켜기** — Microsoft의 Sysinternals Autologon을 실행해 암호를 넣고 Enable. 이 PC의 계정은 Microsoft 계정이라 **PIN이 아니라 Microsoft 계정 암호**를 넣어야 한다. 암호가 암호화돼 시스템에 저장되므로 실험이 끝나면 Disable.
2. **Windows 업데이트 일시 중지** — 설정 → Windows 업데이트 → 일시 중지
3. Microsoft Store 앱 자동 업데이트 — 이 PC에서는 끌 수 없었다(11/10, "관리자 권한이 필요합니다"). 대신 앱 패키지 설치·업데이트가 끼어든 런을 이벤트 로그로 감지해 제외한다
4. **PC 블랙박스 자동 실행 끄기** — `setup-once.ps1`이 명령을 보여 준다
5. NVIDIA 앱의 드라이버 자동 다운로드 끄기
6. (권장) 로그온 때 저절로 켜지는 프로그램 끄기 — 이 PC에서는 Steam, Discord, KakaoTalk, Wallpaper Engine, OneDrive가 그렇다(11/10 확인). 켜 둬도 측정은 진행되고 설치 전·후에 똑같이 들어가지만, 스스로 업데이트하거나 움직이는 배경화면을 그리면서 잡음을 만든다. 작업 관리자 → 시작 앱에서 끄면 된다. **실험 기간 중간에 바꾸지는 말 것**(처음부터 끄거나, 끝까지 켜 두거나)

### 세션 (자기 전·나가기 전)

게임과 작업 중인 프로그램을 닫고 실행한다. 게임이나 안티치트가 떠 있으면 시작하지 않는다. 런처(Steam, STOVE 등)와 메신저는 경고만 하고 기록한다.

```powershell
powershell -ExecutionPolicy Bypass -File lab\start-session.ps1 -Until 07:30      # 또는 -Hours 4
```

30초 뒤 재부팅하고 측정을 시작한다. 끝나면 화면이 잠긴다. 설치 전 측정이 다 끝나면 세션이 "다음 세션을 `-AllowInstall`로 시작하라"는 메시지를 남기고 멈춘다.

```powershell
powershell -ExecutionPolicy Bypass -File lab\start-session.ps1 -Until 07:30 -AllowInstall   # 이 세션에서 설치까지 진행
```

### 도중에 멈추기, 진행 보기

```powershell
powershell -ExecutionPolicy Bypass -File lab\stop-session.ps1     # 지금 측정 중인 런이 끝나면 멈춘다. 끝난 블록은 유지
powershell -ExecutionPolicy Bypass -File lab\analyze.ps1          # 진행 상황과 지금까지의 결과 (runs\report.md)
```

`runs\lab.log`에 모든 진행이 시간순으로 남는다.

### 실험이 끝난 뒤

```powershell
powershell -ExecutionPolicy Bypass -File lab\setup-once.ps1 -Remove    # 관리자. 예약 작업·복사본 제거
```

자동 로그인 끄기, Windows 업데이트 다시 켜기, 보안 프로그램 제거(구라제거기)는 직접 한다.

## 가상 머신 (Hyper-V) — 11/11 켬, 11/12 사례 생산용으로 확정

가상 머신에서 하는 일(11/12 결정): 보안 프로그램 설치와 설치 창 자동 클릭 예행연습, 공식 충돌 후보 재현과 덤프 확보, 조치 실행기 시험, PC 블랙박스의 실제 설치 위치 업데이트 시험. 체크포인트로 되돌릴 수 있고 호스트는 깨끗하게 남는다. **시간 측정은 호스트에서 한다.** 가상 머신에서 시간을 잰다면 한 번에 한 대만 켠다.

### 구성

| 항목 | 값 (`vm\vm.json`) |
|---|---|
| 저장 위치 | `F:\pca-vm` (NVMe). 원본 `base\base.vhdx`, 복제본 `clones\<이름>\` |
| 한 대 | 2세대, 보안 부팅·가상 TPM, vCPU 4, 메모리 8GB 고정, 디스크 최대 100GB(동적), 기본 스위치 |
| 동시에 | 2~3대 권장(호스트를 쓰면서). 4~6대는 호스트를 비워 둘 때. 4대부터 안내가 뜬다 |
| PC 형태(`-Spec`) | `standard` 4코어·8GB(기본) · `office-low` 2코어·4GB · `office-old` 2코어(50% 제한)·4GB·디스크 3000 IOPS(8KB 단위, 약 24MB/s. 11/13 전에는 500) · `work` 8코어·16GB. 느린 PC를 흉내 낸 것이지 특정 기종의 모형이 아니다 |

복제본은 원본의 차등 디스크라 몇 초에 생기고 처음에는 수 MB다. **봉인한 원본 디스크는 절대 고치지 않는다**(고치면 복제본이 전부 깨진다).

### 한 번: 원본 만들기 (직접 하는 일 약 20분)

아래 명령의 `lab\vm\...`은 저장소 폴더(`F:\2026CAU\Capstone2\pc-crash-analyzer`)에서 실행할 때의 경로다. 다른 폴더(예: `C:\WINDOWS\system32`)에서 연 창이면 `F:\2026CAU\Capstone2\pc-crash-analyzer\lab\vm\base.ps1`처럼 **전체 경로**로 쓴다.

1. Windows 11 Enterprise 평가판 ISO(64비트, 90일)를 받는다 — Microsoft 평가판 센터 `https://www.microsoft.com/evalcenter/evaluate-windows-11-enterprise`. 등록 양식이 있어 직접 받아야 한다. 호스트와 같은 한국어를 권한다.
2. 원본 가상 머신을 만든다. "Press any key to boot from CD or DVD"는 스크립트가 대신 눌러 준다(그 바람에 언어·키보드 화면이 기본값으로 넘어가 있을 수 있다). 열린 창에서 Windows를 설치한다. 파일 복사 뒤 다시 시작할 때 같은 문구가 또 나오는데 **그때는 누르지 않는다**(누르면 설치가 처음부터 다시 시작된다).
   ```powershell
   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Create -Iso "받은 ISO 경로"
   ```
3. 설치 중 계정은 **가상 머신 전용 로컬 관리자 계정**으로 만든다(예: 이름 `lab`). 암호는 꼭 넣되 실제로 쓰는 암호는 넣지 않는다. Microsoft 계정으로 로그인하지 않는다. (계정 화면의 문구는 평가판에서 확인 전이다 — 보통 "로그인 옵션 → 대신 도메인 가입"이 로컬 계정으로 가는 길이다.)
4. 바탕 화면이 뜨면 봉인한다. 게스트를 종료시키고, ISO를 떼고, 디스크만 남긴다.
   ```powershell
   powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Seal
   ```
5. 게스트 계정을 한 번 저장한다(이 Windows 사용자만 풀 수 있게 암호화해 `%LOCALAPPDATA%\pca-lab`에 둔다. 저장소·명령줄에는 남지 않는다).
   ```powershell
   powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -SetCredential
   ```

### 복제본 쓰기

```powershell
powershell -ExecutionPolicy Bypass -File lab\vm\base.ps1 -Status                              # 원본·복제본·남은 용량
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -New finsec-a -Boot -Connect        # 만들고(체크포인트 clean) 켜고 창 열기
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Save finsec-a -Label installed     # 지금 상태 저장
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Restore finsec-a -Label clean -Boot  # 되돌리기(그 뒤의 변경은 사라진다)
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Stop finsec-a                      # 게스트 종료(-TurnOff: 전원 끄기)
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -Remove finsec-a                    # 가상 머신과 파일 삭제
powershell -ExecutionPolicy Bypass -File lab\vm\clone.ps1 -List

powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -Wait                # 게스트의 Windows가 응답할 때까지
powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -Run "Get-Service | Select-Object -First 5"
powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -CopyTo C:\호스트\파일 -Dest C:\lab\파일
powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name finsec-a -CopyFrom C:\lab\dumps -Dest F:\pca-vm\out\finsec-a
```

- 이름은 영문 소문자·숫자·`-`만 쓴다. 가상 머신 이름은 앞에 `pca-`가 붙는다.
- 측정 세션 중에는 가상 머신을 켤 수 없다(스크립트가 거부). 반대로 가상 머신이 켜져 있으면 세션이 시작을 거부한다(`lab.json`의 금지 프로세스 `vmwp`, `vmmem`).
- `guest.ps1 -Run`으로 실행한 명령에는 바탕 화면이 없다. 설치 창이나 브라우저처럼 창이 필요한 것은 게스트의 로그온 세션 안에서 실행해야 한다(게스트 안의 예약 작업이나 복사해 넣은 실행 스크립트).



### 프로그램 설치와 무인 실행 (11/12 새벽)

```powershell
powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -List                          # 프로그램과 묶음 목록(apps.json)
powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name try1 -Check              # winget ID·받아 둔 파일이 있는지
powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name office1 -Bundle office   # 켜져 있는 복제본에 묶음 설치
powershell -ExecutionPolicy Bypass -File lab\vm\install.ps1 -Name office1 -App krita,vlc

powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\bundles.json -Until 07:30    # 무인 세션(자기 전·나가기 전)
powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\bundles.json -Hours 3 -DryRun

powershell -ExecutionPolicy Bypass -File lab\vm\guest.ps1 -Name office1 -RunFile C:\호스트\스크립트.ps1 -Desktop   # 게스트의 바탕 화면 세션에서 실행
```

- **묶음**(`apps.json`): `tools`, `office`(브라우저 2종·LibreOffice·카카오톡·VLC + WordCraft·DeckCraft·GridCraft·PdfCraft), `design`(Krita·GIMP·Inkscape·Blender + PhotoCraft·VectorCraft·DesignCraft·FigCraft·AniCraft), `video`(OBS·FilmCraft·EffectCraft·SoundCraft·DaVinci Resolve), `cad`, `dev`, `peripheral`(Logitech G HUB), `artcraft`(Craft 앱 19개 전부). Craft 앱은 호스트의 `F:/ArtCraft`(`vm.json`의 `artcraft_dir`)에서 폴더째 복사한다. 유료 프로그램 대신 무료·평가판과 같은 유형의 프로그램을 쓴다(11/12 결정).
- 설치는 게스트의 **바탕 화면 세션**에서 관리자 권한으로 돈다(예약 작업을 한 번 만들어 실행). 창이 뜨는 프로그램은 이 방식이어야 한다.
- 설치한 프로그램과 **실제 버전**은 복제본 폴더의 `installed.json`에 남는다. 이 값은 **설치 시점**의 버전이다. Chrome 같은 프로그램은 켜 두면 몇 분 안에 스스로 버전을 올리므로(11/12 확인), 사례를 쓸 때는 그 시점의 버전을 게스트에서 다시 읽어 적는다.
- 설치 확인은 게스트의 설치 프로그램 목록에서 이름으로 하고, 이름이 한글로 등록되는 프로그램(카카오톡)은 레지스트리 키 이름으로 찾는다. 이미 설치된 프로그램은 `[have]`로 건너뛰고, 기록이 틀려 있으면 바로잡는다.
- 실패하면 그 순간의 화면을 PNG로 남긴다(클릭을 기다리는 창이 대부분의 원인이다).
- **무인 세션**(`session.ps1`)은 계획 파일의 작업을 하나씩 돈다: 복제본 만들기 → (GPU 붙이기·드라이버 복사·재시작) → 묶음 설치 → 종료 → 체크포인트. 체크포인트가 이미 있는 작업은 건너뛰므로 같은 계획을 다시 돌려도 된다. 남은 시간이 25분보다 적으면 새 작업을 시작하지 않고, 끝나면 가상 머신을 모두 끈다. 도는 동안 호스트가 절전으로 들어가지 않게 한다(설정은 바꾸지 않는다). 기록은 `F:\pca-vm\sessions\<시작 시각>\`.
- 받아 둔 설치 파일(DaVinci Resolve zip 등)은 `vm.json`의 `download_dirs`에서 파일 이름으로 찾는다. 저장소에는 넣지 않는다.

### 사례 만들기 (11/12 낮)

게스트 안에서 수집기(콘솔판)를 돌리면서 시나리오를 재생하고, 덤프와 "무엇을 넣었는지"를 호스트로 가져온다.

```powershell
powershell -ExecutionPolicy Bypass -File lab\vm\payload.ps1                                     # 게스트에 넣을 수집기·FaultLab 빌드(소스가 바뀌면 다시)
powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -List                                  # 시나리오 목록(scenarios.json)
powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -Name office1 -From bundle-office -Scenario baseline
powershell -ExecutionPolicy Bypass -File lab\vm\case.ps1 -Name office1 -From bundle-office -Scenario mem-pressure -Open chrome,libreoffice
powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\cases.json -Until 18:00   # 무인으로 여러 사례 (11/12 낮에 실행함)
powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\cases-3.json -Hours 7     # 11/14 새벽에 실행함: 별칭 사례 17 + idle-late 2 + office2 다시
powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\test-1.json -Hours 9      # 다음 차례(11/14 작성): 시험용 사례 52개. 끝난 작업은 다시 돌릴 때 건너뛴다
powershell -ExecutionPolicy Bypass -File lab\vm\packages.ps1                                    # 실제 프로그램 꾸러미(packages.json: 알려진 버그가 있는 옛 버전)를 <root>\payload\packages 에 만든다. -List 로 상태 확인
powershell -ExecutionPolicy Bypass -File lab\vm\session.ps1 -Plan lab\vm\plans\real-1.json -Hours 4      # 어려운 사례 첫 시도(11/14 작성, 25개 작업, 게스트 미실행): 실제 프로그램 버그 6종·사용자가 돌린 무거운 작업·Windows 와 런처의 자체 작업·두 가지가 동시에
# 세션은 낮에 PC 를 쓰는 동안 돌려도 된다(11/14 결정). 게임·안티치트(vm.json host_wait_processes)가 도는 동안에는 새 작업을 시작하지 않고 기다리고,
# 사례마다 호스트 부하를 scenario.json 의 host_load 에 적는다. 세션은 한 번에 하나만(<root>\session.lock) — 두 번째는 시작을 거부한다
```

- **흐름**: 저장 지점으로 되돌림 → 부팅 → 수집기·시험 프로그램 복사 → 수집기 시작 → 준비 시간(90초) → `-Open` 프로그램 열기 → 시나리오 → (수동 마킹) → 덤프 대기 → 수집기 종료 → 결과 복사 → **저장 지점으로 다시 되돌림**. 사례는 복제본에 아무것도 남기지 않는다.
- **시나리오**(`scenarios.json`): `baseline`(문제 없음 — 설치된 프로그램을 하나씩 켜 보고 끝에 수동 마킹. 실행 확인 겸 "정상" 사례), `idle`(아무것도 안 켜고 5분), `crash-dotnet`, `crash-plugin`(서드파티 DLL 안에서 크래시), `hang`(응답 없음), `mem-pressure`(게스트 메모리의 85%를 몇 초 만에 점유), `mem-leak-slow`(14초마다 50MB씩 60%까지), `cpu-hog`(100초), `cpu-hog-long`(13분), `disk-hog`. 11/14 에 시험용 변형을 추가했다(FaultLab 의 기존 방식만 쓴다): `cpu-hog-early`(25초 만에 마킹), `cpu-hog-late`(210초 뒤 마킹), `mem-leak-mid`(3초마다 50MB), `hang-late`, `crash-start`(켜자마자 종료), `crash-failfast`(게스트 미확인), `disk-hog-long`(느린 디스크 프로필용). 느려짐 계열은 부하가 걸린 뒤 사용자가 "느림"으로 마킹한다. 문제는 `collector/experiments/FaultLab`이 자기 프로세스 안에서만 일으킨다.
- **"성공"의 뜻**(11/12 저녁부터): 그 덤프를 시나리오가 말하는 사례로 쓸 수 있다는 뜻이다. ① 마킹은 FaultLab이 "부하 시작"을 출력한 뒤에 보낸다(프로세스를 켠 시각이 아니다). ② 시나리오가 기대한 종류의 덤프가 있어야 한다 — GPU 복제본이 스스로 남기는 `kernel-live` 덤프는 세지 않는다. ③ FaultLab이 문제를 실제로 일으켰다는 종료 코드여야 한다. 안 켜진 배경 프로그램은 경고로 남고, `scenario.json`의 `open`에는 실제로 돌던 것만 적힌다. `scenario.json`의 `usable`이 이 판정이다.
- **느리게 만든 복제본**(`office-old`): 대기와 시간 한도를 3배로 늘린다(설치 포함). 문제의 길이는 늘리지 않는다. 설치 101분, 사례 하나 23~27분이 걸렸다.
- **결과**: `F:\pca-vm\cases\<복제본>-<시나리오>\<시각>\` — `result.json`(게스트에서 일어난 일: 시간표, 프로그램별 실행 결과, 크래시 이벤트, 덤프 목록, **그 시점의 설치 프로그램 버전**), `scenario.json`(넣은 문제 = 정답, 복제본 사양), `dumps\`(수집기 덤프), `collector.log`, `faultlab.log`, `shots\`(30초마다 화면). 쓸 수 없다고 판정한 실행은 `cases\_invalid\`에 이유(`INVALID.txt`)와 함께 둔다.
- **느려짐 사례에서 범인이 남는 곳**(첫 세션 확인): CPU는 `events.json`의 `resources.topCpu`(FaultLab 99%), 디스크는 같은 곳의 프로세스 I/O 값과 `pagesPerSec`, 메모리는 `processes`의 `privMB`. `engine-snapshot.json`의 추세 항목은 프로세스가 10분 이상 살아야 채워지므로 100초짜리 문제에서는 비어 있다 — 그래서 긴 시나리오 두 개를 추가했다.
- **GPU 복제본의 경고 덤프**: `design1`·`video1`은 실행마다 `LiveKernelEvent 0x193`(그래픽 커널 라이브 덤프)을 남기고 수집기가 `kernel-live`로 덤프한다(6번 실행 모두, 원인 미확인). 넣은 문제가 아니므로 이 복제본의 사례에는 항상 이 덤프가 섞여 있다고 보고 읽는다.
- **정답 표시**: 넣은 문제라서 정답을 안다 → `confidence: "injected"`. `docs/conflict-test-plan.md`의 official / user / guess와 따로 센다(11/12 확정). `cause_category` 낱말은 임시이고, 엔진 분류와의 대응표는 엔진 형식으로 내보낼 때 만든다.
- 게스트의 수집기는 관리자 권한으로 돈다(ETW 포함). 콘솔판이라 어디로도 전송하지 않는다. 참여자판은 게스트에 넣지 않는다.
- FaultLab이 일으킨 크래시는 수집기가 시험용 프로그램으로 알아보고 유의성을 낮춘다(`tier: hidden`). 덤프는 그대로 만들어진다.
- 한계: 합성 문제 + 가상 머신이다. 실제 프로그램의 실제 버그 사례(골드셋의 본체)는 이 틀 위에 시나리오를 더해 만든다.

### GPU와 화면 (11/12 밤)

```powershell
powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Status                                # 나눠 줄 수 있는 호스트 GPU, GPU가 붙은 가상 머신
powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -Attach                  # 내장 Radeon 분할을 붙인다(꺼진 상태). -Gpu nvidia 도 가능
powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -InstallDriver           # 호스트 드라이버 파일을 게스트로 복사(켜진 상태, 게스트 계정 저장 후)
powershell -ExecutionPolicy Bypass -File lab\vm\gpu.ps1 -Name design1 -Detach

powershell -ExecutionPolicy Bypass -File lab\vm\screen.ps1 -Vm pca-design1 -Out F:\pca-vm\shots\design1.png   # 지금 화면을 PNG로
powershell -ExecutionPolicy Bypass -File lab\vm\screen.ps1 -Vm pca-design1 -Key 13                            # 키 누르기(13 = Enter)
```

- **GPU 분할이 붙어 있으면 체크포인트를 만들 수 없다**(Hyper-V가 거부, 11/12 확인). `clone.ps1 -Save`는 꺼진 상태에서만 되고, 분할을 뗀 뒤 저장하고 다시 붙인다. `-Restore`도 복원 뒤 다시 붙인다. 켜진 상태로는 저장할 수 없다.
- 게스트는 **호스트와 같은 드라이버**를 쓴다. "드라이버 버전이 달라서 생기는 문제"는 이 방법으로 재현할 수 없다.
- 기본은 내장 Radeon이다. 화면과 게임에 쓰는 RTX 4070 Ti SUPER에 게스트의 문제가 번지지 않게 하기 위해서다.
- Microsoft가 클라이언트 Windows의 일반 가상 머신에 공식 지원하는 구성이 아니다. 안 되면 "미지원"으로 적고 오래 붙잡지 않는다.
- 화면 캡처와 키 입력은 가상 머신에만 쓴다. 호스트의 프로그램(게임)에는 어떤 입력도 보내지 않는다.
- 스크립트 안에서 상태를 확인할 때 주의: 오래 열어 둔 PowerShell 창은 다른 프로세스가 바꾼 가상 머신 상태를 몇 초 늦게 보여 준다. 새 프로세스에서 읽거나 잠시 기다린다.

### 검증 상태 (11/12)

| 항목 | 상태 | 근거 |
|---|---|---|
| Hyper-V 켜기 (`enable-hyperv.ps1`) | 검증됨 | 11/11 실행, 재부팅 뒤 일반 권한 창에서 `Get-VM` 동작 |
| 일반 권한에서 가상 머신 생성·가상 TPM·차등 디스크·체크포인트·복원·삭제 | 검증됨 | 11/12 스파이크 + `vm\test-vm-scripts.ps1` 24개 확인 통과(OS 없는 시험용 가상 머신, 두 대 동시 실행 포함, 끝나면 모두 삭제) |
| PC 형태 프로필(`-Spec office-old` 등) | 검증됨(설정값 + 체감) | 자체 시험에서 CPU 수·50% 제한·메모리 4GB·디스크 500 IOPS가 적용되고 시작됨(27개 확인). 11/12 `office2`: 묶음 설치 101분(제한 없는 `office1`은 19.5분), 시험 프로그램 하나 읽어 들이는 데 82초 |
| 가상 머신에 키 보내기·화면 캡처(`screen.ps1`) | 검증됨 | 11/12 실제 ISO로 설치 화면까지 부팅시키고 화면을 PNG로 저장해 확인. 설치 창 자동화 예행연습에서 화면을 보는 데 쓴다 |
| 읽을 수 없는 ISO로 원본 만들기 | 검증됨 | 시작 실패 → 가상 머신과 폴더를 지우고 이유를 알려 줌 |
| 상태 값이 늦게 보이는 현상 | 원인 확인(주의) | 오래 열어 둔 PowerShell 창은 다른 프로세스가 바꾼 가상 머신 상태를 몇 초 늦게 보여 준다(복원·시작 직후). 스크립트는 상태가 멈출 때까지 기다린 뒤 보고하고, 시험은 새 프로세스에서 읽는다 |
| 실제 Windows 설치, 봉인 뒤 복제본 부팅 | 검증됨 | 11/12 새벽 복제본 `try1`: 새 가상 TPM으로 부팅, 자동 로그온, 1920×1080 |
| 복제본의 가상 TPM이 원본과 달라도 Windows가 정상 부팅하는지 | 검증됨 | 원본에서 C:를 복호화하고 자동 장치 암호화를 막아 두었다(`base.ps1 -Prepare`) |
| `guest.ps1`(PowerShell Direct) | 검증됨 | 일반 권한 창에서 명령 실행·대기·파일 복사 동작 |
| 게스트에서 GPU 분할 | 검증됨(장치 상태) | 내장 Radeon이 게스트 장치 관리자 기준 정상. 실제 프로그램이 이 GPU로 그리는지는 미확인 |
| 무인 세션, 묶음 4개(`plans\bundles.json`) | 검증됨 | 11/12 03:24~04:08: 복제본 4개 생성·GPU 붙이기·드라이버 복사·설치 34개 항목·체크포인트까지 사람 손 없이, 일반 권한 창에서. 끝난 뒤 켜진 가상 머신 없음 |
| DaVinci Resolve 21.2 조용한 설치(`/i /q /noreboot`) | 검증됨(설치·실행) | 370초, 종료 코드 0, 설치 프로그램 목록에 21.2.00006. `video1`의 `baseline`에서 시작 창("Welcome to DaVinci Resolve")이 90초 안에 뜸, 같은 날 두 번 더 창이 뜬 채로 돌았다. 프로젝트 열기·렌더링은 미확인 |
| GPU 없는 복제본에서 Craft 앱 | 검증됨(사무용 4개) | `office1`에서 WordCraft·DeckCraft·GridCraft·PdfCraft 창이 뜸. 그림·영상·CAD 쪽은 미확인 |
| 설치된 프로그램의 실행 | 검증됨(창까지) | 11/12 `baseline` 4개 복제본: 33개 실행 모두 살아 있고, GIMP만 25초 안에 창이 없음. 프로그램 안에서 작업하는 것은 미확인 |
| 게스트에서 수집기 실행 + 사례 만들기(`case.ps1`) | 검증됨(13개 사례) / 수정분은 부분 | 11/12 첫 세션: 시나리오 7종이 한 번 이상 쓸 수 있는 사례를 냈다(`disk-hog`는 office1만). 무효 3건의 원인을 고쳤고, 고친 부분은 호스트에서 확인한 것(표식 읽기, 플러그인 크래시)과 다음 세션에서 확인할 것(게스트 전체 흐름, 느린 프로필)으로 나뉜다 |
| 느린 프로필에서 사례 만들기 | 검증됨 | 11/13(500 IOPS): `mem-pressure`·`cpu-hog`·`baseline` 완료(24~49분), `disk-hog` 는 원격 세션이 끊겨 실패. 11/14(3000 IOPS): `idle`·`disk-hog`·`cpu-hog`·`mem-pressure` 4/4 완료(7.6~9.5분), 끊김 없음. 문제 없이 두면 디스크 평균 7.6% |
| GPU 복제본의 `LiveKernelEvent 0x193` | 재현됨(원인 미확인) | GPU를 붙인 복제본의 6번 실행 전부, 수집기 시작 111~115초 뒤. GPU 없는 복제본 0건. 호스트에는 없음 |
| 게스트의 센서(온도·전압·팬) | 미지원 | 값이 하나도 나오지 않는다. 센서가 근거인 사례는 가상 머신으로 만들 수 없다 |
| 금융 보안 모듈이 가상 머신에서 실제 PC와 같게 동작하는지 | **미확인** | 첫 설치 예행연습에서 프로세스·드라이버 목록으로 확인 |

### Hyper-V 켜기 (11/11에 끝남 — 기록)

```powershell
powershell -ExecutionPolicy Bypass -File lab\vm\enable-hyperv.ps1 -Check                  # 지금 상태만 본다(바꾸는 것 없음)
powershell -ExecutionPolicy Bypass -File lab\vm\enable-hyperv.ps1 -AddMeToHyperVAdmins    # 켠다: UAC 창에서 "예" 한 번. 재부팅은 하지 않는다
```

- 일반 PowerShell 창에서 실행해도 된다. 스크립트가 관리자 권한을 직접 요청한다(창이 `C:\WINDOWS\system32`에서 시작해도 관리자 창이 아닐 수 있다 — 제목에 "관리자:"가 있어야 관리자 창이다).
- `-AddMeToHyperVAdmins`는 지금 계정을 "Hyper-V Administrators" 그룹에 넣는다. 그래야 가상 머신을 일반 권한 창에서 만들고 다룰 수 있다.
- 켜면 호스트의 드라이버·서비스가 바뀐다. **그 전에 잰 자료와 그 뒤에 잰 자료는 다른 실험이다.** 그 전 자료는 `runs-before-hyperv-20261111\`에 보관했고 시범부터 다시 잰다.

## 폴더

| 경로 | 내용 | git |
|---|---|---|
| `lab.json` | 고정 설정: 반복 수, 대기 시간, 배경 페이지, 금지 프로세스. 첫 측정 뒤에는 바꾸지 않는다 | 포함 |
| `tools.lock.json` | 받은 앱의 버전·해시·서명 고정값. 다르면 실행을 거부한다 | 포함 |
| `start-session.ps1` `stop-session.ps1` `run-next.ps1` `run-block.ps1` `analyze.ps1` | 세션·상태 기계·블록·분석 | 포함 |
| `setup-once.ps1` `install-elevated.ps1` | 관리자 준비, 설치 단계 | 포함 |
| `lib/` | 공용: 프로세스 샘플러, UI 응답 탐침, 입력, 작업 드라이버, 배경 브라우저, 상태·지문·분석, 설치 창 자동 클릭 | 포함 |
| `tasks/` | 고정 작업: `blender/bench.py`, `godot/make-project.ps1`·`bench.gd` | 포함 |
| `spikes/` | 사전 확인과 모의 실행 테스트 | 포함 |
| `vm/` | Hyper-V 켜기(`enable-hyperv.ps1`), 원본(`base.ps1`), 복제본·체크포인트(`clone.ps1`), 게스트 안 실행(`guest.ps1`), 설정(`vm.json`), 자체 시험(`test-vm-scripts.ps1`). 가상 머신 파일은 저장소 밖 `F:\pca-vm` | 포함 |
| `tools/` | 측정에 쓰는 포터블 앱(Blender 5.2.3 LTS, Godot 4.7.2, Notepad++ 8.9.9) | 제외 |
| `runs/` | 원자료(`boots\<블록>\run-NNN.json`), 상태(`state.json`), 로그, 보고서 | 제외 |

## 무엇을 재나

| 앱 | 지표 |
|---|---|
| Blender | 창이 뜰 때까지(`gui_start_ms`), 창 없는 작업 전체(`bench_wall_s`: CPU 렌더·물리·저장 40회) |
| Godot | 프로젝트 임포트(`import_s`), 스크립트 계산·파일 3,000개 입출력, 에디터 창이 뜰 때까지(`gui_start_ms`) |
| Notepad++ | 시작(`start_ms`), 300자 입력 지연(`type.p50/p95`), 50MB 파일 열기(`big_open_ms`)·저장(`big_save_ms`) |
| 공통 | 창 응답 지연(0.1초마다 확인, 100ms·200ms 이상 횟수), PC 전체 CPU, 감시 프로세스별 CPU·메모리 |

배경: `B0` 없음 · `B1` 유튜브 영상 · `B2` 은행 로그인 페이지 2곳(열어 두기만 함) · `B3` 문서 페이지 10개를 20초마다.

측정에서 빼는 런(사전 등록): 스크립트가 보내지 않은 입력이 있었음 · Defender 검사 시작 · Windows 업데이트 설치 · 앱 패키지(Store·MSIX) 설치나 업데이트 · Defender 정의 업데이트 뒤 각 앱의 첫 실행(11/11 시범 자료를 보고 추가) · 금지 프로세스 등장 · 배경이 뜨지 않음.

## 검증한 것 / 못 한 것 (11/10)

| 항목 | 상태 |
|---|---|
| 받은 앱 3종의 공식 체크섬·서명 | 검증됨 |
| 작업 3종 실행과 지표 | 검증됨 — 같은 부팅 안 흔들림 0.2~1% 수준 |
| 배경 3종: 유튜브 재생, 은행 페이지 로드, 문서 순환, 종료 시 실험용 브라우저만 정리 | 검증됨 |
| 블록 하나 전체 | 검증됨 — 시험용 짧은 대기 15런 5.8분, **실제 대기 시간 33런 43.8분**(오류 0, 제외 0). 리허설 원자료: `runs-rehearsal-20261110\` |
| 상태 기계: 시범 → 계획 → 설치 전 → (허락 없으면 멈춤) → 설치 실패 시 멈춤 → 설치(모의) → 적응 → 설치 후 → 완료, 시간 부족 시 종료, 도중 정지 후 재개 | 검증됨 — **재부팅 없는 모의 실행**, 31개 항목 통과(`spikes/test-statemachine.ps1`) |
| 실제 모드의 시작 거부(게임 실행 중, 자동 로그인 꺼짐) | 검증됨 — 재부팅·상태 변경 없이 거부 |
| 설치 창 자동 클릭 | 시험용 3쪽 마법사로만 검증. **실제 보안 프로그램 설치 창에서는 미확인** |
| 세션 끝 화면 잠금 | 검증됨 — 잠금 호출이 동작함(11/10 16:07) |
| 실제 재부팅 후 자동 재개, 자동 로그인 | 검증됨 — 11/11 첫 세션(02:22~07:25): 재부팅 6번, 시범 3블록 + 설치 전 4블록, 201런 중 오류 1·제외 1. 블록 하나는 부팅부터 다음 부팅까지 56분 |
| 환경 지문 비교(세션 시작, 블록 간) | 검증됨 — 11/11 고침. 처음 기준이 재부팅 전 상태여서 측정한 부팅과 달랐다. 지금은 최근 블록과 비교하며, 실제 상태 사본으로 모의 시작해 차단 0건 확인 |
| `setup-once.ps1`, 관리자 예약 작업 등록 | 검증됨 — 11/10 저녁 실행. 복사본 8개의 해시가 원본과 일치, 일반 권한으로는 쓰기 거부, 작업은 트리거 없이 최고 권한으로 등록 |
| `install-elevated.ps1`(설치 단계 본체) | **미확인** — `-AllowInstall` 세션에서 처음 동작 |
| 설치 후 은행 페이지에서 보안 모듈이 실제로 깨어나는지 | 미확인 — 설치 후에만 확인 가능. 런마다 기록한다 |

## 점검 스크립트

```powershell
powershell -ExecutionPolicy Bypass -File lab\spikes\spike-probes.ps1        # 샘플러·응답 탐침·입력·이벤트 (30초)
powershell -ExecutionPolicy Bypass -File lab\spikes\spike-tasks.ps1         # Blender·Godot 반복 실행 흔들림 (2분)
powershell -ExecutionPolicy Bypass -File lab\spikes\test-tasks.ps1 -Backgrounds   # 작업·배경 한 번씩 (창이 앞으로 나옴)
powershell -ExecutionPolicy Bypass -File lab\spikes\test-autoclick.ps1      # 설치 창 자동 클릭 (10초)
powershell -ExecutionPolicy Bypass -File lab\spikes\test-statemachine.ps1   # 세션 전체 모의 실행 (약 25분)
```

창이 앞으로 나오는 테스트는 PC를 쓰지 않을 때 돌린다. Notepad++에 스크립트가 글자를 입력하기 때문이다.
