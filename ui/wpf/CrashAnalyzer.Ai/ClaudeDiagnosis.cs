using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace CrashAnalyzer.Ai;

/// <summary>한 번의 AI 진단 결과. Status 가 "ok" 가 아니면 Answer 는 없다 — 틀린 답이 아니라 "받지 못한 답"이다.</summary>
public sealed record AiResult(
    string Status,                 // ok | no_key | bad_key | refused | truncated | unparseable | rate_limited | network | api_error
    AiAnswer? Answer,
    IReadOnlyList<AdviceCheck.Finding> Findings,
    string Model,
    long InputTokens,
    long OutputTokens,
    double CostUsd,
    int Attempts,
    string? Detail = null);

/// <summary>
/// 사용자의 키로 Claude 에 한 번 묻는다. 평가 실험의 한 번 호출 방식(eval/run_single.py)과 같은 규칙:
///  - 보내는 것은 증거 묶음의 글과 고정된 지시문뿐이다. 원본 덤프, 파일 내용, 키 입력은 보내지 않는다.
///  - 답은 정해진 형식(JSON Schema)으로만 받는다.
///  - 길이 상한(4,000 토큰)에서 잘린 답은 한 번 다시 묻는다. 두 번째도 잘리면 "잘림"으로 돌려준다.
///  - 다른 모델로 바꿔 묻지 않는다. 거절은 거절로 돌려준다.
/// 키는 이 클래스 밖으로 나가지 않고, 예외 글에도 넣지 않는다.
/// </summary>
public static class ClaudeDiagnosis
{
    public const int MaxTokens = 4000;

    /// <summary>보내기 전 미리 보기용: 입력 토큰 수(무료 호출). 키가 맞는지도 이것으로 확인한다.</summary>
    public static async Task<(string Status, long InputTokens, string? Detail)> CountAsync(string? apiKey, string model, string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return ("no_key", 0, null);
        try
        {
            AnthropicClient client = new() { ApiKey = apiKey };
            var r = await client.Messages.CountTokens(new MessageCountTokensParams
            {
                Model = model,
                System = AiPrompt.System,
                Messages = [new UserMessageParam { Content = userText }],
            }, ct);
            return ("ok", r.InputTokens, null);
        }
        catch (Exception e) { var (status, detail) = Classify(e); return (status, 0, detail); }
    }

    public static async Task<AiResult> RunAsync(string? apiKey, string model, string userText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return Fail("no_key", model);
        AnthropicClient client = new() { ApiKey = apiKey };
        var request = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = AiPrompt.System,
            Messages = [new UserMessageParam { Content = userText }],
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = AiPrompt.AnswerSchema() } },
        };

        long inTok = 0, outTok = 0;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Message msg;
            try { msg = await client.Messages.Create(request, ct); }
            catch (Exception e) { var (status, detail) = Classify(e); return Fail(status, model, inTok, outTok, attempt, detail); }

            inTok += msg.Usage.InputTokens;
            outTok += msg.Usage.OutputTokens;
            string stop = msg.StopReason?.ToString() ?? "";
            if (stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase) || stop.Contains("MaxTokens", StringComparison.OrdinalIgnoreCase))
            {
                if (attempt == 1) continue;                       // 한 번 다시 묻는다
                return Fail("truncated", model, inTok, outTok, attempt);
            }
            if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase)) return Fail("refused", model, inTok, outTok, attempt);

            string? body = msg.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault();
            var answer = AiAnswer.Parse(body);
            if (answer is null) return Fail("unparseable", model, inTok, outTok, attempt);
            return new AiResult("ok", answer, AdviceCheck.Check(answer), model, inTok, outTok, Pricing.CostUsd(model, inTok, outTok), attempt);
        }
        return Fail("truncated", model, inTok, outTok, 2);
    }

    private static AiResult Fail(string status, string model, long inTok = 0, long outTok = 0, int attempts = 0, string? detail = null) =>
        new(status, null, [], model, inTok, outTok, Pricing.CostUsd(model, inTok, outTok), attempts, detail);

    /// <summary>예외를 사용자에게 말할 수 있는 종류로. 예외 글은 그대로 내보내지 않는다(요청 내용이 섞일 수 있다).</summary>
    private static (string Status, string? Detail) Classify(Exception e) => e switch
    {
        AnthropicUnauthorizedException => ("bad_key", null),
        AnthropicForbiddenException => ("bad_key", "이 키로는 이 모델을 쓸 수 없습니다"),
        AnthropicRateLimitException => ("rate_limited", null),
        AnthropicApiException a => ("api_error", a.GetType().Name),
        HttpRequestException or TaskCanceledException or IOException => ("network", e.GetType().Name),
        _ => ("api_error", e.GetType().Name),
    };

    public static string StatusText(string status) => status switch
    {
        "ok" => "답을 받았습니다",
        "no_key" => "API 키가 등록되어 있지 않습니다. 설정에서 자기 키를 넣어 주세요.",
        "bad_key" => "API 키가 맞지 않습니다. 키를 다시 확인해 주세요.",
        "refused" => "AI 가 이 요청에 답하지 않았습니다.",
        "truncated" => "답이 길이 상한에서 두 번 잘렸습니다. 다시 시도해 주세요.",
        "unparseable" => "답의 형식이 맞지 않아 쓸 수 없습니다. 다시 시도해 주세요.",
        "rate_limited" => "요청이 너무 잦습니다. 잠시 뒤에 다시 시도해 주세요.",
        "network" => "인터넷 연결을 확인해 주세요.",
        _ => "AI 서비스에서 오류가 났습니다. 잠시 뒤에 다시 시도해 주세요.",
    };
}
