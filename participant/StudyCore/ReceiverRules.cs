using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StudyCore;

/// <summary>
/// 수신기 검증 규칙. participant/receiver/Code.gs(Google Apps Script)가 같은 규칙을 JavaScript 로 따른다 — 바꾸면 둘 다 바꾼다.
/// 수신기는 내용을 열 수 없으므로(개인키 없음) 형식·크기·토큰만 본다.
/// </summary>
public static class ReceiverRules
{
    /// <summary>요청 본문 최대 크기(문자 수). 클라이언트 묶음 상한(BundleBuilder.MaxBundleBytes)의 base64 팽창분을 넉넉히 덮는다.</summary>
    public const int MaxBodyChars = 10 * 1024 * 1024;

    public static readonly Regex PidRx = new("^[a-z0-9]{8,32}$");
    /// <summary>
    /// 봉투 이름은 앱이 만드는 세 형식만 받는다: 덤프 폴더(20261029-103025-crash-1000), 하트비트(hb-…), 철회(withdraw-…).
    /// 점이 없어 "."·".." 로 상위 폴더를 가리킬 수 없고, 숫자·접두어로 시작해 CON·NUL 같은 장치 이름도 될 수 없다(10/29 검토 지적).
    /// </summary>
    public static readonly Regex NameRx = new("^([0-9]{8}-[0-9]{6}-[a-z0-9-]{1,40}|hb-[0-9]{8}-[0-9]{6}|withdraw-[0-9]{8}-[0-9]{6})$");
    public static readonly Regex StudyRx = new("^[a-z0-9-]{3,32}$");
    private static readonly Regex B64Rx = new("^[A-Za-z0-9+/=]+$");

    /// <summary>오류 코드: empty, too_large, bad_json, bad_version, bad_token, bad_request. 수신기 응답 {"ok":false,"error":코드} 와 같다.</summary>
    public static (bool Ok, string? Error, EnvelopeHeader? Header) Validate(string? body, string expectedToken, string? expectedStudy = null)
    {
        if (string.IsNullOrEmpty(body)) return (false, "empty", null);
        if (body.Length > MaxBodyChars) return (false, "too_large", null);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { return (false, "bad_json", null); }
        using (doc)
        {
            var r = doc.RootElement;
            EnvelopeHeader h;
            try { h = Envelope.ReadHeader(r); }
            catch { return (false, "bad_version", null); }
            var token = r.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (!FixedEquals(token, expectedToken)) return (false, "bad_token", null);
            if (!StudyRx.IsMatch(h.Study) || (expectedStudy != null && h.Study != expectedStudy)) return (false, "bad_request", null);
            if (!PidRx.IsMatch(h.Pid) || !NameRx.IsMatch(h.Name) || !Envelope.Kinds.Contains(h.Kind)) return (false, "bad_request", null);
            if (!r.TryGetProperty("sigAlg", out var sa) || sa.ValueKind != JsonValueKind.String || sa.GetString() != Envelope.SigAlg)
                return (false, "bad_request", null);
            foreach (var f in new[] { "ek", "iv", "tag", "ct", "spk", "sig" })
            {
                if (!r.TryGetProperty(f, out var p) || p.ValueKind != JsonValueKind.String || !B64Rx.IsMatch(p.GetString() ?? ""))
                    return (false, "bad_request", null);
            }
            return (true, null, h);
        }
    }

    /// <summary>저장 파일 이름: &lt;kind&gt;_&lt;name&gt;_&lt;무작위8&gt;.pcae.json — 참여자 폴더(pid) 아래.</summary>
    public static string StoredFileName(EnvelopeHeader h, string? rand = null) =>
        $"{h.Kind}_{h.Name}_{rand ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant()}.pcae.json";

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}

/// <summary>참여 ID — 이름 대신 쓰는 무작위 12자. 헷갈리는 글자(0 o 1 l)는 뺐다.</summary>
public static class Ids
{
    private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    public static string NewParticipantId(int length = 12)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (int i = 0; i < length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }
}
