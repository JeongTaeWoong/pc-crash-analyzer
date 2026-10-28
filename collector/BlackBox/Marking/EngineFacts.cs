namespace BlackBox.Marking;

/// <summary>
/// 마킹 스냅샷에서 뽑아낸, 엔진 스키마(engine/docs/snapshot_schema.md v1.1) 변환용 구조화 팩트.
/// snapshot.json(사람/디버깅용 덤프)과 별도로, engine-snapshot.json 생성에 쓰인다.
/// </summary>
public sealed class EngineFacts
{
    public List<string> KeyboardUpper { get; } = new();
    public List<string> MouseUpper { get; } = new();
    public List<string> Minifilters { get; } = new();
    public List<(string Name, string Path)> NonStandardDrivers { get; } = new();
    public List<(string Name, string Date, string? Version)> RecentInstalls { get; } = new();
    public List<string> SecurityProducts { get; } = new();
}

public sealed record SnapshotResult(Dictionary<string, object?> Json, EngineFacts Facts, int ForegroundPid = -1);
