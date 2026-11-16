// ce-ai — AI 진단 라이브러리의 명령 줄 창구.
//   ce-ai bundle  <engine-snapshot.json>                       보낼 글(증거 묶음)만 출력한다. 아무것도 보내지 않는다.
//   ce-ai count   <engine-snapshot.json> [--model ID]          입력 토큰 수와 예상 요금(무료 호출). 키 확인용이기도 하다.
//   ce-ai ask     <engine-snapshot.json> [--model ID] [--json 파일]   한 번 묻는다(요금 발생). 답과 실제 사용량을 출력한다.
//   ce-ai key set | key show | key delete                      자기 API 키 등록(입력은 화면에 보이지 않는다) · 끝 네 글자 확인 · 삭제
// 키는 등록한 것(KeyStore)을 쓰고, 없으면 환경 변수 ANTHROPIC_API_KEY 를 쓴다(개발용). 키는 어디에도 출력하지 않는다.
using System.Text;
using System.Text.Json;
using CrashAnalyzer.Ai;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }
if (args.Length == 0) { Console.Error.WriteLine("usage: ce-ai bundle|count|ask <engine-snapshot.json> [--model ID] [--json FILE]   |   ce-ai key set|show|delete"); return 2; }

string Arg(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
string? Key() => KeyStore.Load() ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY", EnvironmentVariableTarget.User);

if (args[0] == "key")
{
    string sub = args.Length > 1 ? args[1] : "show";
    if (sub == "set")
    {
        Console.Write("API 키(입력은 보이지 않습니다): ");
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        if (sb.Length < 20) { Console.Error.WriteLine("키가 너무 짧습니다. 저장하지 않았습니다."); return 1; }
        KeyStore.Save(sb.ToString());
        Console.WriteLine($"저장했습니다: {KeyStore.Masked(sb.ToString())}  ({KeyStore.DefaultPath})");
        return 0;
    }
    if (sub == "delete") { KeyStore.Delete(); Console.WriteLine("등록된 키를 지웠습니다."); return 0; }
    Console.WriteLine(KeyStore.Exists() ? $"등록된 키: {KeyStore.Masked(KeyStore.Load())}" : "등록된 키가 없습니다.");
    return 0;
}

if (args.Length < 2 || !File.Exists(args[1])) { Console.Error.WriteLine("스냅샷 파일을 찾을 수 없습니다."); return 2; }
using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
var bundle = EvidenceBundle.Build(doc.RootElement);
string user = EvidenceBundle.UserMessage(bundle.Text);
string model = Arg("--model", "claude-haiku-5-5");

switch (args[0])
{
    case "bundle":
        Console.WriteLine(user);
        return 0;
    case "count":
    {
        var (status, tokens, detail) = await ClaudeDiagnosis.CountAsync(Key(), model, user);
        if (status != "ok") { Console.Error.WriteLine($"{status}: {ClaudeDiagnosis.StatusText(status)} {detail}"); return 1; }
        Console.WriteLine($"모델 {model} · 증거 항목 {bundle.Ids.Count}개 · 입력 {tokens:N0} 토큰 · 예상 요금 약 {Pricing.EstimateUsd(model, tokens):0.0000} 달러 (지시문 {AiPrompt.PromptVersion}, 묶음 {AiPrompt.BundleVersion})");
        return 0;
    }
    case "ask":
    {
        var r = await ClaudeDiagnosis.RunAsync(Key(), model, user);
        Console.WriteLine($"상태 {r.Status} · 모델 {r.Model} · 입력 {r.InputTokens:N0} 출력 {r.OutputTokens:N0} 토큰 · {r.CostUsd:0.0000} 달러 · 시도 {r.Attempts}회 {r.Detail}");
        if (r.Answer is null) { Console.Error.WriteLine(ClaudeDiagnosis.StatusText(r.Status)); return 1; }
        Console.WriteLine($"판정: {r.Answer.VerdictText}");
        foreach (var (c, i) in r.Answer.Candidates.Select((c, i) => (c, i)))
            Console.WriteLine($"  후보 {i + 1}. {c.Name} ({c.Kind}, 확신 {c.Confidence}) 근거 [{string.Join(",", c.EvidenceIds)}]\n         {c.Reason}");
        foreach (var (a, i) in r.Answer.Actions.Select((a, i) => (a, i)))
        {
            Console.WriteLine($"  조치 {i + 1}. [{a.Level}] {a.Action} — 대상: {a.Target}\n         {a.Why}");
            foreach (var f in r.Findings.Where(f => f.ActionIndex == i)) Console.WriteLine($"         ⚠ 권고 검사({f.Flag}): {f.Why}");
        }
        Console.WriteLine($"설명: {r.Answer.Explanation}");
        int bad = r.Answer.Candidates.SelectMany(c => c.EvidenceIds).Count(id => !bundle.Ids.Contains(id));
        Console.WriteLine($"인용한 근거 번호 중 실제로 보낸 항목에 없는 것: {bad}개");
        if (Arg("--json", "") is { Length: > 0 } outPath)
            File.WriteAllText(outPath, JsonSerializer.Serialize(new { r.Status, r.Model, r.InputTokens, r.OutputTokens, r.CostUsd, r.Attempts, answer = r.Answer, findings = r.Findings.Select(f => new { f.ActionIndex, flag = f.Flag.ToString(), f.Why }) },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }
    default:
        Console.Error.WriteLine("알 수 없는 명령입니다.");
        return 2;
}
