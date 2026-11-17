using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrashAnalyzer.Ai;

/// <summary>AI 가 낸 답(답 형식 그대로). 화면은 이것을 보여 주기만 한다.</summary>
public sealed record AiAnswer(
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("candidates")] IReadOnlyList<AiCandidate> Candidates,
    [property: JsonPropertyName("recommended_actions")] IReadOnlyList<AiAction> Actions,
    [property: JsonPropertyName("explanation_for_user")] string Explanation)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    /// <returns>형식이 맞지 않으면 null — 틀린 답이 아니라 "쓸 수 없는 답"으로 따로 다룬다.</returns>
    public static AiAnswer? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var a = JsonSerializer.Deserialize<AiAnswer>(json, Options);
            return a is { Verdict: not null, Candidates: not null, Actions: not null, Explanation: not null } ? a : null;
        }
        catch (JsonException) { return null; }
    }

    public string VerdictText => Verdict switch
    {
        "culprit_found" => "원인을 찾았습니다",
        "no_problem_found" => "문제를 찾지 못했습니다",
        "insufficient_evidence" => "자료만으로는 원인을 특정할 수 없습니다",
        _ => Verdict,
    };
}

public sealed record AiCandidate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record AiAction(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("why")] string Why);
