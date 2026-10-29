using StudyCore;

namespace BlackBox.Participant;

/// <summary>참여자에게 보이는 문구. 동의 문구를 바꾸면 ParticipantState.CurrentConsentVersion 을 올린다.</summary>
public static class Texts
{
    public const string AppTitle = "PC 블랙박스";
    public const string HotkeyText = "Ctrl+Shift+F12";

    /// <summary>엔진 증상 코드(snapshot_schema.md 1.2)와 참여자용 표현. 코드 null = 기타/모름.</summary>
    public static readonly (string? Code, string Label)[] Symptoms =
    {
        ("app_crash", "프로그램이 갑자기 꺼졌어요"),
        ("freeze", "프로그램이나 PC가 멈췄어요 (응답 없음)"),
        ("slow", "점점 느려졌어요"),
        ("fps_drop", "게임·영상이 끊기거나 버벅여요"),
        ("flicker", "화면이 깜빡이거나 검게 변했어요"),
        ("input_loss", "키보드·마우스가 안 먹혀요"),
        ("launch_fail", "프로그램이 안 켜져요"),
        ("network_fail", "인터넷이 끊겨요"),
        ("unexpected_reboot", "PC가 갑자기 꺼지거나 재부팅됐어요"),
        (null, "기타 / 잘 모르겠어요"),
    };

    public static string KindLabel(string kind) => kind switch
    {
        "crash-1000" or "crash-1001" => "프로그램 강제 종료",
        "hang-1002" or "hang-1001" => "프로그램 응답 없음",
        "bsod-1001" or "bsod-41" => "블루스크린 후 재시작",
        "power-41" => "PC 비정상 종료",
        "display-4101" => "화면 드라이버 복구",
        "kernel-live" => "그래픽·장치 드라이버 문제",
        "whea" => "하드웨어 오류 보고",
        "manual" => "직접 기록",
        _ => kind,
    };

    /// <summary>특정 프로그램의 사건인가(아니면 PC 전체). 시스템 사건의 subject("BugcheckCode 0" 등)는 프로그램 이름이 아니다.</summary>
    public static bool IsAppKind(string kind) => kind is "crash-1000" or "crash-1001" or "hang-1002" or "hang-1001";

    /// <summary>자동 감지 알림 본문.</summary>
    public static string AutoDetectedMessage(string kind, string? process) => kind switch
    {
        _ when IsAppKind(kind) => $"{process ?? "프로그램"} — 그 순간 상황을 기록했어요. 이 알림을 누르면 무슨 일이었는지 메모할 수 있어요(선택).",
        "power-41" or "bsod-41" or "bsod-1001" => "PC가 정상적으로 꺼지지 않은 기록을 찾아 남겼어요. 이 알림을 누르면 그때 무슨 일이었는지 메모할 수 있어요(선택).",
        _ => "그 순간 상황을 기록했어요. 이 알림을 누르면 무슨 일이었는지 메모할 수 있어요(선택).",
    };

    /// <summary>자동 감지 종류에서 미리 골라 둘 증상.</summary>
    public static string? SymptomForKind(string kind) => kind switch
    {
        "crash-1000" or "crash-1001" => "app_crash",
        "hang-1002" or "hang-1001" => "freeze",
        "bsod-1001" or "bsod-41" or "power-41" => "unexpected_reboot",
        "display-4101" or "kernel-live" => "flicker",
        _ => null,
    };

    public static string SymptomLabel(string? code) =>
        Symptoms.FirstOrDefault(s => s.Code == code).Label ?? code ?? "";

    public static string EndpointHost(StudyConfig s) => s.EndpointUri?.Host ?? "(설정 없음)";

    public static string Consent(StudyConfig s, string participantId) => $@"중앙대학교 캡스톤디자인 7조(정태웅·송민석·남건호)가 만든 연구용 프로그램입니다.
PC에서 프로그램이 꺼지거나 멈추거나 느려지는 순간의 ""주변 상황""을 기록해, 원인을 찾아 주는 엔진을 검증하는 데 씁니다.
완성된 제품이 아닌 시험판이며, 참여는 언제든 그만둘 수 있습니다.

■ 어떻게 동작하나요
· 평소에는 최근 5분의 PC 상태를 메모리에만 담아 두고 계속 덮어씁니다. 이때는 디스크에 쓰지도, 보내지도 않습니다.
· 프로그램 강제 종료·응답 없음, 블루스크린·비정상 종료, 화면 드라이버 복구처럼 Windows가 남기는 문제 기록이 생기면, 그 순간 전후(앞 5분, 뒤 30초)만 저장합니다.
· 느려짐처럼 Windows가 기록하지 않는 문제는 {HotkeyText} 를 누르거나, 작업 표시줄 오른쪽 아이콘에서 ""지금 문제 기록하기""를 눌러 주세요.
· 저장된 기록은 자동으로 암호화되어 연구팀에 전송됩니다. 파일을 따로 보내실 필요가 없습니다.
· 하루 두세 번, 프로그램 상태(정상 동작 여부, 기록 개수, 이 프로그램 자신의 CPU·메모리 사용량)도 함께 보냅니다.

■ 기록하는 것
· 실행 중인 프로그램 이름과 메모리·CPU 사용량, 문제 프로그램이 불러온 부품(DLL) 파일 경로
· Windows 이벤트 로그 중 오류·종료 관련 기록, 그래픽카드 온도·클럭 같은 센서 값
· 설치된 백신·보안 프로그램과 드라이버 이름, 최근 30일 안에 설치한 프로그램 이름
· 직접 고른 증상과 (적으셨다면) 메모, 그리고 아래에 적은 별명

■ 기록하지 않는 것
· 키보드 입력, 화면, 창 제목, 문서·사진 같은 파일 내용, 인터넷 사용 기록과 통신 내용
· 사용자 이름과 PC 이름은 저장할 때 %USERNAME%, %COMPUTERNAME% 기호로 바꾸고, 파일 경로의 사용자 폴더도 %USERPROFILE% 로 바꿉니다.

■ 보내는 곳과 보관
· 받는 곳: {EndpointHost(s)} (연구팀 Google 계정의 비공개 폴더)
· 기록은 이 PC에서 연구팀 공개키로 암호화한 뒤 보냅니다. 연구팀이 따로 가진 개인키 없이는 받는 서버를 포함해 누구도 열 수 없습니다.
· 연구 목적(원인 추론 엔진 검증)에만 쓰고, 발표·보고서에는 프로그램 이름과 수치만 씁니다.
· {s.RetentionUntil} 까지 보관한 뒤 모두 삭제합니다.
· 이름 대신 무작위 참여 ID로 구분합니다. 내 참여 ID: {participantId}

■ PC에 미치는 영향
· 관리자 권한을 요구하지 않습니다. 프로그램을 끄거나 설정·레지스트리를 바꾸지 않습니다(읽기 전용).
· 연구팀 PC 측정 기준 CPU 평균 0.3% 미만, 메모리 약 120MB를 씁니다.
· 기록은 이 PC의 %LOCALAPPDATA%\PcCrashAnalyzer 폴더에도 남으며, 오래된 것부터 지워 약 300MB 안으로 유지합니다.
· 자동 실행을 켜면 시작 프로그램 폴더에 바로가기 하나만 만듭니다.

■ 그만두기
· 작업 표시줄 아이콘 → ""참여 그만두기""를 누르면 자동 실행이 꺼지고, 원하시면 이 PC의 기록을 지우고 연구팀에 보낸 기록의 삭제를 요청합니다.
· ""전송 일시중지""로 보내기만 잠시 멈출 수도 있습니다.
· 문의: {s.Contact}";
}
