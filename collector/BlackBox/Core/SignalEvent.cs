namespace BlackBox.Core;

/// <summary>버퍼에 들어가는 단일 신호 이벤트. payload는 생성 시 1회 직렬화해 보관한다.</summary>
public sealed record SignalEvent(DateTimeOffset Timestamp, string Source, string PayloadJson)
{
    /// <summary>근사 메모리 점유량(바이트). string은 UTF-16이므로 길이×2 + 객체 오버헤드.</summary>
    public int ApproxBytes => PayloadJson.Length * 2 + 96;

    public static SignalEvent Create(string source, object payload) =>
        new(DateTimeOffset.Now, source, Json.Serialize(payload));
}
