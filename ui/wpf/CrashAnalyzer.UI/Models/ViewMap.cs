using System.Text.Json.Nodes;

namespace CrashAnalyzer.UI.Models;

// 엔진 JSON 응답 → 화면 바인딩용 POCO. UI는 값을 계산하지 않고 변환만 한다.

public sealed class EvidenceVm
{
    public string Text { get; init; } = "";
}

public sealed class LayerBarVm
{
    public string Name { get; init; } = "";
    public int Pct { get; init; }
    public string PctText => Pct + "%";
    public double BarWidth => Math.Max(2, Pct * 2.2);
}

public sealed class CandidateVm
{
    public int Rank { get; init; }
    public string Label { get; init; } = "";
    public string Layer { get; init; } = "";
    public int ProbPct { get; init; }
    public int ConfPct { get; init; }
    public bool InFocus { get; init; }
    public int Stage { get; init; }
    public string Header => $"{Rank}. {Label}  [{Layer}] {ProbPct}% · 신뢰 {ConfPct}%" + (InFocus ? "" : $"  ({Stage}차 범위)");
    public List<EvidenceVm> Evidence { get; init; } = new();
}

public sealed class StepVm
{
    public int Index { get; init; }
    public string Label { get; init; } = "";
    public string Target { get; init; } = "";
    public int ChancePct { get; init; }
    public int CostSeconds { get; init; }
    public string Execution { get; init; } = "guide";
    public string Advice { get; init; } = "";
    public List<string> ActionSteps { get; init; } = new();
    public string Rollback { get; init; } = "";
    public string Header => $"{Index}) {Label} → {Target}";
    public string Meta => $"비용 {CostSeconds}초 · 이 단계에서 원인이 드러날 확률 {ChancePct}% · {ExecutionText}";
    public string ExecutionText => Execution switch
    {
        "auto" => "자동 실행",
        "confirm" => "동의 후 실행",
        "manual" => "직접 작업",
        _ => "안내",
    };
}

public sealed class DiagnoseVm
{
    public string SnapshotId { get; init; } = "";
    public string Symptom { get; init; } = "";
    public string Target { get; init; } = "";
    public string Mode { get; init; } = "guide";
    public string LayerTop { get; init; } = "";
    public int LayerConfidencePct { get; init; }
    public List<LayerBarVm> LayerBars { get; init; } = new();
    public List<EvidenceVm> LayerEvidence { get; init; } = new();
    public List<CandidateVm> Candidates { get; init; } = new();
    public string PlanSummary { get; init; } = "";
    public List<StepVm> Steps { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public string HeaderLine => $"증상: {Symptom} / 대상: {(string.IsNullOrEmpty(Target) ? "-" : Target)} / 모드: {(Mode == "auto" ? "자동 점검" : "안내")}";
    public string LayerLine => $"층 판정: {LayerTop}  ·  신뢰도 {LayerConfidencePct}%";
}

public sealed class BeliefVm
{
    public string Label { get; init; } = "";
    public int ProbPct { get; init; }
    public bool InFocus { get; init; }
    public string Text => $"{Label}  {ProbPct}%" + (InFocus ? "" : "  (범위 밖)");
    public double BarWidth => Math.Max(2, ProbPct * 2.2);
}

public sealed class RemedyVm
{
    public string Id { get; init; } = "";
    public int SuccessPct { get; init; }
    public bool IsCurrent { get; init; }
    public List<string> Actions { get; init; } = new();
    public string Header => (IsCurrent ? "▶ " : "") + $"{Id}  (기대 해결률 {SuccessPct}%)";
}

public sealed class SessionVm
{
    public string Status { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string NextAction { get; init; } = "";
    public string RestoreNote { get; init; } = "";
    public List<BeliefVm> Belief { get; init; } = new();
    public bool HasNextCheck { get; init; }
    public string CheckId { get; init; } = "";
    public string CheckLabel { get; init; } = "";
    public string CheckAdvice { get; init; } = "";
    public int CheckChancePct { get; init; }
    public string CheckExecutionText { get; init; } = "";
    public List<string> CheckSteps { get; init; } = new();
    public string CheckRollback { get; init; } = "";
    public string ConfirmedLabel { get; init; } = "";
    public List<EvidenceVm> ConfirmedEvidence { get; init; } = new();
    public List<RemedyVm> Remedies { get; init; } = new();
    public string LastVerifyText { get; init; } = "";
    public List<string> History { get; init; } = new();
}

public static class ViewMap
{
    private static int Pct(JsonNode? n) => (int)Math.Round((n?.GetValue<double>() ?? 0) * 100);
    private static string S(JsonNode? n) => n?.GetValue<string>() ?? "";
    private static List<EvidenceVm> Evidence(JsonNode? arr)
    {
        var list = new List<EvidenceVm>();
        if (arr is JsonArray a)
            foreach (var e in a)
            {
                double contrib = e?["contribution"]?.GetValue<double>() ?? 0;
                list.Add(new EvidenceVm { Text = $"· {S(e?["detail"])}  (기여 {contrib:0.00})" });
            }
        return list;
    }
    private static List<string> Strings(JsonNode? arr)
    {
        var list = new List<string>();
        if (arr is JsonArray a) foreach (var e in a) if (e != null) list.Add(e.GetValue<string>());
        return list;
    }

    public static DiagnoseVm FromDiagnose(JsonNode r)
    {
        var layer = r["layer"];
        var bars = new List<LayerBarVm>();
        if (layer?["probability"] is JsonObject probs)
            foreach (var kv in probs.OrderByDescending(k => k.Value?.GetValue<double>() ?? 0))
                bars.Add(new LayerBarVm { Name = kv.Key, Pct = Pct(kv.Value) });

        var cands = new List<CandidateVm>();
        if (r["candidates"] is JsonArray ca)
        {
            int rank = 1;
            foreach (var c in ca)
                cands.Add(new CandidateVm
                {
                    Rank = rank++,
                    Label = S(c?["label"]),
                    Layer = S(c?["layer"]),
                    ProbPct = Pct(c?["probability"]),
                    ConfPct = Pct(c?["confidence"]),
                    InFocus = c?["in_focus"]?.GetValue<bool>() ?? true,
                    Stage = (int)(c?["stage"]?.GetValue<double>() ?? 1),
                    Evidence = Evidence(c?["evidence"]),
                });
        }

        var steps = new List<StepVm>();
        string planSummary = "";
        var plan = r["plan"];
        if (plan != null)
        {
            double trials = plan["expected_trials"]?.GetValue<double>() ?? 0;
            double mins = plan["expected_cost_minutes"]?.GetValue<double>() ?? 0;
            planSummary = $"원인 확인 순서 (기대 시도 {trials:0.0}회 · 기대 비용 {mins:0.0}분)";
            if (plan["steps"] is JsonArray sa)
            {
                int i = 1;
                foreach (var s in sa)
                {
                    var action = s?["action"];
                    steps.Add(new StepVm
                    {
                        Index = i++,
                        Label = S(s?["label"]),
                        Target = S(s?["target"]),
                        ChancePct = Pct(s?["covered_probability"]),
                        CostSeconds = (int)(s?["cost_seconds"]?.GetValue<double>() ?? 0),
                        Execution = S(action?["execution"]) is { Length: > 0 } ex ? ex : "guide",
                        Advice = S(s?["advice"]),
                        ActionSteps = Strings(action?["steps"]),
                        Rollback = S(action?["rollback"]),
                    });
                }
            }
        }

        return new DiagnoseVm
        {
            SnapshotId = S(r["snapshot_id"]),
            Symptom = S(r["symptom"]),
            Target = S(r["target_process"]),
            Mode = S(r["policy"]?["mode"]),
            LayerTop = S(layer?["top"]),
            LayerConfidencePct = Pct(layer?["confidence"]),
            LayerBars = bars,
            LayerEvidence = Evidence(layer?["evidence"]),
            Candidates = cands,
            PlanSummary = planSummary,
            Steps = steps,
            Notes = Strings(r["notes"]).Concat(Strings(r["warnings"])).ToList(),
        };
    }

    public static SessionVm FromSessionView(JsonNode v)
    {
        var belief = new List<BeliefVm>();
        if (v["belief"] is JsonArray ba)
            foreach (var b in ba)
                belief.Add(new BeliefVm
                {
                    Label = S(b?["label"]),
                    ProbPct = Pct(b?["probability"]),
                    InFocus = b?["in_focus"]?.GetValue<bool>() ?? true,
                });

        var check = v["next_check"];
        var action = check?["action"];

        var remedies = new List<RemedyVm>();
        string current = S(v["current_remedy"]);
        if (v["remedies"] is JsonArray ra)
            foreach (var rm in ra)
            {
                var acts = new List<string>();
                if (rm?["actions"] is JsonArray aa)
                    foreach (var a in aa)
                        acts.Add("· " + (S(a?["label"]) is { Length: > 0 } l ? l : a?.ToJsonString() ?? ""));
                remedies.Add(new RemedyVm
                {
                    Id = S(rm?["remedy_id"]),
                    SuccessPct = Pct(rm?["expected_success"]),
                    IsCurrent = S(rm?["remedy_id"]) == current,
                    Actions = acts,
                });
            }

        string lastVerify = "";
        if (v["last_verify"] is JsonObject lv)
            lastVerify = $"판정: {S(lv["outcome"])} · 재발 {lv["recurrences"]?.GetValue<double>() ?? 0}건 · 관찰 {lv["observed_minutes"]?.GetValue<double>() ?? 0}분";

        var history = new List<string>();
        if (v["history"] is JsonArray ha)
            foreach (var h in ha)
                history.Add(h is JsonValue ? S(h) : h?.ToJsonString() ?? "");

        string execText = S(action?["execution"]) switch
        {
            "auto" => "자동 실행",
            "confirm" => "동의 후 실행",
            "manual" => "직접 작업",
            _ => "안내",
        };

        return new SessionVm
        {
            Status = S(v["status"]),
            StatusText = S(v["status_text"]),
            NextAction = S(v["next_action"]),
            RestoreNote = S(v["restore_after_check"]),
            Belief = belief,
            HasNextCheck = check is not null,
            CheckId = S(check?["check_id"]),
            CheckLabel = S(check?["label"]),
            CheckAdvice = S(check?["advice"]),
            CheckChancePct = Pct(check?["chance_to_reveal_cause"]),
            CheckExecutionText = execText,
            CheckSteps = Strings(action?["steps"]),
            CheckRollback = S(action?["rollback"]),
            ConfirmedLabel = S(v["confirmed"]?["label"]),
            ConfirmedEvidence = Evidence(v["confirmed"]?["evidence"]),
            Remedies = remedies,
            LastVerifyText = lastVerify,
            History = history,
        };
    }
}
