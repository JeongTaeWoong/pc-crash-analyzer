using System.Security.Cryptography;
using System.Text;

namespace CrashAnalyzer.Ai;

/// <summary>
/// 사용자가 등록한 자기 API 키의 보관. 키는 제품에 들어 있지 않다(2026-11-14 결정) — 사용자가 설정에서 한 번 넣는다.
/// Windows 의 사용자별 암호화(DPAPI, CurrentUser)로 감싸 %LOCALAPPDATA%\PcCrashAnalyzer\ai-key.bin 에 둔다:
/// 같은 PC 의 같은 Windows 계정으로만 풀 수 있고, 파일을 다른 PC 로 옮기면 쓸 수 없다.
/// 키는 로그·화면·오류 글에 남기지 않는다. 화면에는 끝 네 글자만 보인다.
/// </summary>
public static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PcCrashAnalyzer.ai-key.v1");

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("CE_AI_KEY_FILE") is { Length: > 0 } p
            ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCrashAnalyzer", "ai-key.bin");

    public static void Save(string apiKey, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] plain = Encoding.UTF8.GetBytes(apiKey.Trim());
        try { File.WriteAllBytes(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <returns>저장된 키가 없거나 이 계정으로 풀 수 없으면 null.</returns>
    public static string? Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (CryptographicException) { return null; }
    }

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static void Delete(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>화면에 보일 모양: 끝 네 글자만.</summary>
    public static string Masked(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? "(없음)" : "····" + (apiKey.Length > 4 ? apiKey[^4..] : "");
}
