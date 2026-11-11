using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BlackBox.Core;

/// <summary>
/// 덤프 직전 개인정보 치환 (CLAUDE.md 6).
/// 1) 사용자 프로필 경로 → %USERPROFILE% (원문 텍스트 전체, JSON 이스케이프 형태 포함, 8.3 짧은 경로 포함).
///    프로필 뒤에 이름이 이어지면 치환하지 않는다 — 사용자 kim 일 때 C:\Users\kimberly 가 "%USERPROFILE%berly" 가 되지 않게(10/29 검토).
/// 2) v0.7: 사용자 이름 → %USERNAME%, 컴퓨터 이름 → %COMPUTERNAME% — JSON 문자열 "값"에서만(키·숫자는 그대로).
///    - 3자 이상이고 흔한 낱말이 아니면: 낱말 경계 어디서나
///    - 짧거나(예: "민수") 흔한 낱말이면: 경로 조각·도메인\이름 형태일 때만 (앞이 \ 또는 /, 뒤가 \ / 또는 끝)
///    JSON 으로 읽을 수 없으면 원문 전체에 같은 치환을 한다(치환을 건너뛰지 않는다).
/// </summary>
public static class Sanitizer
{
    // 주의: 정적 필드는 적힌 순서대로 초기화된다 — CommonWords 가 정규식보다 먼저 와야 한다(10/29 자체 점검에서 TypeInitializationException 으로 발견)
    /// <summary>JSON 키나 일반 낱말과 겹치는 이름 — 어디서나 치환하면 데이터가 망가지므로 경로 조각일 때만 치환한다.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "users", "admin", "administrator", "owner", "guest", "default", "public", "test", "name",
        "home", "pc", "system", "local", "windows", "microsoft", "desktop", "laptop", "computer",
    };

    private static readonly string Profile = Safe(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private static readonly string ProfileShort = Safe(() => ShortPath(Profile));
    private static readonly string UserName = Safe(() => Environment.UserName);
    private static readonly string MachineName = Safe(() => Environment.MachineName);
    private static readonly Regex? UserRx = NameRegex(UserName);
    private static readonly Regex? MachineRx = NameRegex(MachineName);

    public static string SanitizeJson(string json) => SanitizeJson(json, new[] { Profile, ProfileShort }, UserRx, MachineRx);

    /// <summary>테스트용: 프로필·사용자·컴퓨터 이름을 직접 준다.</summary>
    public static string SanitizeJson(string json, string profile, string? userName, string? machineName, string? profileShort = null) =>
        SanitizeJson(json, new[] { profile, profileShort ?? "" }, NameRegex(userName), NameRegex(machineName));

    private static string SanitizeJson(string json, string[] profiles, Regex? userRx, Regex? machineRx)
    {
        foreach (var p in profiles) json = ReplaceProfile(json, p);
        if (userRx == null && machineRx == null) return json;
        string Fix(string s)
        {
            if (userRx != null) s = userRx.Replace(s, "%USERNAME%");
            if (machineRx != null) s = machineRx.Replace(s, "%COMPUTERNAME%");
            return s;
        }
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return Fix(json); } // JSON 이 아니면 원문 전체에 — 치환을 건너뛰지 않는다
        if (root == null) return json;
        bool changed = false;
        var fixedRoot = Walk(root, s =>
        {
            var r = Fix(s);
            if (r != s) changed = true;
            return r;
        });
        if (!changed) return json;
        return fixedRoot!.ToJsonString(json.Contains('\n') ? Json.Indented : Json.Compact);
    }

    /// <summary>JSON 이 아닌 짧은 글(운영 로그 등)용 — 프로필 경로와 이름을 원문 전체에서 치환한다.</summary>
    public static string SanitizeText(string text)
    {
        text = ReplaceProfile(ReplaceProfile(text, Profile), ProfileShort);
        if (UserRx != null) text = UserRx.Replace(text, "%USERNAME%");
        if (MachineRx != null) text = MachineRx.Replace(text, "%COMPUTERNAME%");
        return text;
    }

    private static string ReplaceProfile(string text, string profile)
    {
        if (string.IsNullOrEmpty(profile)) return text;
        // JSON 안의 백슬래시는 \\ 로 이스케이프되어 있으므로 그 형태를 먼저,
        // 혹시 모를 원형(슬래시 경로 포함)도 함께 치환한다. 대소문자 무시. 뒤에 이름 글자가 이어지면(다른 사용자 폴더) 치환하지 않는다.
        foreach (var variant in new[]
                 {
                     profile.Replace("\\", "\\\\"),      // C:\\Users\\name (JSON 이스케이프)
                     profile,                             // C:\Users\name
                     profile.Replace("\\", "/"),          // C:/Users/name
                 })
        {
            text = Regex.Replace(text, Regex.Escape(variant) + NameContinues, "%USERPROFILE%", RegexOptions.IgnoreCase);
        }
        return text;
    }

    /// <summary>프로필 경로 뒤에 사용자 이름 글자가 더 이어지지 않아야 한다.</summary>
    private const string NameContinues = @"(?![\p{L}\p{N}_.\-~$])";

    private static Regex? NameRegex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (name.Length >= 3 && !CommonWords.Contains(name))
            // 낱말 경계: 앞뒤가 글자·숫자·밑줄이 아니어야 한다 ("user32.dll" 안의 "user" 는 치환하지 않는다)
            return new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(name) + @"(?![\p{L}\p{N}_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // 짧거나 흔한 이름: 경로 조각 또는 도메인\이름 형태일 때만 (D:\work\민수\a.txt, DESKTOP-X\민수)
        return new Regex(@"(?<=[\\/])" + Regex.Escape(name) + @"(?=[\\/]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static JsonNode? Walk(JsonNode? node, Func<string, string> fix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    var replaced = Walk(child, fix);
                    if (!ReferenceEquals(replaced, child)) obj[key] = replaced;
                }
                return obj;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    var replaced = Walk(child, fix);
                    if (!ReferenceEquals(replaced, child)) arr[i] = replaced;
                }
                return arr;
            case JsonValue val when val.TryGetValue<string>(out var s):
                var r = fix(s);
                return r == s ? val : JsonValue.Create(r);
            default:
                return node;
        }
    }

    /// <summary>8.3 짧은 경로(C:\Users\KIMCHE~1). 짧은 이름이 꺼져 있거나 같으면 빈 문자열.</summary>
    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var sb = new StringBuilder(512);
        uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
        if (n == 0 || n >= sb.Capacity) return "";
        var s = sb.ToString();
        return string.Equals(s, path, StringComparison.OrdinalIgnoreCase) ? "" : s;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    private static string Safe(Func<string> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }
}
