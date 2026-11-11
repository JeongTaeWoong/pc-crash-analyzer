using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StudyCore;

/// <summary>봉투 머리. 수신기는 이것만 보고 폴더를 고른다 — 내용은 볼 수 없다.</summary>
/// <param name="CreatedAt">ISO-8601 문자열 그대로 (AAD 재현을 위해 문자열로 둔다).</param>
public sealed record EnvelopeHeader(string Study, string Pid, string Kind, string Name, string CreatedAt, string Kid, string App);

/// <summary>
/// 하이브리드 암호 봉투 (v1):
/// - 기밀성: 내용(zip)은 무작위 AES-256-GCM 키로 암호화하고, 그 키를 팀 공개키(RSA-OAEP-SHA256)로 감싼다.
///   머리 필드(study·pid·kind·name·createdAt·kid·app)는 AAD 로 묶어, 머리를 바꾸면 복호화가 실패한다.
/// - 출처: 참여자마다 동의할 때 만든 서명 키(ECDSA P-256)로 머리·암호문 전체에 서명한다(spk = 서명 공개키, sig = 서명).
///   공개키·토큰은 배포 zip 에 들어 있어 누구나 봉투를 "만들" 수는 있지만, 그 참여자의 서명 키 없이는 그 참여자인 척할 수 없다.
///   팀 도구는 참여자별로 처음 본 서명 키를 기억하고(TOFU) 다른 키로 온 봉투를 거부한다.
/// 결과: 전송 중(HTTPS)·저장 중(Google Drive 등) 모두 개인키 없이는 읽을 수 없다. 개인키는 팀 PC 밖으로 나가지 않는다.
/// </summary>
public static class Envelope
{
    public const int Version = 1;
    public const string Alg = "RSA-OAEP-256+A256GCM";
    public const string SigAlg = "ES256";
    public static readonly string[] Kinds = { "dump", "annotation", "heartbeat", "withdraw" };

    public static string Seal(EnvelopeHeader h, byte[] plaintext, RSA publicKey, string token, ECDsa signer)
    {
        if (!Kinds.Contains(h.Kind)) throw new ArgumentException($"알 수 없는 kind: {h.Kind}");
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(iv, plaintext, ct, tag, Aad(h));
            var ek = publicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
            var spk = signer.ExportSubjectPublicKeyInfo();
            var sig = signer.SignData(SigInput(h, spk, ek, iv, tag, ct), HashAlgorithmName.SHA256);

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("v", Version);
                w.WriteString("alg", Alg);
                w.WriteString("study", h.Study);
                w.WriteString("pid", h.Pid);
                w.WriteString("kind", h.Kind);
                w.WriteString("name", h.Name);
                w.WriteString("createdAt", h.CreatedAt);
                w.WriteString("kid", h.Kid);
                w.WriteString("app", h.App);
                w.WriteString("token", token);
                w.WriteString("ek", Convert.ToBase64String(ek));
                w.WriteString("iv", Convert.ToBase64String(iv));
                w.WriteString("tag", Convert.ToBase64String(tag));
                w.WriteString("ct", Convert.ToBase64String(ct));
                w.WriteString("sigAlg", SigAlg);
                w.WriteString("spk", Convert.ToBase64String(spk));
                w.WriteString("sig", Convert.ToBase64String(sig));
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>머리만 읽는다(복호화·서명 검증 없음). 형식이 틀리면 예외.</summary>
    public static EnvelopeHeader ReadHeader(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadHeader(doc.RootElement);
    }

    public static EnvelopeHeader ReadHeader(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) throw new InvalidDataException("객체가 아님");
        if (!r.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != Version)
            throw new InvalidDataException("bad_version");
        return new EnvelopeHeader(Str(r, "study"), Str(r, "pid"), Str(r, "kind"), Str(r, "name"), Str(r, "createdAt"), Str(r, "kid"), Str(r, "app"));
    }

    /// <summary>서명 검증(개인키 불필요). 맞으면 서명 공개키(base64)를 돌려준다. 틀리면 CryptographicException.</summary>
    public static string VerifySignature(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return VerifySignature(doc.RootElement, ReadHeader(doc.RootElement));
    }

    private static string VerifySignature(JsonElement r, EnvelopeHeader h)
    {
        if (Str(r, "sigAlg") != SigAlg) throw new CryptographicException("서명 없음 또는 알 수 없는 서명 방식");
        var spkB64 = Str(r, "spk");
        var spk = Convert.FromBase64String(spkB64);
        var sig = Convert.FromBase64String(Str(r, "sig"));
        var ek = Convert.FromBase64String(Str(r, "ek"));
        var iv = Convert.FromBase64String(Str(r, "iv"));
        var tag = Convert.FromBase64String(Str(r, "tag"));
        var ct = Convert.FromBase64String(Str(r, "ct"));
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(spk, out _);
        if (ecdsa.KeySize != 256 || !ecdsa.VerifyData(SigInput(h, spk, ek, iv, tag, ct), sig, HashAlgorithmName.SHA256))
            throw new CryptographicException("서명 불일치");
        return spkB64;
    }

    /// <summary>
    /// 서명을 확인하고 개인키로 연다. 서명이 틀리거나, 키가 틀리거나, 머리·본문이 바뀌었으면 CryptographicException.
    /// 반환 Spk = 서명 공개키(base64) — 호출자가 참여자별로 처음 본 키와 비교한다.
    /// </summary>
    public static (EnvelopeHeader Header, byte[] Plaintext, string Spk) Open(string json, RSA privateKey)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var h = ReadHeader(r);
        if (Str(r, "alg") != Alg) throw new InvalidDataException("알 수 없는 alg");
        var spk = VerifySignature(r, h);
        var ek = Convert.FromBase64String(Str(r, "ek"));
        var iv = Convert.FromBase64String(Str(r, "iv"));
        var tag = Convert.FromBase64String(Str(r, "tag"));
        var ct = Convert.FromBase64String(Str(r, "ct"));
        var key = privateKey.Decrypt(ek, RSAEncryptionPadding.OaepSHA256);
        try
        {
            var pt = new byte[ct.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(iv, ct, tag, pt, Aad(h));
            return (h, pt, spk);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Aad(EnvelopeHeader h) =>
        Encoding.UTF8.GetBytes(string.Join("\n", "pca-envelope/1", h.Study, h.Pid, h.Kind, h.Name, h.CreatedAt, h.Kid, h.App));

    /// <summary>서명 대상: 머리(AAD)·서명 공개키·감싼 키·nonce·tag·암호문 해시 — 각 조각 앞에 길이를 붙여 경계가 모호하지 않게.</summary>
    private static byte[] SigInput(EnvelopeHeader h, byte[] spk, byte[] ek, byte[] iv, byte[] tag, byte[] ct)
    {
        using var ms = new MemoryStream();
        void Part(ReadOnlySpan<byte> b)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, b.Length);
            ms.Write(len);
            ms.Write(b);
        }
        Part(Encoding.ASCII.GetBytes("pca-envelope-sig/1"));
        Part(Aad(h));
        Part(spk);
        Part(ek);
        Part(iv);
        Part(tag);
        Part(SHA256.HashData(ct));
        return ms.ToArray();
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
