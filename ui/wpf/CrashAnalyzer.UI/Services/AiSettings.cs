using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CrashAnalyzer.UI.Services;

// AI 설정 — 2026-11-16 추가 (11/14 결정 3: API 키는 제품에 넣지 않고 사용자가 자기 키를 등록해 쓴다)
//
//   * 키는 Windows 자격 증명 관리자에만 저장한다 (docs/product-architecture-v2.md 3.4 의 "키 없는 빌드").
//     설정 파일·기록 파일·화면에는 앞 7글자와 끝 4글자만 보인다.
//   * 저장한 키가 없으면 환경 변수 ANTHROPIC_API_KEY 를 쓴다 (eval/ 실험 코드와 같은 이름).
//   * 키가 없어도 엔진 진단은 그대로 동작한다. AI 를 부르는 쪽(AI 호출 라이브러리)은 ResolveKey() 와 Load().Model 만 쓰면 된다.

/// <summary>키를 어디서 읽었는가</summary>
public enum ApiKeySource
{
    None,
    CredentialStore,      // Windows 자격 증명 관리자 (이 화면에서 저장한 키)
    EnvironmentVariable,  // ANTHROPIC_API_KEY
}

/// <summary>모델 하나 (이름은 eval/ 과 같은 API 모델 문자열)</summary>
public sealed class AiModelChoice
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>키가 아닌 설정 (모델 등). 위치: %LOCALAPPDATA%\PcCrashAnalyzer\settings\ai.json</summary>
public sealed class AiSettings
{
    public const string EnvVarName = "ANTHROPIC_API_KEY";
    public const string DefaultModel = "claude-haiku-5-5";

    /// <summary>고를 수 있는 모델. 첫 번째가 기본값 (개발용 사례에서 Haiku 정확도가 충분했고 가장 저렴함 — 11/14 기록)</summary>
    public static readonly AiModelChoice[] Models =
    {
        new() { Id = "claude-haiku-5-5", Label = "Claude Haiku 5.5 — 빠르고 저렴해요 (기본)" },
        new() { Id = "claude-sonnet-5-5", Label = "Claude Sonnet 5.5 — 균형" },
        new() { Id = "claude-opus-5-5", Label = "Claude Opus 5.5 — 가장 정확하지만 비싸요" },
    };

    public string Model { get; set; } = DefaultModel;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string SettingsPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCrashAnalyzer", "settings");
        return Path.Combine(dir, "ai.json");
    }

    public static AiSettings Load()
    {
        try
        {
            string p = SettingsPath();
            if (File.Exists(p))
            {
                var s = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(p), Options) ?? new AiSettings();
                if (!IsKnownModel(s.Model)) s.Model = DefaultModel;
                return s;
            }
        }
        catch (Exception)
        {
            // 망가진 설정 파일은 기본값으로 시작한다 (키는 이 파일에 없으므로 잃을 것이 없다)
        }
        return new AiSettings();
    }

    public void Save()
    {
        string p = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>"claude-haiku-5-5" → "Haiku 5.5" (버튼에 짧게 보일 이름)</summary>
    public static string ShortName(string id)
    {
        for (int i = 0; i < Models.Length; i++)
        {
            if (Models[i].Id != id) continue;
            string label = Models[i].Label;
            int dash = label.IndexOf(" —", StringComparison.Ordinal);
            if (dash > 0) label = label.Substring(0, dash);
            return label.Replace("Claude ", "");
        }
        return id;
    }

    public static bool IsKnownModel(string id)
    {
        for (int i = 0; i < Models.Length; i++)
            if (Models[i].Id == id) return true;
        return false;
    }

    // ═══════════════════════ 키 ═══════════════════════

    /// <summary>AI 를 부를 때 쓸 키. 저장한 키 → 환경 변수 순서. 없으면 null.</summary>
    public static string? ResolveKey(out ApiKeySource source)
    {
        string? key = ApiKeyStore.Load();
        if (!string.IsNullOrEmpty(key))
        {
            source = ApiKeySource.CredentialStore;
            return key;
        }
        key = Environment.GetEnvironmentVariable(EnvVarName);
        if (string.IsNullOrWhiteSpace(key))
            key = Environment.GetEnvironmentVariable(EnvVarName, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(key))
        {
            source = ApiKeySource.EnvironmentVariable;
            return key.Trim();
        }
        source = ApiKeySource.None;
        return null;
    }

    public static bool HasKey()
    {
        return ResolveKey(out _) != null;
    }

    /// <summary>입력한 키의 모양만 본다 (공백 제거 후). 문제가 없으면 null, 있으면 이유.</summary>
    public static string? CheckFormat(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "키를 입력해 주세요.";
        if (key.Contains(' ')) return "키 가운데에 띄어쓰기가 있어요. 복사할 때 다른 글자가 같이 들어왔는지 확인해 주세요.";
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Anthropic API 키는 'sk-ant-' 로 시작해요.";
        if (key.Length < 30) return "키가 너무 짧아요. 끝까지 복사했는지 확인해 주세요.";
        return null;
    }

    /// <summary>화면에 보일 가린 키: sk-ant-…abcd</summary>
    public static string Mask(string key)
    {
        if (key.Length <= 11) return "…";
        return key.Substring(0, 7) + "…" + key.Substring(key.Length - 4);
    }

    // ═══════════════════════ 연결 확인 ═══════════════════════

    /// <summary>
    /// 모델 목록 조회(GET /v1/models)로 키가 맞는지 본다. 요금이 들지 않는 호출이다 (collector/NOTES.md 11/12 확인).
    /// 고른 모델을 이 키로 쓸 수 있는지도 같이 본다.
    /// </summary>
    public static async Task<(bool Ok, string Message)> TestKeyAsync(string key, string model)
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
                bool hasModel = body.Contains("\"" + model + "\"", StringComparison.Ordinal);
                return hasModel
                    ? (true, "연결됐어요. 고른 모델을 쓸 수 있어요.")
                    : (true, "키는 맞아요. 다만 고른 모델이 이 키의 모델 목록에 보이지 않아요. 다른 모델을 골라 보세요.");
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

/// <summary>
/// Windows 자격 증명 관리자(제어판 → 자격 증명 관리자 → Windows 자격 증명 → 일반 자격 증명)에 키를 보관한다.
/// 이 PC 의 이 사용자 계정에서만 읽을 수 있다. 추가 패키지 없이 advapi32 를 직접 부른다.
/// </summary>
public static class ApiKeyStore
{
    public const string TargetName = "PcCrashAnalyzer/AnthropicApiKey";

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;   // 이 PC 에만 (로밍 안 함)
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public static void Save(string key)
    {
        byte[] blob = Encoding.Unicode.GetBytes(key);
        IntPtr mem = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, mem, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetName,
                Comment = "PC 크래시 검진 — AI 설명·상담에 쓰는 Anthropic API 키",
                CredentialBlobSize = blob.Length,
                CredentialBlob = mem,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "api-key",
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException("자격 증명 관리자에 저장하지 못했어요 (오류 " + Marshal.GetLastWin32Error() + ").");
        }
        finally
        {
            // 메모리에 남은 키를 지운다
            Marshal.Copy(new byte[blob.Length], 0, mem, blob.Length);
            Marshal.FreeHGlobal(mem);
            Array.Clear(blob, 0, blob.Length);
        }
    }

    /// <summary>저장된 키. 없거나 읽을 수 없으면 null.</summary>
    public static string? Load()
    {
        try
        {
            if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out IntPtr p)) return null;
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
                if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero) return null;
                byte[] blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                string key = Encoding.Unicode.GetString(blob);
                Array.Clear(blob, 0, blob.Length);
                return key.Length == 0 ? null : key;
            }
            finally
            {
                CredFree(p);
            }
        }
        catch (DllNotFoundException)
        {
            return null;   // Windows 가 아닌 곳 (리눅스 검사 빌드 등)
        }
    }

    /// <summary>지운다. 원래 없었으면 false.</summary>
    public static bool Delete()
    {
        try
        {
            if (CredDelete(TargetName, CRED_TYPE_GENERIC, 0)) return true;
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return false;
            throw new InvalidOperationException("자격 증명 관리자에서 지우지 못했어요 (오류 " + err + ").");
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }
}
