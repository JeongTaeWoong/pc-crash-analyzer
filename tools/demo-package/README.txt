PC Crash Analyzer - 시연용 묶음 {VERSION}
만든 때: {BUILT_AT}   소스: {COMMIT}

이 폴더만 있으면 됩니다. .NET, Visual C++ 런타임, 저장소는 필요 없습니다.


1. 준비 (한 번)

  - 이 폴더를 통째로 바탕 화면이나 C:\PCA 같은 곳에 둡니다.
    기록이 이 폴더 안(collector\BlackBox\dumps)에 저장되므로, Program Files 처럼 쓰기가 막힌 곳은 안 됩니다.
  - Check.bat 을 실행합니다. 두 줄 모두 OK 가 나와야 합니다.
    FAIL 이 나오면 화면의 글을 그대로 이 묶음을 만든 사람에게 보내 주세요.


2. 실행

  - Start-Admin.bat   관리자 권한으로 시작합니다. Windows 가 묻는 창에서 [예] 를 누릅니다.
                      프로그램 시작·종료와 모듈 기록까지 남으므로 시연에는 이쪽을 씁니다.
  - Start.bat         일반 권한으로 시작합니다. 크래시 감지와 기록은 되지만 일부 항목은 "권한 필요"로 남습니다.

  앱에서 [수집 시작] 을 누르면 수집기가 돕니다.
  프로그램이 꺼지면 수집기가 알아서 기록하고, 앱이 그 기록의 검진 결과 화면으로 넘어갑니다.
  직접 기록하려면 Ctrl+Shift+F12 를 누릅니다.
  지난 기록은 [최근 기록 열기] 로, 예시 기록은 [파일 열기...] 로 엽니다 (engine\data\samples).


3. AI 설명

  - 검진 결과 화면의 [AI 설명...] 을 누릅니다.
  - 처음에는 [키 등록...] 으로 자기 Anthropic API 키를 넣습니다. 이 묶음에는 키가 들어 있지 않습니다.
    키는 이 PC 의 Windows 계정으로 암호화되어 %LOCALAPPDATA%\PcCrashAnalyzer\ai-key.bin 에 저장됩니다.
  - 창이 보낼 내용과 예상 요금을 먼저 보여 줍니다. [AI 에게 묻기] 를 눌러야 전송됩니다.
  - 인터넷이 필요합니다.


4. 시연이 끝난 뒤

  - 키를 지웁니다: AI 설명 창의 [키 지우기], 또는 tools\ce-ai\ce-ai.exe key delete
  - 이 폴더를 지웁니다. 그 밖에 남는 것은 %TEMP%\pc_crash_analyzer (검진 세션 파일) 뿐입니다.
  - 이 묶음은 시작 프로그램 등록, 서비스 설치, 레지스트리 변경을 하지 않습니다.
{DEMO_SECTION}

들어 있는 것

  app\                          앱 (CrashAnalyzerUI.exe)
  collector\BlackBox\bin\...    수집기 (BlackBox.exe). 읽기만 하고, 기록 전에는 디스크에 쓰지 않습니다.
  engine\build\Release\         규칙 엔진 (crash_engine.exe) 과 Microsoft Visual C++ 런타임 DLL
  engine\data\samples\          예시 기록
  tools\ce-ai\                  AI 설명의 명령 줄 판 (창이 안 될 때 쓰는 예비)
  package.json                  무엇으로 만들었는지와 만든 뒤의 자체 점검 결과

  폴더 이름이 저장소와 같은 것은 앱이 엔진과 수집기를 그 위치에서 찾기 때문입니다. 폴더 구조를 바꾸지 마세요.
