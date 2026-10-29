using System.Security.Cryptography;
using System.Text;
using StudyCore;
using Xunit;

namespace StudyCore.Tests;

public class EnvelopeTests
{
    private static readonly RSA Key = MakeKey();
    private static readonly ECDsa Signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static RSA MakeKey()
    {
        var k = RSA.Create(3072);
        return k;
    }

    private static EnvelopeHeader Header(string kind = "dump") =>
        new("pca-2026", "abcd2345efgh", kind, "20261029-101500-crash-1000", "2026-10-29T10:15:30.0000000+09:00", "0123456789abcdef", "0.7.0");

    [Fact]
    public void SealOpen_RoundTrip()
    {
        var pt = Encoding.UTF8.GetBytes("{\"hello\":\"블랙박스\"}");
        using var pub = RSA.Create();
        pub.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _);
        var env = Envelope.Seal(Header(), pt, pub, "tok_0123456789abcdef", Signer);
        var (h, back, spk) = Envelope.Open(env, Key);
        Assert.Equal(Header(), h);
        Assert.Equal(pt, back);
        Assert.Equal(Convert.ToBase64String(Signer.ExportSubjectPublicKeyInfo()), spk);
        Assert.DoesNotContain("블랙박스", env); // 평문이 봉투에 보이면 안 된다
        Assert.DoesNotContain("hello", env);
    }

    [Fact]
    public void TamperedHeader_FailsToOpen()
    {
        var env = Envelope.Seal(Header(), new byte[] { 1, 2, 3 }, Key, "tok_0123456789abcdef", Signer);
        // 수신기·중간자가 머리(참여 ID)를 바꾸면 AAD 불일치로 열리지 않아야 한다
        var tampered = env.Replace("\"pid\":\"abcd2345efgh\"", "\"pid\":\"zzzz2345efgh\"");
        Assert.NotEqual(env, tampered);
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(tampered, Key));
    }

    [Fact]
    public void WrongKey_FailsToOpen()
    {
        var env = Envelope.Seal(Header(), new byte[] { 9 }, Key, "tok_0123456789abcdef", Signer);
        using var other = RSA.Create(3072);
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(env, other));
    }

    [Fact]
    public void UnknownKind_Rejected()
    {
        Assert.Throws<ArgumentException>(() => Envelope.Seal(Header("exfil"), new byte[] { 1 }, Key, "tok_0123456789abcdef", Signer));
    }

    [Fact]
    public void ResignedByAttacker_ChangesSignerKey()
    {
        // 배포본의 공개키·토큰으로 누구나 봉투를 만들 수 있다 — 그러나 참여자의 서명 키가 없으면 서명 공개키가 달라진다(팀 도구가 거부)
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var genuine = Envelope.Seal(Header("withdraw"), new byte[] { 1 }, Key, "tok_0123456789abcdef", Signer);
        var forged = Envelope.Seal(Header("withdraw"), new byte[] { 1 }, Key, "tok_0123456789abcdef", attacker);
        Assert.NotEqual(Envelope.VerifySignature(genuine), Envelope.VerifySignature(forged));
    }

    [Fact]
    public void SwappedSignerKey_FailsVerification()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var env = Envelope.Seal(Header(), new byte[] { 1, 2 }, Key, "tok_0123456789abcdef", Signer);
        // 서명은 그대로 두고 서명 공개키만 공격자 것으로 바꾸면 검증 실패
        var doc = System.Text.Json.Nodes.JsonNode.Parse(env)!;
        doc["spk"] = Convert.ToBase64String(attacker.ExportSubjectPublicKeyInfo());
        Assert.ThrowsAny<CryptographicException>(() => Envelope.VerifySignature(doc.ToJsonString()));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(doc.ToJsonString(), Key));
    }

    [Fact]
    public void TamperedCiphertext_FailsSignatureBeforeDecrypt()
    {
        var env = Envelope.Seal(Header(), new byte[64], Key, "tok_0123456789abcdef", Signer);
        var doc = System.Text.Json.Nodes.JsonNode.Parse(env)!;
        var ct = Convert.FromBase64String(doc["ct"]!.GetValue<string>());
        ct[0] ^= 1;
        doc["ct"] = Convert.ToBase64String(ct);
        Assert.ThrowsAny<CryptographicException>(() => Envelope.VerifySignature(doc.ToJsonString()));
    }
}

public class StudyConfigTests
{
    private static StudyConfig Valid()
    {
        using var rsa = RSA.Create(3072);
        return new StudyConfig
        {
            Endpoint = "https://script.google.com/macros/s/AKfy/exec",
            Token = "tok_0123456789abcdef",
            PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
            KeyId = StudyConfig.ComputeKeyId(rsa),
        };
    }

    [Fact]
    public void ValidConfig_HasNoErrors() => Assert.Empty(Valid().Validate());

    [Fact]
    public void PrivateKeyInConfig_IsRejected()
    {
        using var rsa = RSA.Create(3072);
        var c = Valid();
        c.PublicKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        Assert.Contains(c.Validate(), e => e.Contains("개인키"));
        Assert.Throws<CryptographicException>(() => c.CreatePublicKey());
    }

    [Fact]
    public void HttpEndpoint_OnlyForLoopback()
    {
        var c = Valid();
        c.Endpoint = "http://example.com/exec";
        Assert.Contains(c.Validate(), e => e.Contains("https"));
        c.Endpoint = "http://127.0.0.1:8787/";
        Assert.Empty(c.Validate());
        Assert.True(c.IsLoopbackEndpoint);
    }

    [Fact]
    public void CollectionEnd_UsesCollectUntil_ThenRetention()
    {
        var c = Valid();
        c.RetentionUntil = "2026-12-31";
        Assert.False(c.CollectionEnded(new DateTime(2026, 12, 31)));
        Assert.True(c.CollectionEnded(new DateTime(2027, 1, 1)));
        c.CollectUntil = "2026-12-11";
        Assert.True(c.CollectionEnded(new DateTime(2026, 12, 12)));
        c.CollectUntil = "12/11";
        Assert.Contains(c.Validate(), e => e.Contains("collectUntil"));
    }

    [Fact]
    public void SmallKey_And_KeyIdMismatch_AreRejected()
    {
        using var small = RSA.Create(2048);
        var c = Valid();
        c.PublicKeyPem = small.ExportSubjectPublicKeyInfoPem();
        var errors = c.Validate();
        Assert.Contains(errors, e => e.Contains("3072"));
        Assert.Contains(errors, e => e.Contains("keyId"));
    }
}

public class ReceiverRulesTests
{
    private const string Token = "tok_0123456789abcdef";

    private static string Env(string pid = "abcd2345efgh", string name = "20261029-101500-crash-1000", string kind = "dump")
    {
        using var rsa = RSA.Create(3072);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Envelope.Seal(new EnvelopeHeader("pca-2026", pid, kind, name, "2026-10-29T10:15:30+09:00", "0123456789abcdef", "0.7.0"),
            new byte[] { 1, 2, 3 }, rsa, Token, signer);
    }

    [Fact]
    public void Accepts_WellFormed() => Assert.True(ReceiverRules.Validate(Env(), Token).Ok);

    [Theory]
    [InlineData("", "empty")]
    [InlineData("not json", "bad_json")]
    [InlineData("{\"v\":2}", "bad_version")]
    public void Rejects_Malformed(string body, string code) => Assert.Equal(code, ReceiverRules.Validate(body, Token).Error);

    [Fact]
    public void Rejects_WrongToken() => Assert.Equal("bad_token", ReceiverRules.Validate(Env(), "tok_other_0123456789").Error);

    [Theory]
    [InlineData("ABCD", "20261029-101500-crash-1000")]          // 대문자·짧은 참여 ID
    [InlineData("abcd2345efgh", "../../etc/passwd")]           // 경로 이동
    [InlineData("abcd2345efgh", "a/b")]
    [InlineData("abcd2345efgh", "..")]                         // 10/29 검토: 이전 규칙은 "."·".." 를 받아 복호화 도구가 상위 폴더에 썼다
    [InlineData("abcd2345efgh", ".")]
    [InlineData("abcd2345efgh", "CON")]                        // 장치 이름
    [InlineData("abcd2345efgh", "run.cmd")]
    [InlineData("abcd2345efgh", "20261029-101500-crash-1000.json")]
    public void Rejects_BadPidOrName(string pid, string name) =>
        Assert.Equal("bad_request", ReceiverRules.Validate(Env(pid, name), Token).Error);

    [Theory]
    [InlineData("20261029-101500-crash-1000")]
    [InlineData("20261029-101500-crash-1000-2")]
    [InlineData("20261029-101500-power-41")]
    [InlineData("hb-20261029-101500")]
    [InlineData("withdraw-20261029-101500")]
    public void Accepts_AppNameFormats(string name) => Assert.Matches(ReceiverRules.NameRx, name);

    [Fact]
    public void Rejects_MissingSignature()
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(Env())!.AsObject();
        doc.Remove("sig");
        Assert.Equal("bad_request", ReceiverRules.Validate(doc.ToJsonString(), Token).Error);
    }

    [Fact]
    public void Rejects_TooLarge()
    {
        var big = new string('a', ReceiverRules.MaxBodyChars + 1);
        Assert.Equal("too_large", ReceiverRules.Validate(big, Token).Error);
    }

    [Fact]
    public void ParticipantId_MatchesReceiverRule()
    {
        for (int i = 0; i < 200; i++) Assert.Matches(ReceiverRules.PidRx, Ids.NewParticipantId());
    }
}

public class BundleTests
{
    [Fact]
    public void Directory_RoundTrip_TopLevelJsonOnly()
    {
        var dir = Directory.CreateTempSubdirectory("bundle").FullName;
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(dir, "events.json"), "{\"events\":[]}");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "보내면 안 됨");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "x.json"), "{}");

        var zip = BundleBuilder.FromDirectory(dir, new Dictionary<string, string> { ["upload.json"] = "{\"v\":1}" }, out var truncated);
        var files = BundleBuilder.Extract(zip);
        Assert.False(truncated);
        Assert.Equal(new[] { "events.json", "meta.json", "upload.json" }, files.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void Oversized_DropsEvents()
    {
        var dir = Directory.CreateTempSubdirectory("bundle-big").FullName;
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{\"a\":1}");
        // 압축이 안 되는 무작위 바이트를 base64 로 — 상한(6MB)을 넘긴다
        File.WriteAllText(Path.Combine(dir, "events.json"), "\"" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(6 * 1024 * 1024)) + "\"");
        var zip = BundleBuilder.FromDirectory(dir, null, out var truncated);
        Assert.True(truncated);
        Assert.True(zip.Length < BundleBuilder.MaxBundleBytes);
        Assert.Contains("unavailable", Encoding.UTF8.GetString(BundleBuilder.Extract(zip)["events.json"]));
    }

    [Fact]
    public void Extract_RejectsTraversal()
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("../evil.json").Open();
            s.WriteByte(1);
        }
        Assert.Throws<InvalidDataException>(() => BundleBuilder.Extract(ms.ToArray()));
    }
}

public class OutboxTests
{
    private sealed class FakeTransport : IUploadTransport
    {
        public readonly Queue<SendResult> Results = new();
        public int Calls;
        public Task<SendResult> SendAsync(string envelopeJson, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new SendResult(SendOutcome.Ok, "stored.json", null));
        }
    }

    [Fact]
    public async Task Ok_WritesReceipt_AndEmptiesQueue()
    {
        var root = Directory.CreateTempSubdirectory("outbox").FullName;
        var ob = new Outbox(root);
        ob.Enqueue("{}", "dump", "20261029-101500-crash-1000");
        var r = await ob.ProcessAsync(new FakeTransport(), CancellationToken.None);
        Assert.Equal(1, r.Sent);
        Assert.Equal(0, ob.PendingCount);
        Assert.True(ob.IsSent("dump", "20261029-101500-crash-1000"));
    }

    [Fact]
    public async Task NetworkDown_BacksOff_AndStopsTheRun()
    {
        var root = Directory.CreateTempSubdirectory("outbox-net").FullName;
        var ob = new Outbox(root);
        var t0 = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.FromHours(9));
        ob.Enqueue("{}", "dump", "20261029-100000-crash-1000", t0);
        ob.Enqueue("{}", "dump", "20261029-100001-crash-1000", t0.AddSeconds(1));
        var tr = new FakeTransport();
        tr.Results.Enqueue(new SendResult(SendOutcome.Retry, null, "network", NetworkDown: true));
        var now = t0;
        var r = await ob.ProcessAsync(tr, CancellationToken.None, clock: () => now);
        Assert.Equal(1, tr.Calls);           // 연결이 안 되면 두 번째는 시도하지 않는다
        Assert.Equal(2, r.Remaining);

        var first = ob.Pending()[0];
        Assert.Equal(1, first.Attempts);
        Assert.Equal(t0.AddMinutes(1), first.NextAttemptAt);

        // 30초 뒤: 첫 항목은 기한 전이라 건너뛰고 두 번째만 보낸다
        now = t0.AddSeconds(30);
        r = await ob.ProcessAsync(tr, CancellationToken.None, clock: () => now);
        Assert.Equal(1, r.Sent);
        Assert.True(ob.IsSent("dump", "20261029-100001-crash-1000"));
        Assert.True(ob.IsPending("dump", "20261029-100000-crash-1000"));

        // 1분 뒤: 첫 항목도 나간다
        now = t0.AddMinutes(2);
        r = await ob.ProcessAsync(tr, CancellationToken.None, clock: () => now);
        Assert.Equal(1, r.Sent);
        Assert.Equal(0, ob.PendingCount);
    }

    [Fact]
    public async Task Permanent_MovesToFailed()
    {
        var root = Directory.CreateTempSubdirectory("outbox-perm").FullName;
        var ob = new Outbox(root);
        ob.Enqueue("{}", "dump", "big1");
        var tr = new FakeTransport();
        tr.Results.Enqueue(new SendResult(SendOutcome.Permanent, null, "too_large"));
        await ob.ProcessAsync(tr, CancellationToken.None);
        Assert.Equal(0, ob.PendingCount);
        Assert.Equal(1, ob.FailedCount);
        Assert.True(ob.IsFailed("dump", "big1"));
    }

    [Fact]
    public async Task HourlyCap_Holds()
    {
        var root = Directory.CreateTempSubdirectory("outbox-cap").FullName;
        var ob = new Outbox(root, maxPerHour: 2);
        for (int i = 0; i < 4; i++) ob.Enqueue("{}", "dump", $"n{i}", DateTimeOffset.Now.AddSeconds(i));
        var r = await ob.ProcessAsync(new FakeTransport(), CancellationToken.None);
        Assert.Equal(2, r.Sent);
        Assert.Equal(2, r.Remaining);
    }

    [Fact]
    public void Trim_CapsCountBytesAndAge_OldestFirst()
    {
        var root = Directory.CreateTempSubdirectory("outbox-trim").FullName;
        var ob = new Outbox(root);
        var now = DateTimeOffset.Now;
        for (int i = 0; i < 10; i++) ob.Enqueue(new string('x', 1000), "dump", $"20261029-1000{i:00}-crash-1000", now.AddSeconds(i));
        File.WriteAllText(Path.Combine(root, "outbox", ".stale.tmp"), "half");
        File.SetLastWriteTime(Path.Combine(root, "outbox", ".stale.tmp"), DateTime.Now.AddHours(-2));

        Assert.Equal(4, ob.Trim(maxBytes: long.MaxValue, maxCount: 6, maxAge: TimeSpan.FromDays(30), now));
        Assert.Equal(6, ob.PendingCount);
        Assert.False(ob.IsPending("dump", "20261029-100000-crash-1000"));   // 오래된 것부터
        Assert.True(ob.IsPending("dump", "20261029-100009-crash-1000"));
        Assert.False(File.Exists(Path.Combine(root, "outbox", ".stale.tmp")));

        Assert.Equal(3, ob.Trim(maxBytes: 3500, maxCount: 100, maxAge: TimeSpan.FromDays(30), now));
        Assert.Equal(3, ob.PendingCount);
        Assert.Equal(3, ob.Trim(long.MaxValue, 100, TimeSpan.FromDays(-1), now)); // 나이 상한
        Assert.Equal(0, ob.PendingCount);
    }

    [Theory]
    [InlineData(200, "{\"ok\":true,\"stored\":\"x.pcae.json\"}", SendOutcome.Ok)]
    [InlineData(200, "{\"ok\":false,\"error\":\"too_large\"}", SendOutcome.Permanent)]
    [InlineData(200, "{\"ok\":false,\"error\":\"bad_token\"}", SendOutcome.Retry)]      // 수신기 설정으로 고칠 수 있다
    [InlineData(200, "{\"ok\":false,\"error\":\"rate_limited\"}", SendOutcome.Retry)]
    [InlineData(200, "<html>Google error</html>", SendOutcome.Retry)]
    [InlineData(503, "", SendOutcome.Retry)]
    [InlineData(413, "", SendOutcome.Permanent)]
    public void Interpret_Responses(int status, string body, SendOutcome expected) =>
        Assert.Equal(expected, HttpUploadTransport.Interpret(status, body).Outcome);
}
