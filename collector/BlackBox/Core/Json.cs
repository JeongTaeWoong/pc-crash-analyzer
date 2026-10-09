using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlackBox.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static string Serialize(object? value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);
}
