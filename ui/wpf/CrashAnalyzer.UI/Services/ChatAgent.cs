using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CrashAnalyzer.UI.Services;

// 상담 도우미(챗봇) 에이전트 (2026-11-02 시안 2-4).
//
// 에이전트는 나중에 서버로 띄울 계획이라(2026-10-15 결정) 클라이언트에는 연결 지점(IChatAgent)만 두고,
// 지금은 Python 시안 ce_agent.py 의 OfflineAgent 를 그대로 옮긴 규칙 기반 대역만 넣었다.
// 서버 에이전트가 생기면 IChatAgent 를 구현한 클래스 하나를 추가하고 ChatAgentFactory.Create 에서 고르면 된다.
//
// 판단은 똑같이 엔진이 한다. 에이전트는 사용자의 말을 엔진 도구 호출로 바꾸고, 결과를 문장으로 설명만 한다.

public interface IChatAgent
{
    /// <summary>상담 창 위쪽에 보이는 연결 설명</summary>
    string Name { get; }

    /// <summary>사용자 말 한 마디에 답한다. 부른 엔진 도구 이름은 calledTools 에 넣는다. (작업 스레드에서 불린다)</summary>
    string Ask(string userText, List<string> calledTools);
}

public static class ChatAgentFactory
{
    public static IChatAgent Create(SessionService session) => new OfflineChatAgent(session);
}

/// <summary>LLM 없이 정해진 표현("그대로예요", "왜요?", "적용했어요", "2시간 재발 없었어요")을 알아듣는 대역.</summary>
public sealed class OfflineChatAgent : IChatAgent
{
    private static readonly string[] WordsSolved = { "사라졌", "해결됐", "해결 됐", "안 튕", "안튕", "괜찮아", "멀쩡", "됐어요", "정상" };
    private static readonly string[] WordsNotSolved = { "그대로", "똑같", "여전히", "또 튕", "계속", "안 돼", "안돼", "아니요", "아니에요" };
    private static readonly string[] WordsSkipped = { "못 하", "못하", "할 수 없", "모르겠", "건너" };
    private static readonly string[] WordsWhy = { "왜", "근거", "이유" };
    private static readonly string[] WordsApplied = { "적용했", "바꿨", "설정했", "했어요", "했습니다" };
    private static readonly string[] WordsNoRecur = { "재발 없", "안 났", "안났", "문제없", "문제 없" };
    private static readonly string[] WordsRecur = { "다시 났", "재발", "또 났" };

    private readonly SessionService _session;

    public OfflineChatAgent(SessionService session) => _session = session;

    public string Name => "오프라인 대역 (정해진 표현만 이해 · 서버 에이전트 연결 전)";

    private static bool Has(string text, string[] words)
    {
        for (int i = 0; i < words.Length; i++)
            if (text.Contains(words[i])) return true;
        return false;
    }

    /// <summary>'2시간', '90분' 같은 표현에서 분을 뽑는다. 없으면 0.</summary>
    private static double Minutes(string text)
    {
        double total = 0;
        var h = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*시간");
        if (h.Success) total += double.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 60;
        var m = Regex.Match(text, @"(\d+(?:\.\d+)?)\s*분");
        if (m.Success) total += double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return total;
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static double D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    private static string Pct(double p) => (int)Math.Round(p * 100) + "%";

    private static string OutcomeText(string o) => o switch
    {
        "resolved" => "해결됨",
        "recurred" => "재발",
        "observing" => "관찰 중(시간이 더 필요)",
        "need_answer" => "사용자 응답 필요",
        _ => o,
    };

    /// <summary>다음 점검을 사람이 읽을 문장으로.</summary>
    public static string DescribeCheck(JsonNode nc)
    {
        var act = nc["action"];
        var lines = new List<string>();
        double sec = D(nc["cost_seconds"]);
        string took = sec / 60.0 >= 1 ? (int)Math.Round(sec / 60.0) + "분" : (int)sec + "초";
        lines.Add("다음 점검: " + S(nc["label"]) + " → " + S(nc["target"]));
        lines.Add("  (약 " + took + ", 이 점검으로 원인이 드러날 확률 " + Pct(D(nc["chance_to_reveal_cause"])) + ")");
        if (act?["steps"] is JsonArray steps)
            for (int i = 0; i < steps.Count; i++)
                lines.Add("  " + (i + 1) + ") " + S(steps[i]));
        if (S(act?["rollback"]).Length > 0) lines.Add("  되돌리기: " + S(act?["rollback"]));
        string exe = S(act?["execution"]);
        if (exe == "confirm") lines.Add("  ※ 실행 전에 동의가 필요한 조치입니다.");
        else if (exe == "manual") lines.Add("  ※ 직접 해야 하는 작업입니다.");
        lines.Add("해 보시고 '증상이 사라졌어요' / '그대로예요' / '못 하겠어요' 중 하나로 알려 주세요.");
        return string.Join("\n", lines);
    }

    public static string DescribeStatus(JsonNode v)
    {
        string status = S(v["status"]);
        var output = new List<string>();
        if (v["belief"] is JsonArray belief && belief.Count > 0)
        {
            var top = new List<string>();
            for (int i = 0; i < Math.Min(3, belief.Count); i++)
                top.Add(S(belief[i]?["label"]) + " " + Pct(D(belief[i]?["probability"])));
            output.Add("지금 원인 후보: " + string.Join(", ", top));
        }
        if (status == "checking" && v["next_check"] is JsonNode nc)
        {
            output.Add(DescribeCheck(nc));
        }
        else if (status == "confirmed")
        {
            output.Add("원인이 확인되었습니다: " + S(v["confirmed"]?["label"]));
            if (S(v["restore_after_check"]).Length > 0) output.Add("먼저: " + S(v["restore_after_check"]));
            string cur = S(v["current_remedy"]);
            if (v["remedies"] is JsonArray rem)
                for (int i = 0; i < rem.Count; i++)
                {
                    if (S(rem[i]?["remedy_id"]) != cur) continue;
                    output.Add("추천 해결책: " + S(rem[i]?["label"]) + " (기대 해결률 " + Pct(D(rem[i]?["expected_success"])) + ")");
                    if (rem[i]?["actions"] is JsonArray acts)
                        for (int k = 0; k < acts.Count; k++)
                            if (acts[k]?["steps"] is JsonArray st)
                                for (int s = 0; s < st.Count; s++)
                                    output.Add("  - " + S(st[s]));
                }
            output.Add("적용하셨으면 '적용했어요'라고 알려 주세요.");
        }
        else if (status == "fixing")
        {
            output.Add("해결책을 적용한 상태입니다. 한동안 써 본 뒤 '2시간 동안 재발 없었어요'처럼 알려 주세요.");
        }
        else if (status == "resolved")
        {
            output.Add("해결이 확인되었습니다.");
        }
        else if (status == "failed" || status == "exhausted")
        {
            output.Add(S(v["status_text"]) + " — " + S(v["next_action"]));
        }
        return string.Join("\n", output);
    }

    public string Ask(string userText, List<string> calledTools)
    {
        string text = userText.Trim();
        try
        {
            calledTools.Add("session_status");
            var v = _session.Status();
            string status = S(v["status"]);

            if (Has(text, WordsWhy))
            {
                string target = status != "checking" ? S(v["confirmed"]?["id"]) : "";
                calledTools.Add("explain");
                var ex = _session.Explain(target);
                var lines = new List<string> { (S(ex["label"]).Length > 0 ? S(ex["label"]) : "1순위 후보") + "을(를) 의심하는 근거:" };
                if (ex["reasons"] is JsonArray reasons)
                    for (int i = 0; i < Math.Min(5, reasons.Count); i++)
                        lines.Add("  · " + S(reasons[i]?["sentence"]));
                return string.Join("\n", lines);
            }

            if (status == "checking" && v["next_check"] is JsonNode nc)
            {
                string checkId = S(nc["check_id"]);
                string? result = null;
                if (Has(text, WordsSkipped)) result = "skipped";
                else if (Has(text, WordsNotSolved)) result = "not_solved";
                else if (Has(text, WordsSolved)) result = "solved";
                if (result is null)
                    return "증상을 이해했습니다. 엔진이 고른 순서대로 확인해 볼게요.\n" + DescribeStatus(v);
                calledTools.Add("session_report");
                var after = _session.Report(checkId, result);
                string head = result switch
                {
                    "solved" => "좋아요, 결과를 반영했습니다.",
                    "not_solved" => "알겠습니다. 이 후보의 확률을 낮추고 다음 점검을 골랐어요.",
                    _ => "건너뛰고 다음 점검으로 넘어갈게요.",
                };
                return head + "\n" + DescribeStatus(after);
            }

            if (status == "confirmed" && Has(text, WordsApplied))
            {
                calledTools.Add("session_apply_fix");
                var after = _session.ApplyFix();
                return "적용을 기록했습니다.\n" + DescribeStatus(after);
            }

            if (status == "fixing" && (Has(text, WordsNoRecur) || Has(text, WordsRecur)))
            {
                bool recurred = Has(text, WordsRecur) && !Has(text, WordsNoRecur);
                int mins = (int)Math.Round(Minutes(text));
                calledTools.Add("session_verify");
                var res = _session.VerifyAnswer(recurred, mins);
                var ver = res["verify"];
                var lines = new List<string> { "검증 결과: " + OutcomeText(S(ver?["outcome"])) };
                if (ver?["details"] is JsonArray det)
                    for (int i = 0; i < det.Count; i++)
                        lines.Add("  · " + S(det[i]));
                return string.Join("\n", lines) + "\n" + DescribeStatus(res["session"] ?? new JsonObject());
            }
            return DescribeStatus(v);
        }
        catch (Exception e)
        {
            return "엔진 오류: " + e.Message;
        }
    }
}
