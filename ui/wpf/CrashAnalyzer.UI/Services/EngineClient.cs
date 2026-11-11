using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace CrashAnalyzer.UI.Services;

/// <summary>
/// crash_engine.exe의 도구 API(JSON stdin/stdout) 래퍼 — engine/docs/tool_api.md.
/// UI는 판단하지 않는다: 층·확률·순서·실행 방식은 전부 엔진 응답을 그대로 보여 준다.
/// </summary>
public sealed class EngineClient
{
    public string ExePath { get; }

    public EngineClient(string exePath)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("crash_engine.exe 없음", exePath);
        ExePath = exePath;
    }

    public JsonNode CallTool(string tool, JsonObject args, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
        };
        psi.ArgumentList.Add("tool");
        psi.ArgumentList.Add(tool);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("엔진 프로세스 시작 실패");
        var errTask = p.StandardError.ReadToEndAsync();
        using (var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            w.Write(args.ToJsonString());
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { /* 이미 종료됨 */ }
            throw new TimeoutException($"엔진 도구 '{tool}' 응답 시간 초과");
        }

        JsonNode root;
        try { root = JsonNode.Parse(stdout) ?? throw new FormatException("빈 응답"); }
        catch (Exception ex)
        {
            string err = errTask.IsCompleted ? errTask.Result : "";
            throw new InvalidOperationException($"엔진 응답 파싱 실패: {ex.Message}\n{Trunc(stdout)}\n{Trunc(err)}");
        }

        if (root["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(root["error"]?.GetValue<string>() ?? "엔진이 오류를 반환했습니다");
        return root["result"] ?? new JsonObject();
    }

    private static string Trunc(string s) => s.Length > 400 ? s[..400] + "..." : s;
}
