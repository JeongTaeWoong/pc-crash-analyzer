using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using StudyCore;
using Xunit;

namespace StudyCore.Tests;

/// <summary>자동 업데이트(0.9)의 검증 로직: 서명·버전·주소, 묶음 풀기, 폴더 바꿔치기·되돌리기.</summary>
public class UpdateTests : IDisposable
{
    private static readonly ECDsa Key = UpdateSigning.NewKey();
    private static readonly string[] Trusted = { UpdateSigning.PublicKeyBase64(Key) };
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pcbb-update-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static UpdateManifest Manifest(string version = "0.9.1", string? url = null) => new()
    {
        Version = version,
        Url = url ?? $"{UpdateTrust.AssetPrefix}v{version}/PcBlackBox-{version}-win-x64-update.zip",
        Size = 1234,
        Sha256 = new string('a', 64),
        Published = "2026-11-11T12:00:00+09:00",
        Notes = "시험",
    };

    private static UpdateManifest Read(byte[] bytes, string sig, string[]? trusted = null) =>
        UpdateManifest.ReadVerified(bytes, sig, trusted ?? Trusted, UpdateTrust.IsReleaseAsset);

    // ───────── 서명 ─────────

    [Fact]
    public void SignedManifest_IsAccepted()
    {
        var bytes = Manifest().ToBytes();
        var m = Read(bytes, UpdateSigning.Sign(bytes, Key));
        Assert.Equal("0.9.1", m.Version);
        Assert.Equal("시험", m.Notes);
    }

    [Fact]
    public void TamperedManifest_IsRejected()
    {
        var bytes = Manifest().ToBytes();
        var sig = UpdateSigning.Sign(bytes, Key);
        // 서명한 뒤 버전만 바꿔 본다(공격자가 안내문을 고친 경우)
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("0.9.1", "0.9.9"));
        Assert.NotEqual(bytes, tampered);
        Assert.Throws<CryptographicException>(() => Read(tampered, sig));
    }

    [Fact]
    public void ManifestSignedByOtherKey_IsRejected()
    {
        using var attacker = UpdateSigning.NewKey();
        var bytes = Manifest().ToBytes();
        Assert.Throws<CryptographicException>(() => Read(bytes, UpdateSigning.Sign(bytes, attacker)));
    }

    [Fact]
    public void MissingOrGarbageSignature_IsRejected()
    {
        var bytes = Manifest().ToBytes();
        Assert.Throws<CryptographicException>(() => Read(bytes, ""));
        Assert.Throws<CryptographicException>(() => Read(bytes, "not base64 !!"));
        Assert.Throws<CryptographicException>(() => Read(bytes, Convert.ToBase64String(new byte[64])));
    }

    [Fact]
    public void SecondTrustedKey_Works_ForKeyRotation()
    {
        using var next = UpdateSigning.NewKey();
        var bytes = Manifest().ToBytes();
        var m = Read(bytes, UpdateSigning.Sign(bytes, next), new[] { Trusted[0], "garbage", UpdateSigning.PublicKeyBase64(next) });
        Assert.Equal("0.9.1", m.Version);
    }

    [Fact]
    public void EmbeddedTrustKeys_AreReadableP256PublicKeys()
    {
        Assert.NotEmpty(UpdateTrust.Keys);
        foreach (var k in UpdateTrust.Keys)
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
            Assert.Equal(256, ec.KeySize);
            Assert.Throws<CryptographicException>(() => ec.ExportPkcs8PrivateKey()); // 공개키만 들어 있어야 한다
        }
    }

    // ───────── 내용 규칙 ─────────

    [Theory]
    [InlineData("https://github.com/someone-else/pc-crash-analyzer/releases/download/v0.9.1/x.zip")]
    [InlineData("http://github.com/JeongTaeWoong/pc-crash-analyzer/releases/download/v0.9.1/x.zip")]
    [InlineData("https://github.com.evil.example/JeongTaeWoong/pc-crash-analyzer/releases/download/v0.9.1/x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("http://127.0.0.1:8788/x.zip")]
    [InlineData("file:///C:/x.zip")]
    public void CorrectlySignedManifest_WithForeignUrl_IsRejected(string url)
    {
        var bytes = Manifest(url: url).ToBytes();
        Assert.Throws<InvalidDataException>(() => Read(bytes, UpdateSigning.Sign(bytes, Key)));
    }

    [Fact]
    public void LoopbackUrl_OnlyWhenCallerAllowsIt()
    {
        var bytes = Manifest(url: "http://127.0.0.1:8788/x.zip").ToBytes();
        var m = UpdateManifest.ReadVerified(bytes, UpdateSigning.Sign(bytes, Key), Trusted, u => u.IsLoopback);
        Assert.Equal("http://127.0.0.1:8788/x.zip", m.Url);
    }

    [Fact]
    public void BadFields_AreRejected()
    {
        void Bad(Action<UpdateManifest> change)
        {
            var m = Manifest();
            change(m);
            var bytes = m.ToBytes();
            Assert.Throws<InvalidDataException>(() => Read(bytes, UpdateSigning.Sign(bytes, Key)));
        }
        Bad(m => m.Product = "Other");
        Bad(m => m.Schema = 2);
        Bad(m => m.Version = "0.9");
        Bad(m => m.Version = "0.9.1+abc");
        Bad(m => m.Size = 0);
        Bad(m => m.Size = UpdateManifest.MaxPackageBytes + 1);
        Bad(m => m.Sha256 = "xyz");
        Bad(m => m.Sha256 = new string('A', 64)); // 소문자 hex 만
        Bad(m => m.Notes = new string('가', 601));
    }

    [Theory]
    [InlineData("0.9.1", true)]
    [InlineData("0.9.1+3f2a9c1", true)]   // 파일 버전 정보에 붙는 커밋 꼬리
    [InlineData("10.20.30", true)]
    [InlineData("0.9", false)]
    [InlineData("0.9.1.0", false)]
    [InlineData("0.9.x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("-1.0.0", false)]
    public void VersionParsing(string? s, bool ok) => Assert.Equal(ok, UpdateManifest.TryParseVersion(s, out _));

    [Theory]
    [InlineData("0.9.1", "0.9.0", true)]
    [InlineData("0.10.0", "0.9.9", true)]   // 문자열 비교가 아니라 숫자 비교
    [InlineData("0.9.1", "0.9.1", false)]   // 같은 버전은 다시 받지 않는다
    [InlineData("0.9.0", "0.9.1", false)]   // 낮은 버전(되돌리기 공격)은 받지 않는다
    [InlineData("0.9.1", "0.9.1+abc", false)]
    [InlineData("0.9.1", "이상한값", false)]
    public void NewerThan(string offered, string current, bool newer) =>
        Assert.Equal(newer, Manifest(offered).IsNewerThan(current));

    // ───────── 묶음 ─────────

    private string MakeApp(string name = "app")
    {
        var dir = Path.Combine(_tmp, name);
        Directory.CreateDirectory(Path.Combine(dir, "ko"));
        File.WriteAllText(Path.Combine(dir, UpdatePackage.MainExe), "exe-" + name);
        File.WriteAllText(Path.Combine(dir, "ko", "res.dll"), "dll");
        File.WriteAllText(Path.Combine(dir, "읽어주세요.txt"), "안내");
        File.WriteAllText(Path.Combine(dir, "study.json"), "{\"token\":\"secret\"}");
        File.WriteAllText(Path.Combine(dir, "x.pdb"), "pdb");
        File.WriteAllText(Path.Combine(dir, "k.private.pem"), "pem");
        return dir;
    }

    [Fact]
    public void Package_RoundTrip_LeavesOutStudyConfigAndKeys()
    {
        var zip = Path.Combine(_tmp, "u.zip");
        Assert.Equal(3, UpdatePackage.Create(MakeApp(), zip));
        using (var z = ZipFile.OpenRead(zip))
        {
            var names = z.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("ko/res.dll", names); // '/' 구분자
            Assert.DoesNotContain(names, n => n.Contains("study") || n.EndsWith(".pem") || n.EndsWith(".pdb"));
        }
        var outDir = Path.Combine(_tmp, "out");
        Assert.Equal(3, UpdatePackage.Extract(zip, outDir));
        Assert.Equal("exe-app", File.ReadAllText(Path.Combine(outDir, UpdatePackage.MainExe)));
        Assert.Equal("안내", File.ReadAllText(Path.Combine(outDir, "읽어주세요.txt")));
        Assert.False(File.Exists(Path.Combine(outDir, "study.json")));
        Assert.Equal(64, UpdatePackage.Sha256Hex(zip).Length);
    }

    private string ZipWith(params (string Name, string Content)[] entries)
    {
        var zip = Path.Combine(_tmp, Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write(content);
        }
        return zip;
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("/abs.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("..\\evil.dll")]
    public void Extract_RejectsNamesThatLeaveTheFolder(string bad)
    {
        var zip = ZipWith((UpdatePackage.MainExe, "x"), (bad, "evil"));
        var outDir = Path.Combine(_tmp, "o-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(zip, outDir));
        Assert.False(File.Exists(Path.Combine(_tmp, "evil.dll")));
    }

    [Fact]
    public void Extract_SkipsStudyConfigEvenIfPresent_AndNeedsMainExe()
    {
        var outDir = Path.Combine(_tmp, "o1");
        Assert.Equal(1, UpdatePackage.Extract(ZipWith((UpdatePackage.MainExe, "x"), ("study.json", "{}")), outDir));
        Assert.False(File.Exists(Path.Combine(outDir, "study.json")));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(ZipWith(("readme.txt", "x")), Path.Combine(_tmp, "o2")));
    }

    [Fact]
    public void Extract_EnforcesLimits_AndNeedsEmptyFolder()
    {
        var big = ZipWith((UpdatePackage.MainExe, new string('x', 5000)));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(big, Path.Combine(_tmp, "o3"), maxBytes: 1000));
        var many = ZipWith((UpdatePackage.MainExe, "x"), ("a", "1"), ("b", "2"));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.Extract(many, Path.Combine(_tmp, "o4"), maxFiles: 2));
        var used = Path.Combine(_tmp, "o5");
        Directory.CreateDirectory(used);
        File.WriteAllText(Path.Combine(used, "old.txt"), "x");
        Assert.Throws<IOException>(() => UpdatePackage.Extract(big, used));
    }

    // ───────── 폴더 바꿔치기 ─────────

    private (string Target, string Source) MakeInstall()
    {
        var target = MakeApp("install");
        File.WriteAllText(Path.Combine(target, "old-only.dll"), "지난 버전에만 있던 파일");
        var source = Path.Combine(_tmp, "staged");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, UpdatePackage.MainExe), "exe-new");
        File.WriteAllText(Path.Combine(source, "new-only.dll"), "new");
        return (target, source);
    }

    [Fact]
    public void Replace_SwapsWholeFolder_KeepsStudyConfig_AndLeavesOldForRollback()
    {
        var (target, source) = MakeInstall();
        DirSwap.Replace(target, source, new[] { "study.json" });

        Assert.Equal("exe-new", File.ReadAllText(Path.Combine(target, UpdatePackage.MainExe)));
        Assert.True(File.Exists(Path.Combine(target, "new-only.dll")));
        Assert.False(File.Exists(Path.Combine(target, "old-only.dll")));           // 섞이지 않는다
        Assert.Equal("{\"token\":\"secret\"}", File.ReadAllText(Path.Combine(target, "study.json"))); // 연구 설정은 이어 쓴다
        Assert.Equal("exe-install", File.ReadAllText(Path.Combine(DirSwap.OldOf(target), UpdatePackage.MainExe)));
        Assert.False(Directory.Exists(DirSwap.NewOf(target)));
        Assert.True(File.Exists(Path.Combine(source, UpdatePackage.MainExe)));      // 받아 둔 쪽은 그대로(복사)
    }

    [Fact]
    public void Restore_PutsOldVersionBack()
    {
        var (target, source) = MakeInstall();
        DirSwap.Replace(target, source, new[] { "study.json" });
        DirSwap.Restore(target);

        Assert.Equal("exe-install", File.ReadAllText(Path.Combine(target, UpdatePackage.MainExe)));
        Assert.True(File.Exists(Path.Combine(target, "old-only.dll")));
        Assert.False(File.Exists(Path.Combine(target, "new-only.dll")));
        Assert.False(Directory.Exists(DirSwap.OldOf(target)));
        Assert.False(Directory.Exists(DirSwap.FailedOf(target)));
    }

    [Fact]
    public void Replace_WhenTargetIsInUse_ChangesNothing()
    {
        var (target, source) = MakeInstall();
        // 실행 중인 exe 처럼 파일을 잡고 있으면 폴더 이름을 바꿀 수 없다
        using (new FileStream(Path.Combine(target, UpdatePackage.MainExe), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => DirSwap.Replace(target, source, new[] { "study.json" }, retries: 1));
            Assert.True(File.Exists(Path.Combine(target, "old-only.dll")));
            Assert.False(Directory.Exists(DirSwap.OldOf(target)));
            Assert.False(Directory.Exists(DirSwap.NewOf(target)));
        }
        Assert.Equal("exe-install", File.ReadAllText(Path.Combine(target, UpdatePackage.MainExe)));
    }

    [Fact]
    public void Replace_ClearsLeftoversFromAnEarlierAttempt()
    {
        var (target, source) = MakeInstall();
        Directory.CreateDirectory(DirSwap.NewOf(target));
        File.WriteAllText(Path.Combine(DirSwap.NewOf(target), "stale.dll"), "지난 시도 찌꺼기");
        Directory.CreateDirectory(DirSwap.OldOf(target));
        DirSwap.Replace(target, source, Array.Empty<string>());
        Assert.False(File.Exists(Path.Combine(target, "stale.dll")));
        Assert.False(File.Exists(Path.Combine(target, "study.json"))); // keep 에 없으면 가져오지 않는다
    }

    [Fact]
    public void State_And_Health_RoundTrip()
    {
        var st = UpdateState.Load(_tmp);
        Assert.Equal("", st.PendingTo);
        st.PendingTo = "0.9.1";
        st.SkipVersion = "0.9.0";
        st.Result("rolled-back", new string('x', 500));
        st.Save(_tmp);
        var back = UpdateState.Load(_tmp);
        Assert.Equal("0.9.1", back.PendingTo);
        Assert.Equal("0.9.0", back.SkipVersion);
        Assert.Equal(300, back.LastDetail.Length);

        Assert.Null(UpdateHealth.TryLoad(_tmp));
        UpdateHealth.Write(_tmp, "0.9.1", ok: false, "EtwError");
        var h = UpdateHealth.TryLoad(_tmp)!;
        Assert.False(h.Ok);
        Assert.Equal("0.9.1", h.Version);
    }
}
