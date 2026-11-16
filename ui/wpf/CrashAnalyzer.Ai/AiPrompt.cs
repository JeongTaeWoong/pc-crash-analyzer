using System.Reflection;
using System.Text.Json;

namespace CrashAnalyzer.Ai;

/// <summary>
/// 지시문과 답 형식. 글은 평가 실험(eval/prompts.py)에서 내보낸 파일을 그대로 싣는다(eval/export_prompt.py) —
/// 제품에서 따로 고쳐 쓰지 않는다. 고칠 일이 있으면 평가 쪽에서 판을 올리고 다시 내보낸다.
/// </summary>
public static class AiPrompt
{
    public static string System { get; } = Read("system-prompt.txt");
    public static string AnswerSchemaJson { get; } = Read("answer-schema.json");
    public static string PromptVersion { get; }
    public static string BundleVersion { get; }

    static AiPrompt()
    {
        using var doc = JsonDocument.Parse(Read("versions.json"));
        PromptVersion = doc.RootElement.GetProperty("prompt").GetString() ?? "";
        BundleVersion = doc.RootElement.GetProperty("bundle").GetString() ?? "";
    }

    /// <summary>답 형식(JSON Schema)을 SDK 가 받는 모양으로.</summary>
    public static Dictionary<string, JsonElement> AnswerSchema()
    {
        using var doc = JsonDocument.Parse(AnswerSchemaJson);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static string Read(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        string res = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s, global::System.Text.Encoding.UTF8);   // 'System' 은 이 클래스의 속성 이름이기도 하다
        return r.ReadToEnd();
    }
}
