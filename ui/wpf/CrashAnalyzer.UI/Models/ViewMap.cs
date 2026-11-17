using System.Text.Json.Nodes;
using System.Windows.Media;

namespace CrashAnalyzer.UI.Models;

// 엔진 JSON 응답 → 화면 표시용 도우미. UI는 값을 계산하지 않고 변환·표시만 한다.
// (2026-11-02) 시안 2-4 화면은 MainWindow 가 엔진 JSON 을 바로 읽어 그린다. DiagnoseVm 은 --selftest 가 쓴다.

internal static class Ui
{
    // 2026-11-02 시안 2-4 색 (App.xaml 토큰과 같은 값)
    public static readonly Brush Accent = Hex("#0B7A55");
    public static readonly Brush Accent2 = Hex("#7FB59B");
    public static readonly Brush AccentSoft = Hex("#ECF6F2");
    public static readonly Brush Purple = Hex("#6D4FC4");
    public static readonly Brush AmberBar = Hex("#C97A2B");
    public static readonly Brush Track = Hex("#E3ECE7");
    public static readonly Brush DimFg = Hex("#4A5750");
    public static readonly Brush FaintFg = Hex("#9AA59F");
    public static readonly Brush StrongFg = Hex("#17201B");
    public static readonly Brush Red = Hex("#C0392B");
    public static readonly Brush Amber = Hex("#8A5300");
    public static readonly Brush GuideBg = Hex("#EDEDED");
    public static readonly Brush GuideFg = Hex("#3B3B3B");
    public static readonly Brush AutoBg = Hex("#DFF6E3");
    public static readonly Brush AutoFg = Hex("#0E6B2F");
    public static readonly Brush ConfirmBg = Hex("#FFF1D6");
    public static readonly Brush ConfirmFg = Hex("#8A5300");
    public static readonly Brush ManualBg = Hex("#FDE7E9");
    public static readonly Brush ManualFg = Hex("#A4262C");

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
        "DRIVER" => "드라이버",
        "HW" => "하드웨어",
        _ => layer,
    };

    /// <summary>후보의 범위(stage) 표시. 0 = 범위 밖.
    /// 2026-11-17 추가: 엔진의 새 분류 app_self(꺼진 프로그램 자신의 문제)는 stage 0 이어도 '범위 밖'이 아니라
    /// '프로그램 자체 문제'로 보인다 (11/12 목표가 원인 프로그램 찾기이고, 프로그램 자체 버그는 정답 사례라서).</summary>
    public static string StageKo(int stage, string category = "") => category == "app_self" ? "프로그램 자체 문제" : stage switch
    {
        1 => "1차 보안",
        2 => "2차 드라이버",
        3 => "3차 하드웨어",
        _ => "범위 밖",
    };

    public static string SymptomKo(string type) => type switch
    {
        "app_crash" => "프로그램 튕김",
        "freeze" => "멈춤(프리징)",
        "fps_drop" => "프레임 저하",
        "flicker" => "화면 깜빡임",
        "input_loss" => "입력 누락",
        "launch_fail" => "실행 안 됨",
        "network_fail" => "네트워크 끊김",
        "unexpected_reboot" => "갑자기 재부팅",
        _ => type,
    };

    /// <summary>점검 결과를 사람이 읽을 말로.</summary>
    public static string ResultKo(string result) => result switch
    {
        "solved" => "증상 사라짐",
        "not_solved" => "그대로 → 원인 아님",
        "skipped" => "건너뜀",
        _ => result,
    };

    /// <summary>검증 결과(outcome) 글자와 색.</summary>
    public static (string Text, Brush Fg) OutcomeStyle(string outcome) => outcome switch
    {
        "resolved" => ("해결됨", AutoFg),
        "recurred" => ("재발", ManualFg),
        "observing" => ("관찰 중", ConfirmFg),
        "need_answer" => ("사용자 응답 필요", ConfirmFg),
        _ => (outcome, StrongFg),
    };

    public static (string Text, Brush Bg, Brush Fg) ExecPill(string execution) => execution switch
    {
        "auto" => ("자동 가능", AutoBg, AutoFg),
        "confirm" => ("동의 후 실행", ConfirmBg, ConfirmFg),
        "manual" => ("직접 작업", ManualBg, ManualFg),
        _ => ("안내", GuideBg, GuideFg),
    };

    public static string CostText(int seconds) =>
        seconds >= 60 ? $"{seconds / 60}분" + (seconds % 60 > 0 ? $" {seconds % 60}초" : "") : $"{seconds}초";

    /// <summary>걸리는 시간: 60초 미만은 초, 1시간 미만은 분(반올림), 그 이상은 시간(소수 1자리).</summary>
    public static string DurationText(double seconds)
    {
        if (seconds < 60) return (int)seconds + "초";
        double minutes = seconds / 60.0;
        if (minutes < 60) return (int)Math.Round(minutes) + "분";
        return (minutes / 60.0).ToString("0.0") + "시간";
    }

    public static string Pct(double p) => (int)Math.Round(p * 100) + "%";

    // 2026-11-17 추가: 확률을 말로. 처음에는 50%·20% 기준(높음/보통/낮음)이었는데, 1순위가 43% 일 때
    // '보통'으로 보여 헷갈려서 순위로 말하도록 바꿨다(사용자 결정, 바뀔 수 있음).
    //   1순위 → '가장 의심돼요', 나머지 → 20% 이상 '의심돼요', 그 밖 '가능성 낮음'. 엔진 확률은 바꾸지 않는다.
    public static string Suspicion(bool isFirst, double p) =>
        isFirst ? "가장 의심돼요" : (p >= 0.2 ? "의심돼요" : "가능성 낮음");

    /// <summary>순위 막대 옆에 붙일 짧은 말</summary>
    public static string SuspicionShort(bool isFirst, double p) =>
        isFirst ? "가장 의심" : (p >= 0.2 ? "의심" : "낮음");

    public static string SuspicionTip(bool isFirst, double p) =>
        "엔진이 계산한 확률 " + Pct(p) + ". " + (isFirst
            ? "후보 중 1순위라 '가장 의심돼요'로 보여 줘요. 아직 점검으로 확인된 원인은 아니에요."
            : "1순위가 아닌 후보는 20% 이상이면 '의심돼요', 그 밖은 '가능성 낮음'으로 보여 줘요.");

    public static string Short(string text, int limit) =>
        text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
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
    public string Category { get; init; } = "";   // 2026-11-17 추가
    public string ProbText => ProbPct + "%";
    public string RangeText => Category == "app_self" ? "프로그램 자체 문제" : (InFocus ? $"{Stage}차 범위" : "범위 밖");
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
        $"증상 {Symptom}   ·   대상 {(string.IsNullOrEmpty(Target) ? "-" : Target)}   ·   {(Mode == "auto" ? "자동으로" : "직접")}";
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
                    Category = S(c?["category"]),
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
}
