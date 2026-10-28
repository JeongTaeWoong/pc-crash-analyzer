namespace BlackBox.Core;

/// <summary>이벤트 로그 레코드 하나를 "트리거 종류"로 분류한 결과. Kind가 null이면 트리거가 아니다(버퍼에만 기록).</summary>
public sealed record TriggerClass(
    string? Kind,          // crash-1000 / crash-1001 / hang-1002 / hang-1001 / kernel-live / bsod-1001 / power-41 / display-4101 / whea
    string SymptomType,    // 엔진 스키마 symptom.type
    string Scope,          // 엔진 스키마 symptom.scope
    int BaseScore,         // 유의성 기본 점수
    string Note)
{
    public bool IsTrigger => Kind != null;
}

/// <summary>
/// 트리거 1단계 분류 (규칙, 비용 0). 2026-10-28 30일 재분류에서 확인된 사실을 반영한다:
/// 1001(WER)은 "보고서가 생겼다"는 이벤트라 유형(event name)별로 갈라야 하고, 절반은 크래시가 아니다.
/// 순수 함수 — xUnit으로 검증한다 (BlackBox.Tests).
/// </summary>
public static class TriggerClassifier
{
    public const string BugCheckProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";

    // WER 1001 중 크래시가 아닌 보고 유형(접두 일치). 버퍼에는 남기되 트리거하지 않는다.
    private static readonly string[] NonCrashWerPrefixes =
    {
        "RADAR_PRE_LEAK",                 // 메모리 누수 진단 보고
        "RADAR_",                         // 기타 RADAR 진단
        "StoreAgentScanForUpdatesFailure",// 스토어 업데이트 실패
        "crashpad_log",                   // Chromium crashpad 로그 업로드
        "FaultTolerantHeap",              // FTH 적용 알림
        "WindowsUpdateFailure",
        "ScriptedDiagFailure",
        "PnPDriverImportError",
        "PnPDriverNotFound",
        "PnPGenericDriverFound",
    };

    private static readonly string[] CrashWerPrefixes = { "APPCRASH", "CLR20r3", "BEX", "MoAppCrash", "MoBEX" };
    private static readonly string[] HangWerPrefixes = { "AppHang", "MoAppHang" };

    public static TriggerClass Classify(string log, int eventId, string? provider, string? werEventType)
    {
        switch (log, eventId)
        {
            case ("Application", 1000):
                return new("crash-1000", "app_crash", "single_app", 30, "Application Error");
            case ("Application", 1002):
                return new("hang-1002", "freeze", "single_app", 60, "Application Hang");
            case ("Application", 1001):
                return ClassifyWer(werEventType);
            case ("System", 41):
                return new("power-41", "unexpected_reboot", "system_wide", 90, "Kernel-Power 비정상 종료");
            case ("System", 1001) when provider == BugCheckProvider:
                return new("bsod-1001", "unexpected_reboot", "system_wide", 90, "BugCheck(BSOD) 기록");
            case ("System", 4101):
                return new("display-4101", "flicker", "system_wide", 70, "디스플레이 드라이버 복구(TDR)");
            case ("System", >= 17 and <= 20) when provider == WheaProvider:
                return new("whea", "freeze", "system_wide", 90, "WHEA 하드웨어 오류");
            default:
                return new(null, "unknown", "unknown", 0, "구독 대상 아님");
        }
    }

    private static TriggerClass ClassifyWer(string? werEventType)
    {
        string t = werEventType ?? "";
        if (t.StartsWith("LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
            return new("kernel-live", "freeze", "system_wide", 90, "커널 라이브 덤프(GPU TDR 등 시스템급)");
        if (NonCrashWerPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new(null, "unknown", "unknown", 0, $"WER 비크래시 보고({t}) — 버퍼에만 기록");
        if (HangWerPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new("hang-1001", "freeze", "single_app", 60, "WER 응답 없음 보고 — 1002와 중복 제거 대상");
        if (CrashWerPrefixes.Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return new("crash-1001", "app_crash", "single_app", 30, "WER 크래시 보고 — 1000과 중복 제거 대상");
        if (t.Length == 0)
            return new("crash-1001", "app_crash", "single_app", 20, "WER 유형 없음");
        return new("crash-1001", "app_crash", "single_app", 20, $"WER 미분류 유형({t}) — 보수적으로 트리거, 낮은 점수");
    }
}
