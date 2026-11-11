# lab — 보안 프로그램 영향 실험 (무인 실행)

설계: `docs/security-impact-design.md`(특히 11절). 앱·브라우저를 띄우고 재부팅하는 코드라서 읽기 전용 원칙이 있는 `collector/` 밖에 둔다.

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

복제본은 원본의 차등 디스크라 몇 초에 생기고 처음에는 수 MB다. **봉인한 원본 디스크는 절대 고치지 않는다**(고치면 복제본이 전부 깨진다).

### 한 번: 원본 만들기 (직접 하는 일 약 20분)

1. Windows 11 Enterprise 평가판 ISO(64비트, 90일)를 받는다 — Microsoft 평가판 센터 `https://www.microsoft.com/evalcenter/evaluate-windows-11-enterprise`. 등록 양식이 있어 직접 받아야 한다. 호스트와 같은 한국어를 권한다.
2. 원본 가상 머신을 만든다. 창이 열리면 "Press any key to boot from CD or DVD"에서 키를 누르고 Windows를 설치한다.
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

### 검증 상태 (11/12)

| 항목 | 상태 | 근거 |
|---|---|---|
| Hyper-V 켜기 (`enable-hyperv.ps1`) | 검증됨 | 11/11 실행, 재부팅 뒤 일반 권한 창에서 `Get-VM` 동작 |
| 일반 권한에서 가상 머신 생성·가상 TPM·차등 디스크·체크포인트·복원·삭제 | 검증됨 | 11/12 스파이크 + `vm\test-vm-scripts.ps1` 24개 확인 통과(OS 없는 시험용 가상 머신, 두 대 동시 실행 포함, 끝나면 모두 삭제) |
| 읽을 수 없는 ISO로 원본 만들기 | 검증됨 | 시작 실패 → 가상 머신과 폴더를 지우고 이유를 알려 줌 |
| 복원 직후 상태 값 | 검증됨(주의) | 꺼진 상태의 체크포인트로 복원한 직후 약 4초 동안 다른 프로세스에는 `Running`으로 보였다. 상태가 멈출 때까지 기다린 뒤 보고하게 했다 |
| 실제 Windows 설치, 봉인 뒤 복제본 부팅 | **미확인** | 이 PC에 ISO가 없다 |
| 복제본의 가상 TPM이 원본과 달라도 Windows가 정상 부팅하는지 | **미확인** | 복제본마다 새 키 보호기를 만든다. 장치 암호화가 켜진 원본이면 문제가 될 수 있다 |
| `guest.ps1`(PowerShell Direct) — 일반 권한 창에서 되는지 포함 | **미확인** | 게스트가 있어야 시험할 수 있다 |
| GPU 없는 가상 머신에서 Blender·Godot 창이 뜨는지 | **미확인** | 안 뜨면 가상 머신에서는 그 작업을 못 돌린다 |
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
