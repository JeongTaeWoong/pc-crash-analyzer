using System.Security.Principal;
using System.Text;
using System.Text.Json;
using BlackBox.Core;

namespace BlackBox.Marking;

/// <summary>dumps/&lt;timestamp&gt;/ 아래 meta.json, events.json, snapshot.json을 쓴다. 디스크 쓰기는 여기가 유일하다.</summary>
public sealed class DumpWriter
{
    private readonly string _dumpRoot;

    public DumpWriter(string dumpRoot) => _dumpRoot = dumpRoot;

    public string Write(
        DateTimeOffset markTime,
        string kind,
        object trigger,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<SignalEvent> events,
        object snapshot,
        (int Count, long ApproxBytes) bufferStats,
        IReadOnlyList<SourceStatus> sourceStatuses)
    {
        string baseName = $"{markTime:yyyyMMdd-HHmmss}-{kind}";
        string dir = Path.Combine(_dumpRoot, baseName);
        for (int i = 2; Directory.Exists(dir); i++)
            dir = Path.Combine(_dumpRoot, $"{baseName}-{i}");
        Directory.CreateDirectory(dir);

        bool isElevated;
        using (var identity = WindowsIdentity.GetCurrent())
            isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        var meta = new
        {
            collector = "BlackBox",
            version = "0.1",
            os = Environment.OSVersion.VersionString,
            isElevated,
            markTime,
            kind,
            trigger,
            window = new { from, to },
            sources = sourceStatuses,
            bufferAtDump = new { eventCount = bufferStats.Count, approxBytes = bufferStats.ApproxBytes },
            eventsInDump = events.Count,
        };

        WriteJson(Path.Combine(dir, "meta.json"), Json.Serialize(meta, indented: true));
        WriteJson(Path.Combine(dir, "events.json"), BuildEventsJson(markTime, from, to, events));
        WriteJson(Path.Combine(dir, "snapshot.json"), Json.Serialize(snapshot, indented: true));
        return dir;
    }

    private static string BuildEventsJson(DateTimeOffset markTime, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<SignalEvent> events)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("markTime", markTime);
            writer.WriteString("from", from);
            writer.WriteString("to", to);
            writer.WriteNumber("count", events.Count);
            writer.WriteStartArray("events");
            foreach (var e in events)
            {
                writer.WriteStartObject();
                writer.WriteString("t", e.Timestamp);
                writer.WriteString("source", e.Source);
                writer.WritePropertyName("payload");
                writer.WriteRawValue(e.PayloadJson);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteJson(string path, string json) =>
        File.WriteAllText(path, Sanitizer.SanitizeJson(json), Encoding.UTF8);
}
