using System.Text.Json.Nodes;
using System.Windows.Media;

namespace CrashAnalyzer.UI.Models;

// 엔진 JSON 응답 → 화면 바인딩용 POCO. UI는 값을 계산하지 않고 변환·표시만 한다.

internal static class Ui
{
    public static readonly Brush Accent = Hex("#2563C7");
    public static readonly Brush Purple = Hex("#6D4FC4");
    public static readonly Brush AmberBar = Hex("#C97A2B");
    public static readonly Brush Track = Hex("#ECF0F5");
    public static readonly Brush DimFg = Hex("#5C6A80");
    public static readonly Brush StrongFg = Hex("#1C2740");
    public static readonly Brush GuideBg = Hex("#ECF0F5");
    public static readonly Brush AutoBg = Hex("#E8EFFB");
    public static readonly Brush AutoFg = Hex("#2563C7");
    public static readonly Brush ConfirmBg = Hex("#F9EFDE");
    public static readonly Brush ConfirmFg = Hex("#B9741F");
    public static readonly Brush ManualBg = Hex("#FAE7E8");
    public static readonly Brush ManualFg = Hex("#C2434A");

    public static Brush Hex(string s) => (Brush)new BrushConverter().ConvertFromString(s)!;

    public static Brush LayerBrush(string layer) => layer switch
    {
        "SW" => Accent,
        "DRIVER" => Purple,
        "HW" => AmberBar,
        _ => DimFg,
    };

    public static string LayerKo(string layer) => layer switch
    {
        "SW" => "소프트웨어",
        "DRIVER" => "드라이버·시스템",
        "HW" => "하드웨어",
        _ => layer,
    };

    public static (string Text, Brush Bg, Brush Fg) ExecPill(string execution) => execution switch
    {
        "auto" => ("자동 실행", AutoBg, AutoFg),
        "confirm" => ("동의 후 실행", ConfirmBg, ConfirmFg),
        "manual" => ("직접 작업", ManualBg, ManualFg),
        _ => ("안내", GuideBg, DimFg),
    };

    public static string CostText(int seconds) =>
        seconds >= 60 ? $"{seconds / 60}분" + (seconds % 60 > 0 ? $" {seconds % 60}초" : "") : $"{seconds}초";
}

public sealed class EvidenceVm
{
    public string Text { get; init; } = "";
}

public sealed class LayerBarVm
{
    public string Layer { get; init; } = "";
    public string Name => Ui.LayerKo(Layer);
    public int Pct { get; init; }
    public string PctText => Pct + "%";
    public double FillWidth => Math.Max(2, Pct * 1.8); // 트랙 180 기준
    public Brush Fill => Ui.LayerBrush(Layer);
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
    public string ProbText => ProbPct + "%";
    public string RangeText => InFocus ? $"{Stage}차 범위" : "범위 밖";
    public string SubText => $"{Ui.LayerKo(Layer)} · {RangeText} · 신뢰 {ConfPct}%";
    public double RowOpacity => InFocus ? 1.0 : 0.55;
    public Brush RankBg => InFocus ? Ui.AutoBg : Ui.GuideBg;
    public Brush RankFg => InFocus ? Ui.AutoFg : Ui.DimFg;
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
    public string Title => $"{Label} → {Target}";
    public string CostText => Ui.CostText(CostSeconds);
    public string ChanceText => ChancePct + "%";
    public string ExecText => Ui.ExecPill(Execution).Text;
    public Brush ExecBg => Ui.ExecPill(Execution).Bg;
    public Brush ExecFg => Ui.ExecPill(Execution).Fg;
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

    public string MetaLine =>
        $"증상 {Symptom}   ·   대상 {(string.IsNullOrEmpty(Target) ? "-" : Target)}   ·   {(Mode == "auto" ? "자동 점검 모드" : "안내 모드")}";
    public string ConfidenceLine => $"판정 신뢰도 {LayerConfidencePct}%  (수집된 항목이 적으면 낮아집니다)";

    public string SummaryText
    {
        get
        {
            var c1 = Candidates.FirstOrDefault();
            if (c1 is null) return "후보가 없습니다.";
            var lines = new List<string>
            {
                $"1순위: {c1.Label}",
                $"{c1.RangeText} · 확률 {c1.ProbPct}% · 신뢰 {c1.ConfPct}%",
            };
            var hw = LayerBars.FirstOrDefault(b => b.Layer == "HW");
            if (hw != null && hw.Pct <= 10)
                lines.Add($"하드웨어 점검은 생략을 권합니다 (HW 확률 {hw.Pct}%).");
            if (Candidates.Count > 12)
                lines.Add($"후보 {Candidates.Count}개 중 상위 12개를 표시합니다.");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed class BeliefVm
{
    public string Label { get; init; } = "";
    public int ProbPct { get; init; }
    public bool InFocus { get; init; }
    public string Text => $"{Label}  ·  {ProbPct}%" + (InFocus ? "" : "  (범위 밖)");
    public double FillWidth => Math.Max(2, ProbPct * 1.6);
    public Brush Fill => InFocus ? Ui.Accent : Ui.DimFg;
    public double RowOpacity => InFocus ? 1.0 : 0.6;
}

public sealed class RemedyVm
{
    public string Id { get; init; } = "";
    public int SuccessPct { get; init; }
    public bool IsCurrent { get; init; }
    public List<string> Actions { get; init; } = new();
    public string Title => Id;
    public string SubText => $"기대 해결률 {SuccessPct}%" + (IsCurrent ? "  ·  현재 추천" : "");
    public System.Windows.FontWeight TitleWeight => IsCurrent ? System.Windows.FontWeights.SemiBold : System.Windows.FontWeights.Normal;
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
    public string CheckExecution { get; init; } = "guide";
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
                list.Add(new EvidenceVm { Text = $"•  {S(e?["detail"])}   (기여 {contrib:0.00})" });
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
                bars.Add(new LayerBarVm { Layer = kv.Key, Pct = Pct(kv.Value) });

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
            planSummary = $"엔진이 정한 원인 확인 순서  —  기대 시도 {trials:0.0}회 · 기대 비용 {mins:0.0}분";
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
                        acts.Add("•  " + (S(a?["label"]) is { Length: > 0 } l ? l : a?.ToJsonString() ?? ""));
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
            CheckExecution = S(action?["execution"]) is { Length: > 0 } ex ? ex : "guide",
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
