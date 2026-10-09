namespace BlackBox.Core;

public sealed record SourceStatus(string Name, string Status, string? Reason = null)
{
    public static SourceStatus Ok(string name, string? note = null) => new(name, "ok", note);
    public static SourceStatus Unavailable(string name, string reason) => new(name, "unavailable", reason);
}
