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

/// <summary>수동 마킹에 사용자가 지정한 증상(엔진 symptom.type)과 대상 프로그램(…exe). 둘 다 선택.</summary>
public sealed record ManualMark(string? Symptom, string? Target, string? Memo = null)
{
    /// <summary>엔진 스키마의 증상 값(snapshot_schema.md 1.2).</summary>
    public static readonly string[] KnownSymptoms =
        { "app_crash", "freeze", "fps_drop", "flicker", "input_loss", "launch_fail", "network_fail", "unexpected_reboot", "slow" };

    /// <summary>콘솔 입력 "m", "m slow", "m blender", "m slow blender" 를 해석한다. 첫 토큰이 알려진 증상이면 증상, 나머지는 대상.</summary>
    public static ManualMark Parse(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? symptom = null, target = null;
        int i = 0;
        if (tokens.Length > 0 && KnownSymptoms.Contains(tokens[0], StringComparer.OrdinalIgnoreCase))
        {
            symptom = tokens[0].ToLowerInvariant();
            i = 1;
        }
        if (tokens.Length > i) target = string.Join(' ', tokens.Skip(i));
        return new ManualMark(symptom, target);
    }
}
