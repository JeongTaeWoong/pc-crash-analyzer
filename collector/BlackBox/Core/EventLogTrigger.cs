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
    string Kind);
