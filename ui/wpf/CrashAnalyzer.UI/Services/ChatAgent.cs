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
    private static readonly string[] WordsOther = { "다른 방법", "다른 건", "다른거", "다른 거", "또 뭐", "어떻게 해", "뭘 해" };

    private readonly SessionService _session;
    private string _lastAnswer = "";   // 같은 답을 되풀이하지 않으려고 (2026-11-02 추가)

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

    /// <summary>후보 id → 표시 이름 (세션의 belief 목록에서 찾음)</summary>
    private static string CandidateLabel(JsonNode v, string id)
    {
        if (v["belief"] is JsonArray belief)
            for (int i = 0; i < belief.Count; i++)
                if (S(belief[i]?["id"]) == id) return S(belief[i]?["label"]);
        return id;
    }

    /// <summary>지금까지 한 점검을 사람이 읽을 줄로 ("Steam 오버레이 → 그대로")</summary>
    private static List<string> DoneChecks(JsonNode v)
    {
        var lines = new List<string>();
        if (v["history"] is not JsonArray hist) return lines;
        for (int i = 0; i < hist.Count; i++)
        {
            if (S(hist[i]?["type"]) != "check") continue;
            string reference = S(hist[i]?["ref"]);
            int at = reference.IndexOf('@');
            // 대상이 있는 점검은 대상 이름, 대상 없이 한꺼번에 하는 점검(group)은 점검 이름으로 (engine/data/rules.json 의 checks)
            string name = at >= 0 ? CandidateLabel(v, reference.Substring(at + 1)) : reference switch
            {
                "clean_boot" => "클린 부트(서드파티 전체 중지)",
                "hw_visual_check" => "하드웨어 일괄 육안 점검",
                _ => reference,
            };
            string result = S(hist[i]?["result"]) switch
            {
                "solved" => "증상 사라짐",
                "not_solved" => "그대로",
                "skipped" => "건너뜀",
                _ => S(hist[i]?["result"]),
            };
            lines.Add("  · " + name + " → " + result);
        }
        return lines;
    }

    private static string TopCandidates(JsonNode v, int n)
    {
        var top = new List<string>();
        if (v["belief"] is JsonArray belief)
            for (int i = 0; i < Math.Min(n, belief.Count); i++)
                top.Add(S(belief[i]?["label"]) + " " + Pct(D(belief[i]?["probability"])));
        return string.Join(", ", top);
    }

    /// <summary>엔진이 정한 점검을 모두 했는데 원인이 드러나지 않았을 때 할 수 있는 것</summary>
    public static string DescribeExhausted(JsonNode v)
    {
        var lines = new List<string>();
        var done = DoneChecks(v);
        lines.Add("엔진이 정한 점검 " + done.Count + "개를 모두 해 봤지만 원인이 드러나지 않았어요.");
        if (done.Count > 0)
        {
            lines.Add("해 본 점검:");
            lines.AddRange(done);
        }
        lines.Add("그래도 확률이 높게 남은 후보: " + TopCandidates(v, 3));

        var outOfScope = new List<string>();
        if (v["belief"] is JsonArray belief)
            for (int i = 0; i < belief.Count && outOfScope.Count < 3; i++)
                if (belief[i]?["in_focus"] is JsonValue f && f.TryGetValue<bool>(out bool inFocus) && !inFocus)
                    outOfScope.Add(S(belief[i]?["label"]));

        lines.Add("");
        lines.Add("이렇게 해 볼 수 있어요:");
        lines.Add("  1) 블랙박스 수집기를 켜 둔 채로 문제를 한 번 더 일으켜 보세요. 새 기록으로 다시 검진하면 근거가 늘어서 원인을 더 잘 가려요.");
        if (outOfScope.Count > 0)
            lines.Add("  2) 이번 점검 범위 밖이라 확인하지 않은 후보가 있어요: " + string.Join(", ", outOfScope) + ". 이것들을 잠시 끄거나 지운 뒤 증상이 나는지 직접 확인해 보세요.");
        else
            lines.Add("  2) 엔진이 생략한 층(드라이버·하드웨어)의 점검을 고려해 보세요. 예: 그래픽 드라이버 재설치, 온도·전원 확인.");
        lines.Add("  3) 증상이 다르게 느껴지면 [파일 열기]로 다시 검진하면서 증상 종류와 범위를 정확히 골라 주세요.");
        lines.Add("(지금 상담 도우미는 정해진 표현만 이해하는 오프라인 대역이에요. 자유로운 대화는 서버 에이전트가 연결된 뒤에 돼요.)");
        return string.Join("\n", lines);
    }

    /// <summary>준비된 해결책을 모두 써 봤는데 실패했을 때</summary>
    public static string DescribeFailed(JsonNode v)
    {
        var lines = new List<string> { "원인으로 확인된 '" + S(v["confirmed"]?["label"]) + "'에 준비된 해결책을 모두 써 봤지만 문제가 남았어요." };
        if (v["remedies"] is JsonArray rem && rem.Count > 0)
        {
            lines.Add("써 본 해결책:");
            for (int i = 0; i < rem.Count; i++)
                lines.Add("  · " + S(rem[i]?["label"]) + " (기대 해결률 " + Pct(D(rem[i]?["expected_success"])) + ")");
        }
        lines.Add("");
        lines.Add("이렇게 해 볼 수 있어요:");
        lines.Add("  1) 해당 프로그램을 최신 버전으로 업데이트하거나, 잠시 지운 뒤 증상이 나는지 확인해 보세요.");
        lines.Add("  2) 블랙박스 수집기를 켜 둔 채로 다시 일어나면 새 기록으로 검진해 보세요. 다른 원인이 함께 있을 수 있어요.");
        lines.Add("  3) 제조사 고객센터에 문의할 때는 [블랙박스 기록] 페이지의 기록을 함께 보여 주면 도움이 돼요.");
        return string.Join("\n", lines);
    }

    /// <summary>"다른 방법은 없어요?" — 지금 상태에서 고를 수 있는 다른 길</summary>
    public static string DescribeOtherWays(JsonNode v)
    {
        string status = S(v["status"]);
        var lines = new List<string>();
        if (status == "checking")
        {
            lines.Add("지금 점검을 하기 어렵다면 '할 수 없어요'라고 알려 주세요. 엔진이 다음 점검으로 넘어가요.");
            lines.Add("지금 남은 후보: " + TopCandidates(v, 3));
        }
        else if (status == "confirmed" || status == "fixing")
        {
            string cur = S(v["current_remedy"]);
            lines.Add("이 원인에 준비된 해결책이에요 (기대 해결률 순):");
            if (v["remedies"] is JsonArray rem)
                for (int i = 0; i < rem.Count; i++)
                    lines.Add("  · " + S(rem[i]?["label"]) + " " + Pct(D(rem[i]?["expected_success"])) +
                              (S(rem[i]?["remedy_id"]) == cur ? "  ← 지금 추천" : ""));
            lines.Add("추천 해결책을 적용했는데 다시 나면 '재발했어요'라고 알려 주세요. 다음 해결책으로 넘어가요.");
        }
        else if (status == "resolved")
        {
            lines.Add("이미 해결이 확인됐어요. 다시 문제가 나면 수집기 기록으로 새로 검진해 주세요.");
        }
        else
        {
            return DescribeStatus(v);
        }
        return string.Join("\n", lines);
    }

    public string Ask(string userText, List<string> calledTools)
    {
        string answer = AskInner(userText, calledTools);
        // 상태가 그대로라 같은 답이 또 나오면, 같은 글을 반복하지 않고 짧게 알려 준다
        if (answer == _lastAnswer)
            answer = "상태가 바뀌지 않아서 바로 위의 안내와 같아요.\n" +
                     "위에 적힌 방법 중 하나를 해 보신 뒤 결과를 알려 주시거나, '왜 이게 원인이에요?' / '다른 방법은 없어요?' 로 물어봐 주세요.";
        else
            _lastAnswer = answer;
        return answer;
    }

    private string AskInner(string userText, List<string> calledTools)
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

            // 점검을 다 해도 원인을 못 찾았을 때 / 해결책이 모두 실패했을 때 (2026-11-02 추가)
            if (status == "exhausted")
            {
                string head = Has(text, WordsNotSolved)
                    ? "더 해 볼 점검이 남아 있지 않아서 '그대로'는 반영할 곳이 없어요. 대신 이렇게 해 볼 수 있어요.\n\n"
                    : "";
                return head + DescribeExhausted(v);
            }
            if (status == "failed")
                return DescribeFailed(v);

            if (Has(text, WordsOther))
                return DescribeOtherWays(v);

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
