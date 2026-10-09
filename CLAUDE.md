# PC Crash Analyzer — collector

## 프로젝트
Windows PC 크래시 원인을 증거 기반으로 추론하는 진단 도구. 이 저장소의 `collector/`는 "블랙박스 수집기":
상시 저부하 수집 → 메모리 링버퍼 → 단축키/자동 감지 시 마킹 시점 전후만 덤프.
설계 원문은 `docs/제안서.docx` 5장, 요약은 `README.md`.

## 스택
- C# / .NET 8, Windows 10/11 x64. 콘솔 앱으로 시작, UI는 나중.
- 라이브러리: Microsoft.Diagnostics.Tracing.TraceEvent (ETW), LibreHardwareMonitorLib (센서),
  System.Diagnostics.Eventing.Reader (이벤트 로그 구독), System.Management (WMI).
- 테스트: xUnit. 수집 결과는 JSON으로 덤프.

## 반드시 지킬 원칙
1. 실시간 "분석" 금지. 수집기는 수집·버퍼·덤프만 한다. 추론은 `engine/`의 몫.
2. 링버퍼는 메모리에만. 마킹 전에는 디스크에 아무것도 쓰지 않는다.
3. 자동 조치 금지. 프로세스 종료·서비스 중지·레지스트리 변경·설정 변경을 하는 코드를 쓰지 않는다. 읽기만.
4. 수집 실패는 예외로 죽지 말고 `{"status":"unavailable","reason":...}`로 기록한다. "안 되는 것"도 데이터다.
5. 관리자 권한이 필요한 항목은 권한 없을 때 건너뛰고 그 사실을 기록한다.
6. 개인정보: 파일 경로·사용자명은 수집하되 덤프 시 `%USERPROFILE%` 치환. 패킷 내용·키 입력 내용은 절대 수집하지 않는다.

## 작업 방식
- 매 단계 끝에 `collector/NOTES.md`에 "검증됨 / 미지원 / 권한 필요 / 미확인" 4분류로 신호별 상태를 갱신한다.
- 새 신호를 추가할 때는 먼저 5초짜리 스파이크 코드로 실제 값이 나오는지 확인한 뒤 본 코드에 넣는다.
- 커밋 메시지: `collector: <무엇을>`. `*.dmp`, `*.etl`, `logs/`, `dumps/`는 커밋하지 않는다.
- 모르는 Windows API는 추측하지 말고 "미확인"으로 표시하고 나에게 묻는다.