# 지인 배포 연구 운영 — 참여자판 PC 블랙박스 v0.7 (팀용)

참여자(지인·일반 사용자) PC 에서 블랙박스를 돌리고, 수집한 기록을 **참여자가 파일을 보내는 일 없이** 팀이 받는 구조와 운영 절차.
참여자용 안내는 [`participant-guide.md`](participant-guide.md) (배포 zip 에 `읽어주세요.txt` 로 들어간다).

## 1. 구조

```
참여자 PC                                            팀 Google 계정                 팀 PC
─────────────────────────────────────────────        ─────────────────────         ─────────────────────────────
PcBlackBox.exe (트레이, 관리자 — UAC)                 Apps Script 웹 앱             StudyTools decrypt
  수집기(collector/ 소스 그대로, 메모리 링버퍼)          형식·크기·토큰만 검사          (개인키 %USERPROFILE%\.pca-study)
  → 자동 감지/단축키 → 덤프(%ProgramData%\PcBlackBox)   → Drive 폴더/<참여 ID>/       → <참여 ID>/<덤프>/*.json
  → zip → AES-256-GCM + RSA-OAEP(팀 공개키) ──HTTPS──→   *.pcae.json (암호문)  ──내려받기→  → crash_engine diagnose
  → 디스크 대기열(재시도) · 하트비트(8시간)                                               → StudyTools summary
```

- 수집 코드는 `collector/` 에만 있다. 참여자판(`participant/BlackBox.Participant`)은 같은 소스를 링크하고 트레이·동의·전송만 더한다 — 수집기 원칙(분석 없음, 조치 없음, 마킹 전 디스크 쓰기 없음)은 그대로.
- 수신기는 **내용을 열 수 없다**. Google 도, 주소를 알아낸 제3자도 마찬가지. 열 수 있는 것은 개인키를 가진 팀 PC 뿐.
- **관리자 권한(0.8~)**: 매니페스트 `highestAvailable`. 관리자 계정이면 켤 때 UAC 로 권한을 받아 커널 ETW(프로그램 시작·DLL 로드 실시간)·보안 필터 드라이버 목록(fltmc)·접근 거부되던 프로세스의 모듈까지 모은다. 표준 사용자 계정이면 UAC 없이 그대로 돌고 관리자 전용 항목은 `unavailable` 로 기록(0.7 과 같음).
  관리자로 돌 때 지키는 것:
  - 기록 폴더는 `C:\ProgramData\PcBlackBox\<SID>` — 루트는 관리자·SYSTEM 만 쓰기, 소유자 Administrators, OWNER RIGHTS 읽기 전용 상속, 본인은 읽기만. 루트를 다른 계정이 먼저 만들어 두었거나 연결(정션)이면 쓰지 않고 멈춘다(`--check-data-dir` 로 확인).
  - 자동 실행은 작업 스케줄러(로그인 트리거, 가장 높은 권한, 실행 시간 제한 없음) + `C:\Program Files\PcBlackBox` 복사본. 다운로드 폴더의 exe 를 가리키면 일반 권한 악성 프로그램이 바꿔치기해 관리자 권한을 얻으므로 쓰지 않는다. 작업 XML 도 설치 폴더 안에서 만들어 넘긴다.
  - 커널 ETW 세션 이름은 판마다 다르다(`PcBlackBox-Kernel`, 자체 점검 `PcBlackBox-Kernel-SelfTest`, 콘솔판 `CrashCollector-BlackBox-Kernel`). 로그오프가 확정되면 세션을 먼저 멈춘다.

| 폴더 | 역할 |
|---|---|
| `participant/StudyCore` | 연구 설정 검증, 암호 봉투(`Envelope`), 전송 대기열(`Outbox`), 수신 규칙(`ReceiverRules`), zip 묶음 |
| `participant/BlackBox.Participant` | 참여자 앱(WinForms 트레이). exe 이름 `PcBlackBox.exe` |
| `participant/StudyTools` | 팀 도구: `keygen` `check` `receive`(시험 수신기) `decrypt` `summary` |
| `participant/receiver` | Apps Script 수신기 `Code.gs`·`appsscript.json`, Node 규칙 시험 `test-receiver.js` |
| `tools/publish-participant.ps1` | 배포 zip 빌드(설정 검증·금지 파일 점검·SHA-256) |

## 2. 처음 한 번 (팀)

### 2-1. 키 — 완료(10/29)

`StudyTools keygen` 으로 만들었다. 키 ID `6e88a733c8746f43`.

- 개인키: `%USERPROFILE%\.pca-study\6e88a733c8746f43.private.pem` (정태웅 PC) — **이 파일이 없으면 받은 기록을 아무도 못 연다.** USB 에 백업하고, 분석할 팀원에게만 직접 건넨다(채팅·메일·저장소 금지).
- 설정: `participant/study.local.json` — 공개키·토큰·문의처. `.gitignore` 로 커밋 제외(토큰과 수신 주소가 저장소에 남지 않게).
- 키를 바꿀 때: `StudyTools keygen --config participant/study.local.json --rotate`. 이전 개인키는 지우지 않는다(이전 기록 복호화용). 새 키로 다시 빌드·배포해야 한다.

### 2-2. 수신기 배포 (Google 계정 필요 — 팀이 직접)

1. https://script.google.com → 새 프로젝트 → 이름 "PC블랙박스 수신기".
2. `Code.gs` 내용을 `participant/receiver/Code.gs` 로 바꿔 붙여넣고 저장.
3. 함수 목록에서 `setupFolder` 실행 → 권한 승인("확인되지 않은 앱" 경고는 본인이 만든 스크립트라 뜨는 것 — 고급 → 이동). 실행 로그에 Drive 폴더 주소가 나온다.
4. 프로젝트 설정(톱니) → 스크립트 속성 → 속성 추가 `TOKEN` = `study.local.json` 의 `token` 값.
5. 배포 → 새 배포 → 유형 "웹 앱" → 실행 사용자 **나**, 액세스 권한 **모든 사용자** → 배포 → 웹 앱 URL(`…/exec`) 복사.
6. 브라우저로 URL 을 열면 `{"ok":true,"service":"pca-receiver","v":1}` 이 보여야 한다.
7. `participant/study.local.json` 의 `endpoint` 에 URL, `contact` 에 참여자가 볼 연락처(예: "정태웅 · 카카오톡 ID …")를 넣는다.
   `collectUntil`(예: `"2026-12-11"`)을 넣으면 그 날이 지나 켜질 때 앱이 기록을 멈추고 자동 실행을 끈 뒤 로컬 삭제를 묻는다(비우면 `retentionUntil`).

> Code.gs 를 고친 뒤에는 "배포 관리 → 수정 → 새 버전"을 해야 같은 URL 에 반영된다.

### 2-3. 점검

```powershell
participant\StudyTools\bin\Release\net8.0\StudyTools.exe check --config participant\study.local.json
# 팀 PC 에서: 배포 빌드의 PcBlackBox.exe 를 한 번 실행해 동의 → 그다음 "관리자 PowerShell" 에서
PcBlackBox.exe --selftest
PcBlackBox.exe --check-data-dir   # 기록 폴더 보호 확인(OK 가 나와야 함)
```

> 0.8 부터 exe 가 관리자 권한을 요청한다. 일반 PowerShell 에서 `--selftest` 를 부르면 UAC 창이 뜨고 출력이 보이지 않을 수 있으니 관리자 PowerShell 에서 실행한다. 관리자 아닌 경로를 시험하려면 `$env:__COMPAT_LAYER='RunAsInvoker'` 를 먼저 설정한다.

`--selftest` 는 실제 수집기로 마킹 1건 → 덤프 → 봉투 → 전송 → 메모 → 하트비트를 하고 단계별 OK/FAIL 을 출력한다(참여 ID `selftest…` 폴더로 들어가 실제 참여자와 섞이지 않는다). 실제 수신 주소로는 그 PC 에서 동의한 뒤에만 돈다.

### 2-4. 배포 빌드

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish-participant.ps1 -Version 0.8.0
# → dist\PcBlackBox-0.8.0-win-x64.zip (+ .zip.sha256.txt)
```

- 127.0.0.1 시험 설정이면 거부한다(시험 빌드는 `-AllowLoopback`, 이름에 `-LOCALTEST`).
- 설정에 개인키가 섞였거나 배포 폴더에 `*.pem`·`*.pdb`·`study.local.json` 이 있으면 실패한다.
- 크기 약 65MB(.NET·WinForms 런타임 포함, 설치 불필요). 카카오톡·Google Drive 링크로 전달하고 SHA-256 을 함께 알린다.

## 3. 받기 (주 1~2회)

1. Drive 의 수신 폴더를 통째로 내려받아(오른쪽 클릭 → 다운로드) 저장소 밖에 푼다. 예: `D:\pca\study-inbox\`.
2. 복호화·요약:

```powershell
StudyTools decrypt --in D:\pca\study-inbox --out D:\pca\study-decrypted
StudyTools summary --in D:\pca\study-decrypted
```

- 같은 봉투를 다시 넣어도 이미 처리한 것은 건너뛴다. 머리(참여 ID 등)나 본문이 바뀐 봉투는 서명·복호화 단계에서 실패로 표시된다.
- **서명 키 고정**: 참여자마다 동의할 때 만든 서명 키로 모든 봉투에 서명한다. `decrypt` 는 참여자별로 처음 본 키를 `<참여 ID>/signer.spk` 에 고정하고, 다른 키로 온 봉투는 "위장 의심"으로 거부한다. 한 참여 ID 에 키가 둘 이상이면 `★ 서명 키 충돌` 을 출력한다 — 참여자에게 확인할 것.
- 이름·파일은 허용 형식만 쓴다(참여자 폴더 밖 경로, `..`, 허용 목록 밖 zip 항목은 거부·무시).
- 결과: `<참여 ID>/<덤프>/` (meta·events·snapshot·engine-snapshot·upload.json, 메모가 오면 `report.json`), `<참여 ID>/heartbeats/`, 철회 시 `<참여 ID>/withdraw/`.
- 진단: `engine\build\Release\crash_engine.exe diagnose <덤프>\engine-snapshot.json` (10/29 참여자판 덤프 3건으로 확인).
- 복호화본은 `study-decrypted/` 처럼 저장소 밖 또는 `.gitignore` 된 폴더에만 둔다(원본 덤프는 저장소에 넣지 않는다 — 팀 규칙).

### 하트비트로 보는 것

`summary` 의 마지막 하트비트·버전·수집기 CPU% 와, 하트비트 파일의 `sources`(소스별 ok/unavailable)·`outbox`(대기·실패·마지막 오류)·`log`(최근 운영 기록 80줄, 경로·이름 치환됨). "기록이 안 온다" = 하트비트도 안 옴(꺼짐·네트워크) / 하트비트는 옴(문제가 없었음 — 음성 데이터).

## 4. 참여 철회

참여자가 "참여 그만두기"를 누르면 `withdraw` 봉투가 온다.
- Apps Script 가 배포 계정 메일로 참여 ID 를 알린다(메일 권한이 없으면 생략).
- `decrypt` 가 `★ 참여 철회 요청 … 서명 확인됨` 을 출력한다. 메일만 오고 `decrypt` 에서 서명 실패로 나오면 위장일 수 있으니 지우기 전에 참여자에게 확인한다.
- 할 일: Drive 수신 폴더의 해당 참여 ID 폴더, 팀 PC 의 복호화본·파생 케이스를 지운다. 보고서에 이미 쓴 집계 수치는 식별 불가하므로 남긴다(동의 화면에 그렇게 적었다).

## 5. 보안 모델

| 위협 | 대책 | 남는 위험 |
|---|---|---|
| 전송 중 가로채기 | HTTPS + 봉투 암호화(AES-256-GCM, 키는 RSA-3072-OAEP-SHA256 로 감쌈) | 없음(팀 개인키 유출 제외) |
| 저장소(Drive)·Google·수신기 열람 | 수신기는 개인키가 없다 — 암호문만 저장 | 머리 메타데이터(참여 ID·종류·시각·앱 버전·서명 공개키)는 보인다 |
| 배포 zip 을 가진 사람이 참여자인 척(가짜 철회·메모·하트비트) | 참여자별 서명 키(ECDSA P-256)로 머리·암호문 서명, 팀 도구가 참여자별 첫 키를 고정 | 참여 ID 를 모르는 새 가짜 참여자는 만들 수 있다 — 하트비트·별명 없는 참여 ID 는 의심 |
| 수신 주소·토큰 유출(배포본에 들어 있음) | 토큰·형식·크기 검사, 6시간 고정 구간 상한(참여자 150건·50MB, 전체 2000건·300MB), Drive 사용량 80% 초과 시 거부, 오류 응답에 상세 없음 | 상한을 채우는 방해는 가능 — 새 배포 주소로 교체 |
| 봉투 머리 조작 | 머리 전체를 AAD 로 묶고 서명에도 포함 — 바뀌면 검증·복호화 실패 | 없음 |
| 받은 봉투로 팀 PC 공격(경로 조작·압축 폭탄) | 이름은 앱 형식만(점 없음 — `..`·장치 이름 불가), 쓸 경로가 참여자 폴더 안인지 확인, zip 항목 허용 목록, 64항목·64MB 상한 | 없음 |
| 팀 개인키 유출 | 저장소 밖 보관, keygen 이 git 저장소 안 저장을 거부 | 사람 실수 — 유출 시 `--rotate` 후 재배포 |
| 배포본에 개인키·시험 설정이 실림 | 앱·빌드 스크립트가 PRIVATE KEY 포함 설정 거부, 시험 설정 빌드 거부, 금지 파일 점검 | 없음 |
| 개인정보 | 창 제목·키 입력·화면·파일 내용·통신 내용 수집 안 함. 프로필 경로(8.3 짧은 경로 포함, 다른 사용자 폴더 오치환 없음)·사용자 이름·PC 이름 치환. 짧은 이름(예: 두 글자)은 경로 조각·도메인\이름 형태에서 치환. ETW 명령줄 인자 삭제 | 일반 문장 속 두 글자 이름, 파일·폴더 이름(예: 프로젝트 이름)은 남는다 — 동의 화면에 명시 |
| 참여자 PC 디스크·네트워크 | 로컬 기록 200MB·300건(하드 400MB) + 대기열 100MB·500건·30일, 같은 대상 자동 덤프 시간당 6건, 수동 기록 30초 연타 무시, 끊긴 임시 파일 정리 | 없음 |
| 관리자 권한 악용(0.8~) | 기록 폴더는 일반 권한으로 못 쓰는 ProgramData 하위(소유자·쓰기 권한·OWNER RIGHTS·연결 여부 확인, 선점돼 있으면 멈춤). 자동 실행은 Program Files 복사본만 가리킴. 작업 인자는 고정(`--autostart`) | 사용자가 다운로드 폴더에서 직접 켤 때(UAC 승인) 그 폴더에 미리 심긴 DLL 은 막지 못한다 — 일반 설치 파일과 같은 위험 |
| 참여자 PC 안정성 | 관리자 권한은 읽기에만, 커널 드라이버 없음(센서 라이브러리는 PawnIO 미설치면 드라이버를 올리지 않음), 읽기 전용, 자동 조치 없음, 전체 화면 중 창 안 띄움, 한 번만 실행, 로그오프 "취소" 뒤에도 정상(SessionEnded 에서만 정리), 종료 때 UI 를 막지 않음 | 코드 서명이 없어 SmartScreen 경고, 일부 백신의 평판 기반 차단 가능 |
| 안티치트 보호 게임 | 모듈 열거가 거부된 PID 는 살아 있는 동안 다시 열지 않음(10초마다 시도하지 않음) | 첫 1회 열기 시도는 남는다 — 반응은 미확인 |
| 동의 없는 수집 | 동의 체크 필수, 거절 시 저장 없음. 동의는 연구 ID·받는 곳에 묶임(시험 동의가 실제 배포본으로 넘어가지 않음). 시험 옵션은 127.0.0.1 설정 + 별도 `--data-dir` 에서만. `--selftest` 는 그 연구에 동의한 PC 에서만 | 없음 |
| 철회 뒤 업로드 | 철회 시 수집·전송을 먼저 멈추고 → 삭제 요청 → 로컬 삭제(`selftest` 포함). 정리 중 끝난 덤프는 보내지 않음 | 없음 |

## 6. 검증 상태 (10/29, CLAUDE.md 4분류)

| 항목 | 상태 | 근거 |
|---|---|---|
| 봉투 암호화·서명, 머리·암호문·서명 키 바꿔치기 거부, 대기열 상한, 연구 종료일 | **검증됨** | StudyCore.Tests 48건 |
| 수집기 단위 시험(치환·짧은 이름·8.3·명령줄·증상 정규화·크래시 반복 상한) | **검증됨** | BlackBox.Tests 84건 |
| 수집 → 덤프 → 봉투 → 전송 → 복호화, 원본과 바이트 일치 | **검증됨** | 시험 수신기(302 흉내) + 자체 점검, 4개 파일 `cmp` 일치 |
| 트레이 앱 전체 흐름(동의 → 단축키 경로 → 질문 창 → 덤프 → 전송 → 하트비트 → 종료) | **검증됨** | `--test-*` 실행, 부팅 재생 41 + 수동 2건 전송 |
| 철회 순서(정지 → 요청 → 삭제), 정리 중 끝난 덤프 미전송, 바로가기·로컬 삭제 | **검증됨** | `--test-withdraw-after` — 덤프 2건이 정리 중 끝났으나 전송 0, 철회 봉투만 도착, 데이터 폴더·바로가기 사라짐 |
| 위장 봉투(가짜 철회·`..` 이름·허용 밖 zip 항목) | **검증됨** | 공격자 흉내 도구로 3건 — 가짜 철회 "위장 의심" 거부, `..` 거부, `run.cmd` 무시, 참여자 폴더 밖 파일 0 |
| 자동 실행 바로가기 생성·대상 경로 | **검증됨** | 임시 시작 폴더(`--startup-dir`), WScript.Shell 로 대상 확인 |
| 사용자·PC 이름 누출 | **검증됨(이 PC)** | 덤프·복호화본·봉투에서 사용자 이름·PC 이름 0건 |
| 수신 규칙 C# ↔ Apps Script 일치 | **검증됨(Node 흉내)** | `test-receiver.js` 30개 판정(실제 서명 봉투 10건, 바이트 상한·저장 공간·철회 메일 포함) |
| 참여자판 유휴 오버헤드 | **검증됨(이 PC, 16스레드)** | 8분: 기계 CPU 0.204%(1코어 3.3%), Private 121MB 일정, 정상 종료 |
| 배포 zip 탐색기 압축 풀기(한글 이름)·게시본 자체 점검 | **검증됨(이 PC)** | 65.3MB·278파일 |
| 실제 Apps Script 배포에서의 302 → GET 응답·Drive 저장·메일 | **미확인** | Google 계정 필요 — 배포 직후 `--selftest` 로 확인 |
| `DriveApp.getStorageUsed/Limit`, `createFile` 문자열 크기 상한 | **미확인** | Node 흉내로만 시험. 봉투는 보통 80~300KB |
| `drive.file` 같은 좁은 범위로 동작하는지 | **미확인** | 지금은 `drive` 전체 범위(DriveApp 기본) |
| 다른 PC 의 SmartScreen·백신(V3 등)·안티치트 반응 | **미확인** | 지인 1~2명 시범 배포로 확인 |
| 관리자 아닌 경로(0.8 — 표준 사용자 계정) | **검증됨** | `__COMPAT_LAYER=RunAsInvoker` 로 자체 점검 4단계·트레이 흐름, 예약 작업 안 만듦 |
| 관리자 경로(0.8): 기록 폴더 선점 방어·ACL, 예약 작업 등록·실행, 설치본, 그만두기 정리, ETW 명령줄 인자 삭제, 강제 종료 뒤 세션 잔류 | **권한 필요 / 시험 대기** | `tools/test-participant-admin.ps1` (UAC 한 번) |
| 참여자 PC 장기(2주) 안정성 | **미확인** | 하트비트 `self.cpuPctOfMachine`·`privateMB` 로 원격 관찰 |

## 7. 문제 해결

| 증상 | 확인 |
|---|---|
| 참여자: "설정 파일(study.json)을 읽지 못했어요" | zip 을 다시 풀게 한다(폴더째 옮기다 study.json 이 빠짐) |
| 참여자: "압축 파일 안에서 바로 실행하셨어요" | zip 을 먼저 풀고 실행 |
| 하트비트 `outbox.lastError` = `bad_token` | Apps Script 스크립트 속성 TOKEN 과 `study.local.json` 토큰이 다름 — 수신기 쪽을 고치면 대기열이 저절로 다시 보낸다 |
| `rate_limited` | 6시간 창 상한 — 기다리면 저절로 이어서 보낸다. 쓰레기 업로드라면 새 배포 주소로 교체 |
| `JSON 아닌 응답` | 배포 접근 권한이 "모든 사용자"가 아니면 Google 로그인 페이지(HTML)가 온다 |
| 단축키가 안 됨 | 다른 앱이 Ctrl+Shift+F12 를 씀 — 트레이 메뉴 "지금 문제 기록하기" 사용(시작 시 알림으로 안내됨) |
| 팀: "시험 수신기 설정이나 --test-* 옵션은 --data-dir …" | 시험 설정(127.0.0.1)으로는 기본 데이터 폴더를 쓰지 않는다 — `--data-dir <임시 폴더>` 를 준다 |
| 참여자: 새 버전으로 바꾸기 | 아이콘 → 종료 → 새 zip 을 풀고 새 PcBlackBox.exe 실행(같은 참여 ID 유지, 자동 실행 바로가기는 새 경로로 고쳐짐) |
