using System.Net.Http;
using CrashAnalyzer.Ai;

namespace CrashAnalyzer.UI.Services;

// AI 설정 — 2026-11-16 추가, 2026-11-17 추가: 키 보관을 CrashAnalyzer.Ai 의 KeyStore 하나로 맞춤
//
//   * 11/14 결정 3: API 키는 제품에 넣지 않고 사용자가 자기 키를 등록해 쓴다.
//   * 키는 KeyStore(정태웅, c1bfc82)에만 둔다 — Windows 사용자 계정 암호화(DPAPI)로 %LOCALAPPDATA%\PcCrashAnalyzer\ai-key.bin.
//     그래서 'AI 설정' 창에서 넣은 키와 'AI 설명' 창의 [키 등록…]으로 넣은 키가 같은 키다.
//     (11/16 판은 Windows 자격 증명 관리자와 환경 변수를 썼는데, AI 설명 창이 그 키를 못 읽어서 뺐다.)
//   * 모델은 AI 설명 창에서 물을 때마다 고른다(예상 요금과 함께). 이 창에서는 고르지 않는다.
//   * 키가 없어도 엔진 진단은 그대로 동작한다.
public static class AiSettings
{
    /// <summary>저장된 키 (없거나 이 계정으로 풀 수 없으면 null)</summary>
    public static string? LoadKey() => KeyStore.Load();

    public static bool HasKey() => !string.IsNullOrEmpty(KeyStore.Load());

    /// <summary>입력한 키의 모양만 본다 (공백 제거 후). 문제가 없으면 null, 있으면 이유.</summary>
    public static string? CheckFormat(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "키를 입력해 주세요.";
        if (key.Contains(' ')) return "키 가운데에 띄어쓰기가 있어요. 복사할 때 다른 글자가 같이 들어왔는지 확인해 주세요.";
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Anthropic API 키는 'sk-ant-' 로 시작해요.";
        if (key.Length < 30) return "키가 너무 짧아요. 끝까지 복사했는지 확인해 주세요.";
        return null;
    }

    /// <summary>
    /// 모델 목록 조회(GET /v1/models)로 키가 맞는지 본다. 요금이 들지 않는 호출이다.
    /// AI 설명 창이 고를 수 있는 모델(Pricing.Models)을 이 키로 쓸 수 있는지도 같이 본다.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestKeyAsync(string key)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=100");
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            using var res = await http.SendAsync(req).ConfigureAwait(false);
            int code = (int)res.StatusCode;
            if (code == 200)
            {
                string body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var missing = new List<string>();
                for (int i = 0; i < Pricing.Models.Count; i++)
                    if (!body.Contains("\"" + Pricing.Models[i].Id + "\"", StringComparison.Ordinal))
                        missing.Add(Pricing.Models[i].Label);
                return missing.Count == 0
                    ? (true, "연결됐어요. AI 설명에서 고를 수 있는 모델을 모두 쓸 수 있어요.")
                    : (true, "키는 맞아요. 다만 이 키로는 보이지 않는 모델이 있어요: " + string.Join(", ", missing));
            }
            if (code == 401) return (false, "키가 맞지 않아요. 다시 복사해서 넣어 주세요.");
            if (code == 403) return (false, "이 키에는 권한이 없어요. Anthropic 콘솔에서 키 상태를 확인해 주세요.");
            if (code == 429) return (false, "요청이 너무 많아요. 잠시 뒤에 다시 확인해 주세요.");
            if (code >= 500) return (false, "Anthropic 서버에 문제가 있어요. 잠시 뒤에 다시 확인해 주세요. (" + code + ")");
            return (false, "확인하지 못했어요. (응답 " + code + ")");
        }
        catch (TaskCanceledException)
        {
            return (false, "응답이 없어요. 인터넷 연결을 확인해 주세요.");
        }
        catch (HttpRequestException)
        {
            return (false, "서버에 연결하지 못했어요. 인터넷 연결이나 방화벽을 확인해 주세요.");
        }
    }
}
