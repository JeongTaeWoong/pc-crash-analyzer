namespace CrashAnalyzer.Ai;

/// <summary>
/// 요금 계산(백만 토큰당 달러). 표는 평가 실험의 것(eval/pricing.py, 2026-11-03 기준)과 같다 — 실제 청구는 Anthropic
/// Console 이 기준이고, 여기 값은 사용자에게 "대략 얼마"를 미리 알리기 위한 것이다.
/// </summary>
public static class Pricing
{
    public sealed record ModelInfo(string Id, string Label, double InPerMillion, double OutPerMillion, string Note);

    public static readonly IReadOnlyList<ModelInfo> Models =
    [
        // Note 의 금액은 평가 실험에서 잰 값(개발용 사례, 요약 묶음 방식). Opus 는 그 방식으로 재지 않았다.
        new("claude-haiku-5-5", "Haiku 5.5 (빠르고 저렴)", 0.10, 0.50, "실험에서 잰 값: 진단 1회 약 0.002달러"),
        new("claude-sonnet-5-5", "Sonnet 5.5 (더 꼼꼼)", 2.00, 10.00, "실험에서 잰 값: 진단 1회 약 0.04달러"),
        new("claude-opus-5-5", "Opus 5.5 (가장 꼼꼼, 비쌈)", 4.00, 20.00, "실험에서 재지 않음 — 아래 어림을 보세요"),
    ];

    public static ModelInfo Of(string modelId) => Models.FirstOrDefault(m => m.Id == modelId) ?? Models[0];

    public static double CostUsd(string modelId, long inputTokens, long outputTokens)
    {
        var m = Of(modelId);
        return (inputTokens * m.InPerMillion + outputTokens * m.OutPerMillion) / 1_000_000.0;
    }

    /// <summary>보내기 전 어림: 답은 평가에서 1,100~3,250 토큰이었다(422개). 가운데쯤인 2,000 으로 잡는다.</summary>
    public static double EstimateUsd(string modelId, long inputTokens) => CostUsd(modelId, inputTokens, 2000);
}
