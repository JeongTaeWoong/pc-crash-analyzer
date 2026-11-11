using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyCore;

// 자동 업데이트(0.9~)의 판단·검증 부분. 네트워크와 프로세스 다루기는 참여자 앱(Updater.cs)에 있고,
// 여기에는 시험할 수 있는 순수 로직만 둔다: 안내문 서명 확인, 버전 비교, 묶음 풀기, 폴더 바꿔치기.
//
// 믿음의 근거는 하나다 — 팀의 업데이트 서명 키(ECDSA P-256). 내려받는 곳(GitHub Releases)은 믿지 않는다.
// GitHub 계정이 뚫려도 서명 없는 안내문은 버려지고, 예전에 서명한 안내문을 다시 올려도 버전이 낮아 버려진다.

/// <summary>앱이 믿는 업데이트 서명 공개키. 개인키는 저장소 밖(%USERPROFILE%\.pca-study\update-signing.private.pem).</summary>
public static class UpdateTrust
{
    /// <summary>SubjectPublicKeyInfo(base64). 키를 바꿀 때는 새 키를 "더한" 버전을 먼저 배포한 뒤 옛 키를 뺀다.</summary>
    public static readonly string[] Keys =
    {
        // 2026-11-11 정태웅 PC 에서 만듦(StudyTools update-keygen)
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1fGtKA4vO5RjWvtXN8vxLaU6yJHCwCSEf9Nm5X6tNdVVDPPMHMi7FWT160oRgONX3hOA1HsdSQowKzDGePaccw==",
    };

    public const string Repo = "https://github.com/JeongTaeWoong/pc-crash-analyzer";

    /// <summary>항상 가장 최근 정식 릴리스의 파일을 가리키는 주소(GitHub 문서: /releases/latest/download/파일명). 시험판(pre-release)은 여기에 안 잡힌다.</summary>
    public const string FeedUrl = Repo + "/releases/latest/download/update.json";

    /// <summary>묶음 주소는 이 저장소의 릴리스 파일이어야 한다(서명이 이미 주소를 묶지만 한 겹 더).</summary>
    public const string AssetPrefix = Repo + "/releases/download/";

    public static bool IsReleaseAsset(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && u.AbsoluteUri.StartsWith(AssetPrefix, StringComparison.Ordinal);
}

/// <summary>업데이트 안내문(update.json). 옆의 update.json.sig 가 이 파일 바이트 전체에 대한 서명이다.</summary>
public sealed class UpdateManifest
{
    public const string ProductName = "PcBlackBox";
    public const long MaxPackageBytes = 400L * 1024 * 1024;
    public const int MaxManifestBytes = 16 * 1024;

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("product")] public string Product { get; set; } = ProductName;
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";
    /// <summary>참여자에게 보여 줄 바뀐 점(한두 줄).</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    [JsonIgnore] public Uri? Uri => System.Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u : null;

    /// <summary>"0.9.1" 처럼 숫자 세 칸만 받는다. 뒤에 붙는 "+커밋" 은 떼고 본다(파일 버전 정보에 붙어 나온다).</summary>
    public static bool TryParseVersion(string? s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var core = s.Split('+', '-')[0].Trim();
        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        var n = new int[3];
        for (int i = 0; i < 3; i++)
            if (parts[i].Length == 0 || parts[i].Length > 5 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out n[i])) return false;
        v = new Version(n[0], n[1], n[2]);
        return true;
    }

    /// <summary>지금 버전보다 높은가. 같거나 낮으면(되돌리기 시도 포함) false.</summary>
    public bool IsNewerThan(string current) =>
        TryParseVersion(Version, out var mine) && TryParseVersion(current, out var cur) && mine > cur;

    /// <summary>
    /// 서명을 먼저 확인하고, 통과한 바이트만 해석한다. 믿는 키 가운데 하나로 서명돼 있어야 한다.
    /// </summary>
    /// <param name="urlAllowed">묶음 주소 규칙(배포본: 이 저장소의 릴리스 파일. 시험: 127.0.0.1).</param>
    public static UpdateManifest ReadVerified(byte[] manifest, string signatureBase64, IEnumerable<string> trustedKeys, Func<Uri, bool> urlAllowed)
    {
        if (manifest.Length == 0 || manifest.Length > MaxManifestBytes) throw new InvalidDataException("안내문 크기가 맞지 않음");
        byte[] sig;
        try { sig = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { throw new CryptographicException("서명 형식이 맞지 않음"); }
        if (!UpdateSigning.Verify(manifest, sig, trustedKeys)) throw new CryptographicException("업데이트 안내문의 서명이 맞지 않음");

        var m = JsonSerializer.Deserialize<UpdateManifest>(manifest) ?? throw new InvalidDataException("빈 안내문");
        var errors = m.Validate(urlAllowed);
        if (errors.Count > 0) throw new InvalidDataException("안내문 내용이 맞지 않음: " + string.Join(", ", errors));
        return m;
    }

    public List<string> Validate(Func<Uri, bool> urlAllowed)
    {
        var e = new List<string>();
        if (Schema != 1) e.Add($"schema {Schema}");
        if (Product != ProductName) e.Add("product");
        if (!TryParseVersion(Version, out _) || Version.Contains('+') || Version.Contains('-')) e.Add("version");
        if (Uri is not { } u || !urlAllowed(u)) e.Add("url");
        if (Size <= 0 || Size > MaxPackageBytes) e.Add("size");
        if (Sha256.Length != 64 || !Sha256.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'))) e.Add("sha256");
        if (Notes.Length > 600) e.Add("notes");
        return e;
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

public static class UpdateSigning
{
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string PublicKeyBase64(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static string Sign(byte[] data, ECDsa key) => Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256));

    public static bool Verify(byte[] data, byte[] signature, IEnumerable<string> trustedKeys)
    {
        foreach (var k in trustedKeys)
        {
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _);
                if (ec.VerifyData(data, signature, HashAlgorithmName.SHA256)) return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException) { /* 못 읽는 키는 건너뛴다 */ }
        }
        return false;
    }
}

/// <summary>업데이트 묶음(zip): 앱 파일이 맨 위에 그대로 들어 있다. 연구 설정(study.json)은 들어 있지 않다 — 설치본의 것을 이어 쓴다.</summary>
public static class UpdatePackage
{
    public const string MainExe = "PcBlackBox.exe";
    public const int MaxFiles = 2000;
    public const long MaxExtractedBytes = 1024L * 1024 * 1024;

    /// <summary>묶음에 넣지 않는 것: 연구 설정, 키, 디버그 기호, 팀 설정.</summary>
    public static bool Excluded(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        return name.Equals("study.json", StringComparison.OrdinalIgnoreCase) || name.Equals("study.local.json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    public static string Sha256Hex(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>앱 폴더를 묶는다(팀 도구). 항목 이름은 '/' 로, 이름순으로 넣는다. 반환: 넣은 파일 수.</summary>
    public static int Create(string appDir, string zipPath)
    {
        var root = Path.GetFullPath(appDir);
        if (!File.Exists(Path.Combine(root, MainExe))) throw new InvalidDataException($"{MainExe} 가 없는 폴더: {root}");
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !Excluded(f.Rel)).OrderBy(f => f.Rel, StringComparer.Ordinal).ToList();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (full, rel) in files) zip.CreateEntryFromFile(full, rel, CompressionLevel.Optimal);
        return files.Count;
    }

    /// <summary>
    /// 빈 폴더에 푼다. 서명으로 확인한 묶음이지만 그래도 지킨다: 폴더 밖으로 나가는 이름·절대 경로·드라이브 문자 거부,
    /// 파일 수·푼 크기 상한(머리의 크기를 믿지 않고 쓰면서 센다), 연구 설정·키는 들어 있어도 풀지 않는다. 반환: 푼 파일 수.
    /// </summary>
    public static int Extract(string zipPath, string destDir, int maxFiles = MaxFiles, long maxBytes = MaxExtractedBytes)
    {
        var dest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destDir));
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any()) throw new IOException($"풀 폴더가 비어 있지 않음: {dest}");
        Directory.CreateDirectory(dest);
        int count = 0;
        long total = 0;
        var buffer = new byte[81920];
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue; // 폴더 항목
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is "" or "." or ".."))
                throw new InvalidDataException($"묶음 안 이름이 맞지 않음: {entry.FullName}");
            if (Excluded(name)) continue;
            var target = Path.GetFullPath(Path.Combine(dest, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"폴더 밖으로 나가는 이름: {entry.FullName}");
            if (++count > maxFiles) throw new InvalidDataException($"파일이 너무 많음(상한 {maxFiles})");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = entry.Open();
            using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new InvalidDataException($"푼 크기가 너무 큼(상한 {maxBytes / 1048576}MB)");
                dst.Write(buffer, 0, n);
            }
        }
        if (!File.Exists(Path.Combine(dest, MainExe))) throw new InvalidDataException($"묶음에 {MainExe} 가 없음");
        return count;
    }
}

/// <summary>
/// 설치 폴더를 통째로 바꾼다. 새 내용을 옆(&lt;폴더&gt;.update-new)에 다 만들어 놓고 이름만 두 번 바꾸므로, 중간에 실패해도 반쯤 섞인 폴더가 남지 않는다.
/// 이전 내용은 &lt;폴더&gt;.update-old 로 남겨 되돌릴 수 있게 한다. 실행 중인 파일이 있으면 이름을 못 바꾸므로, 앱이 끝난 뒤에 부른다.
/// </summary>
public static class DirSwap
{
    // 이름을 길게 잡는다 — 참여자가 직접 만든 "…​.old" 같은 폴더와 겹쳐 그것을 지우는 일이 없게.
    public static string NewOf(string target) => Trim(target) + ".update-new";
    public static string OldOf(string target) => Trim(target) + ".update-old";
    public static string FailedOf(string target) => Trim(target) + ".update-failed";

    private static string Trim(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    /// <param name="keep">새 내용에 없으면 이전 폴더에서 가져올 파일(연구 설정).</param>
    public static void Replace(string target, string source, IEnumerable<string> keep, int retries = 20)
    {
        target = Trim(target);
        source = Trim(source);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        string fresh = NewOf(target), old = OldOf(target);
        DeleteDir(fresh);
        CopyDir(source, fresh);
        foreach (var k in keep)
        {
            string from = Path.Combine(target, k), to = Path.Combine(fresh, k);
            if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
        }
        DeleteDir(old);
        try { Move(target, old, retries); }
        catch { DeleteDir(fresh); throw; } // 아직 아무것도 안 바뀜
        try { Move(fresh, target, 5); }
        catch
        {
            Move(old, target, 5); // 원래대로
            DeleteDir(fresh);
            throw;
        }
    }

    /// <summary>되돌리기: 지금 폴더를 .update-failed 로 치우고 .update-old 를 제자리에 놓는다.</summary>
    public static void Restore(string target, int retries = 20)
    {
        target = Trim(target);
        string old = OldOf(target), failed = FailedOf(target);
        if (!Directory.Exists(old)) throw new DirectoryNotFoundException(old);
        DeleteDir(failed);
        if (Directory.Exists(target)) Move(target, failed, retries);
        try { Move(old, target, 5); }
        catch
        {
            if (Directory.Exists(failed) && !Directory.Exists(target)) Move(failed, target, 5);
            throw;
        }
        try { DeleteDir(failed); } catch { /* 다음 정리 때 */ }
    }

    /// <summary>폴더가 연결(정션·심볼릭 링크)이면 연결만 지우고 따라가지 않는다.</summary>
    public static void DeleteDir(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); return; }
        foreach (var f in info.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            if (f.IsReadOnly) f.IsReadOnly = false;
        info.Delete(recursive: true);
    }

    public static void CopyDir(string source, string dest)
    {
        source = Trim(source);
        Directory.CreateDirectory(dest);
        // 원본 안의 연결은 따라가지 않는다(Autostart.Install 과 같은 이유)
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(source, "*", walk))
        {
            var to = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: false);
        }
    }

    private static void Move(string from, string to, int retries)
    {
        for (int i = 0; ; i++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < retries)
            {
                Thread.Sleep(500); // 방금 끝난 프로세스의 파일 잠금·백신 검사가 풀리기를 기다린다
            }
        }
    }
}

/// <summary>업데이트 진행 상태(&lt;데이터 폴더&gt;\update\state.json). 수집 데이터가 아니다.</summary>
public sealed class UpdateState
{
    /// <summary>이 버전은 다시 받지 않는다(받았다가 되돌린 버전).</summary>
    [JsonPropertyName("skipVersion")] public string SkipVersion { get; set; } = "";
    /// <summary>바꿔 놓고 아직 "정상 동작"을 확인하지 못한 버전. 비어 있으면 진행 중인 업데이트 없음.</summary>
    [JsonPropertyName("pendingTo")] public string PendingTo { get; set; } = "";
    [JsonPropertyName("pendingFrom")] public string PendingFrom { get; set; } = "";
    [JsonPropertyName("pendingAt")] public DateTimeOffset? PendingAt { get; set; }
    /// <summary>마지막 결과: updated / rolled-back / failed / unconfirmed.</summary>
    [JsonPropertyName("lastResult")] public string LastResult { get; set; } = "";
    [JsonPropertyName("lastDetail")] public string LastDetail { get; set; } = "";
    [JsonPropertyName("lastAt")] public DateTimeOffset? LastAt { get; set; }

    public static string DirIn(string dataDir) => Path.Combine(dataDir, "update");
    public static string PathIn(string dataDir) => Path.Combine(DirIn(dataDir), "state.json");
    public static string HealthPathIn(string dataDir) => Path.Combine(DirIn(dataDir), "health.json");

    public static UpdateState Load(string dataDir)
    {
        try
        {
            var p = PathIn(dataDir);
            if (File.Exists(p)) return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(p)) ?? new UpdateState();
        }
        catch { /* 손상 — 새로 시작 */ }
        return new UpdateState();
    }

    public void Save(string dataDir) => AtomicFile.WriteAllText(PathIn(dataDir), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public void Result(string result, string detail)
    {
        LastResult = result;
        LastDetail = detail.Length > 300 ? detail[..300] : detail;
        LastAt = DateTimeOffset.Now;
    }
}

/// <summary>새 버전이 "수집기까지 떴다 / 못 떴다"를 적는 파일. 바꿔치기한 프로세스가 이것을 보고 그대로 둘지 되돌릴지 정한다.</summary>
public sealed class UpdateHealth
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }

    public static UpdateHealth? TryLoad(string dataDir)
    {
        try
        {
            var p = UpdateState.HealthPathIn(dataDir);
            return File.Exists(p) ? JsonSerializer.Deserialize<UpdateHealth>(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public static void Write(string dataDir, string version, bool ok, string detail) =>
        AtomicFile.WriteAllText(UpdateState.HealthPathIn(dataDir), JsonSerializer.Serialize(new UpdateHealth { Version = version, Ok = ok, Detail = detail, At = DateTimeOffset.Now }));
}
