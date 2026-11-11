using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>
/// 참여자 상태(데이터 폴더의 participant.json): 참여 ID·동의 시각·설정·서명 키. 수집 데이터가 아니라 설정이다.
/// 동의하지 않으면 아무것도 저장하지 않는다.
/// </summary>
public sealed class ParticipantState
{
    /// <summary>동의 문구가 바뀌면 올린다 → 다음 실행에서 다시 묻는다.</summary>
    public const int CurrentConsentVersion = 1;

    [JsonPropertyName("participantId")] public string ParticipantId { get; set; } = "";
    [JsonPropertyName("consentVersion")] public int ConsentVersion { get; set; }
    [JsonPropertyName("consentedAt")] public DateTimeOffset? ConsentedAt { get; set; }

    /// <summary>동의한 연구·받는 곳. 다른 연구나 시험 수신기에 한 동의가 실제 배포본으로 넘어가지 않게(10/29 검토 지적).</summary>
    [JsonPropertyName("consentStudyId")] public string ConsentStudyId { get; set; } = "";
    [JsonPropertyName("consentEndpointHost")] public string ConsentEndpointHost { get; set; } = "";

    [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";
    [JsonPropertyName("autostart")] public bool Autostart { get; set; }
    [JsonPropertyName("uploadsPaused")] public bool UploadsPaused { get; set; }

    /// <summary>
    /// 이 참여자만의 서명 키(ECDSA P-256, PKCS#8 base64). 동의할 때 만든다. 봉투에 서명해, 배포 zip 의 공개키·토큰을 가진
    /// 다른 사람이 이 참여자인 척(가짜 철회·메모·하트비트) 보낼 수 없게 한다. 이 PC 사용자 폴더(%LOCALAPPDATA%)에만 있다.
    /// </summary>
    [JsonPropertyName("signingKey")] public string SigningKey { get; set; } = "";

    [JsonIgnore]
    public bool HasCurrentConsent => ConsentVersion >= CurrentConsentVersion && ReceiverRules.PidRx.IsMatch(ParticipantId) && SigningKey.Length > 0;

    public bool HasConsentFor(StudyConfig study) =>
        HasCurrentConsent && ConsentStudyId == study.StudyId &&
        string.Equals(ConsentEndpointHost, Texts.EndpointHost(study), StringComparison.OrdinalIgnoreCase);

    /// <summary>같은 연구·받는 곳에 대한 이전 참여 ID 가 있으면(동의 문구 개정 등) 이어 쓴다. 아니면 새로.</summary>
    public string ParticipantIdFor(StudyConfig study) =>
        ReceiverRules.PidRx.IsMatch(ParticipantId) && SigningKey.Length > 0 && ConsentStudyId == study.StudyId &&
        string.Equals(ConsentEndpointHost, Texts.EndpointHost(study), StringComparison.OrdinalIgnoreCase)
            ? ParticipantId
            : Ids.NewParticipantId();

    public static string PathIn(string dataDir) => Path.Combine(dataDir, "participant.json");

    public static ParticipantState Load(string dataDir)
    {
        var p = PathIn(dataDir);
        try
        {
            if (File.Exists(p)) return JsonSerializer.Deserialize<ParticipantState>(File.ReadAllText(p)) ?? new ParticipantState();
        }
        catch
        {
            // 손상 — 다시 동의를 받는다(이전 파일은 남겨 둔다)
            try { File.Copy(p, p + ".broken", overwrite: true); } catch { }
        }
        return new ParticipantState();
    }

    public void Save(string dataDir) =>
        AtomicFile.WriteAllText(PathIn(dataDir), JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

    public void Accept(string participantId, string? nickname, bool autostart, StudyConfig study)
    {
        // 참여 ID 가 같으면 서명 키도 이어 쓴다(팀 도구의 "처음 본 키" 기억과 이어지게)
        if (participantId != ParticipantId || SigningKey.Length == 0)
        {
            using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            SigningKey = Convert.ToBase64String(k.ExportPkcs8PrivateKey());
        }
        ParticipantId = participantId;
        ConsentVersion = CurrentConsentVersion;
        ConsentedAt = DateTimeOffset.Now;
        ConsentStudyId = study.StudyId;
        ConsentEndpointHost = Texts.EndpointHost(study);
        Nickname = CleanNickname(nickname);
        Autostart = autostart;
    }

    public ECDsa CreateSigner()
    {
        var k = ECDsa.Create();
        k.ImportPkcs8PrivateKey(Convert.FromBase64String(SigningKey), out _);
        return k;
    }

    public static string CleanNickname(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = new string(s.Where(c => !char.IsControl(c) && c != '|').ToArray()).Trim();
        return t.Length > 20 ? t[..20] : t;
    }
}
