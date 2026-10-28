namespace BlackBox.Core;

/// <summary>자동 마킹을 발동시킨 이벤트 로그 레코드의 구조화 정보. Message는 발동 이벤트 원문(최대 1000자).</summary>
public sealed record EventLogTrigger(
    DateTimeOffset ReceivedAt,
    string Log,
    int EventId,
    string? Provider,
    DateTimeOffset? EventTime,
    string Message,
    string? ProcessName,
    int? Pid,
    string? FaultingModule,
    string Kind,
    string? WerEventType,        // 1001: APPCRASH / RADAR_PRE_LEAK_64 / LiveKernelEvent …
    string? ExceptionCode,       // 1000 [6]: c0000005 등
    string? FaultingModulePath,  // 1000 [11]: 오류 모듈 전체 경로 (서드파티 판정 근거)
    string? ReportId,            // 1000 [12] / 1002 [6]
    TriggerClass Class);
