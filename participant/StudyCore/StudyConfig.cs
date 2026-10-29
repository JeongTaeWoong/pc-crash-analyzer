using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StudyCore;

/// <summary>
/// 연구 배포 설정. 팀 PC 에서는 participant/study.local.json(커밋 금지), 배포본에서는 exe 옆 study.json.
/// 공개키만 담는다 — 개인키가 들어 있으면 검증에서 거부한다(배포본에 개인키가 실려 나가는 사고 방지).
/// </summary>
public sealed class StudyConfig
{
    [JsonPropertyName("studyId")] public string StudyId { get; set; } = "pca-2026";

    /// <summary>수신 주소. https 만 허용한다(루프백 시험 주소는 http 허용).</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";

    /// <summary>수신기가 아무 요청이나 받지 않게 하는 빌드 토큰. 배포본에 들어가므로 비밀이 아니다 — 쓰레기 업로드 거르기용.</summary>
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>팀 공개키(SubjectPublicKeyInfo PEM, RSA 3072 이상). 데이터는 이 키로만 열 수 있게 봉한다.</summary>
    [JsonPropertyName("publicKeyPem")] public string PublicKeyPem { get; set; } = "";

    /// <summary>공개키 지문(SPKI SHA-256 앞 16자리 hex). 복호화 도구가 맞는 개인키를 고를 때 쓴다.</summary>
    [JsonPropertyName("keyId")] public string KeyId { get; set; } = "";

    /// <summary>참여자에게 보여 줄 문의처 (예: "정태웅 · 카카오톡").</summary>
    [JsonPropertyName("contact")] public string Contact { get; set; } = "정태웅 (수집기 담당)";

    /// <summary>보관 기한 — 동의 화면에 그대로 보인다.</summary>
    [JsonPropertyName("retentionUntil")] public string RetentionUntil { get; set; } = "2026-12-31";

    /// <summary>수집 종료일(이 날짜가 지나면 앱이 기록·전송을 멈추고 정리를 안내한다). 비우면 보관 기한과 같다.</summary>
    [JsonPropertyName("collectUntil")] public string CollectUntil { get; set; } = "";

    /// <summary>수집 마지막 날(포함).</summary>
    public DateOnly? CollectLastDay =>
        DateOnly.TryParseExact(string.IsNullOrWhiteSpace(CollectUntil) ? RetentionUntil : CollectUntil, "yyyy-MM-dd", out var d) ? d : null;

    /// <summary>연구 기간이 끝났는가(오늘이 마지막 날보다 뒤).</summary>
    public bool CollectionEnded(DateTime today) => CollectLastDay is { } last && DateOnly.FromDateTime(today) > last;

    private static readonly Regex StudyRx = new("^[a-z0-9-]{3,32}$");
    private static readonly Regex TokenRx = new("^[A-Za-z0-9_-]{16,128}$");

    public static StudyConfig Load(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<StudyConfig>(text) ?? throw new InvalidDataException("빈 설정");
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        AtomicFile.WriteAllText(path, json);
    }

    public Uri? EndpointUri => Uri.TryCreate(Endpoint, UriKind.Absolute, out var u) ? u : null;

    /// <summary>시험용 루프백 수신기(127.0.0.1/localhost)인가. 시험 전용 옵션은 이때만 허용한다.</summary>
    public bool IsLoopbackEndpoint => EndpointUri is { } u && (u.IsLoopback || u.Host == "localhost");

    /// <summary>문제 목록(빈 목록 = 정상).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!StudyRx.IsMatch(StudyId ?? "")) errors.Add("studyId 형식(소문자·숫자·하이픈 3~32자)");
        var uri = EndpointUri;
        if (uri == null) errors.Add("endpoint 가 절대 주소가 아님");
        else if (uri.Scheme != Uri.UriSchemeHttps && !(IsLoopbackEndpoint && uri.Scheme == Uri.UriSchemeHttp))
            errors.Add("endpoint 는 https 여야 함 (http 는 127.0.0.1 시험 수신기만)");
        if (!TokenRx.IsMatch(Token ?? "")) errors.Add("token 형식(영숫자·_- 16~128자)");
        if (!DateOnly.TryParseExact(RetentionUntil ?? "", "yyyy-MM-dd", out _)) errors.Add("retentionUntil 형식(yyyy-MM-dd)");
        if (!string.IsNullOrWhiteSpace(CollectUntil) && !DateOnly.TryParseExact(CollectUntil, "yyyy-MM-dd", out _)) errors.Add("collectUntil 형식(yyyy-MM-dd)");
        if (string.IsNullOrWhiteSpace(PublicKeyPem)) errors.Add("publicKeyPem 없음");
        else if (PublicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            errors.Add("publicKeyPem 에 개인키가 들어 있음 — 배포 금지");
        else
        {
            try
            {
                using var rsa = CreatePublicKey();
                if (rsa.KeySize < 3072) errors.Add($"공개키가 {rsa.KeySize}비트 — 3072 이상 필요");
                var kid = ComputeKeyId(rsa);
                if (!string.Equals(kid, KeyId, StringComparison.OrdinalIgnoreCase)) errors.Add($"keyId 불일치 (설정 {KeyId}, 계산 {kid})");
            }
            catch (Exception ex) { errors.Add($"공개키 읽기 실패: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>공개키만 가져온다. PEM 에 개인키가 있으면 예외.</summary>
    public RSA CreatePublicKey()
    {
        if (PublicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("개인키가 든 PEM 은 쓰지 않는다");
        var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKeyPem);
        return rsa;
    }

    public static string ComputeKeyId(RSA rsa)
    {
        var spki = rsa.ExportSubjectPublicKeyInfo();
        return Convert.ToHexString(SHA256.HashData(spki))[..16].ToLowerInvariant();
    }
}

/// <summary>임시 파일에 쓰고 바꿔치기 — 쓰는 도중 전원이 꺼져도 반쪽 파일이 남지 않게.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
