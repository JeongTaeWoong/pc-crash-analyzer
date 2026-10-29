// 팀 전용 연구 도구 — 참여자 배포본에는 들어가지 않는다.
//   keygen   팀 키 쌍 만들기(개인키는 저장소 밖), study.local.json 에 공개키·토큰 기록
//   check    설정 검증
//   receive  127.0.0.1 시험 수신기 (Apps Script 와 같은 규칙, --mimic-apps-script 로 302 리디렉션 흉내)
//   decrypt  받은 봉투(*.pcae.json) → 참여자별 덤프 폴더
//   summary  복호화한 폴더 요약(참여자별 최근 하트비트·기록 수·철회 여부)
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StudyCore;

Console.OutputEncoding = Encoding.UTF8;
var jsonOut = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (args.Length == 0) return Usage();
var cmd = args[0];
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);
string DefaultKeyDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pca-study");

try
{
    return cmd switch
    {
        "keygen" => Keygen(),
        "check" => Check(),
        "receive" => await Receive(),
        "decrypt" => Decrypt(),
        "summary" => Summary(),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine("""
        StudyTools <명령>
          keygen  --config participant/study.local.json [--key-dir DIR] [--endpoint URL] [--contact TEXT] [--rotate]
          check   --config FILE
          receive --config FILE --out DIR [--port 8787] [--mimic-apps-script] [--max-posts N]
          decrypt --in DIR --out DIR [--key-dir DIR]
          summary --in DIR
        """);
    return 64;
}

// ───────── keygen ─────────
int Keygen()
{
    var configPath = Path.GetFullPath(Opt("--config") ?? throw new ArgumentException("--config 필요"));
    var keyDir = Path.GetFullPath(Opt("--key-dir") ?? DefaultKeyDir());
    if (InsideGitRepo(keyDir))
        throw new InvalidOperationException($"개인키 폴더가 git 저장소 안이다: {keyDir} — 저장소 밖(기본 %USERPROFILE%\\.pca-study)을 쓴다");

    var cfg = File.Exists(configPath) ? StudyConfig.Load(configPath) : new StudyConfig();
    if (!string.IsNullOrEmpty(cfg.PublicKeyPem) && !Flag("--rotate"))
        throw new InvalidOperationException("이미 공개키가 있다. 새 키로 바꾸려면 --rotate (이전 개인키 파일은 지우지 말 것 — 이전 기록 복호화에 필요)");

    using var rsa = RSA.Create(3072);
    var kid = StudyConfig.ComputeKeyId(rsa);
    Directory.CreateDirectory(keyDir);
    var keyPath = Path.Combine(keyDir, $"{kid}.private.pem");
    File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

    cfg.PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
    cfg.KeyId = kid;
    if (string.IsNullOrEmpty(cfg.Token)) cfg.Token = "t_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    if (Opt("--endpoint") is { } ep) cfg.Endpoint = ep;
    if (Opt("--contact") is { } c) cfg.Contact = c;
    cfg.Save(configPath);

    Console.WriteLine($"""
        [keygen] 키 ID {kid}
          개인키: {keyPath}
            - 이 파일이 없으면 받은 기록을 아무도 열 수 없다 → USB 등으로 백업.
            - 팀원(분석 담당)에게만 직접 전달. 채팅·메일·저장소에 올리지 않는다.
          설정:   {configPath} (공개키·토큰 기록, 커밋 금지 — .gitignore)
          토큰:   {cfg.Token}  → Apps Script 스크립트 속성 TOKEN 에 같은 값
        """);
    var errors = cfg.Validate();
    if (errors.Count > 0) Console.WriteLine("  남은 설정: " + string.Join(" / ", errors));
    return 0;
}

static bool InsideGitRepo(string dir)
{
    for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return true;
    return false;
}

// ───────── check ─────────
int Check()
{
    var cfg = StudyConfig.Load(Opt("--config") ?? throw new ArgumentException("--config 필요"));
    var errors = cfg.Validate();
    Console.WriteLine(errors.Count == 0
        ? $"[check] OK — 받는 곳 {cfg.EndpointUri?.Host}, 키 {cfg.KeyId}, 연구 {cfg.StudyId}{(cfg.IsLoopbackEndpoint ? " (시험 수신기)" : "")}"
        : "[check] 문제:\n  - " + string.Join("\n  - ", errors));
    return errors.Count == 0 ? 0 : 1;
}

// ───────── receive (시험 수신기) ─────────
async Task<int> Receive()
{
    var cfg = StudyConfig.Load(Opt("--config") ?? throw new ArgumentException("--config 필요"));
    var outDir = Path.GetFullPath(Opt("--out") ?? throw new ArgumentException("--out 필요"));
    int port = int.Parse(Opt("--port") ?? "8787");
    bool mimic = Flag("--mimic-apps-script");
    int maxPosts = int.Parse(Opt("--max-posts") ?? "0");
    Directory.CreateDirectory(outDir);

    var echoes = new ConcurrentDictionary<string, string>();
    // Apps Script(Code.gs)와 같은 6시간 칸 상한: 참여자 150건·50MB, 전체 2000건·300MB
    var counters = new ConcurrentDictionary<string, long>();
    int posts = 0;
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    Console.WriteLine($"[receive] http://127.0.0.1:{port}/ 대기 — 저장 {outDir}{(mimic ? " (Apps Script 302 흉내)" : "")}");

    while (maxPosts == 0 || posts < maxPosts)
    {
        using var client = await listener.AcceptTcpClientAsync();
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 30000;
            var (method, path, body) = ReadRequest(stream);
            string status = "200 OK", reply;
            string? location = null;
            if (method == "GET" && path.StartsWith("/echo/") && echoes.TryRemove(path[6..], out var saved))
                reply = saved;
            else if (method == "GET")
                reply = """{"ok":true,"service":"pca-receiver-local","v":1}""";
            else if (method == "POST")
            {
                posts++;
                reply = Store(body);
                if (mimic)
                {
                    var id = Guid.NewGuid().ToString("N")[..12];
                    echoes[id] = reply;
                    status = "302 Moved Temporarily";
                    location = $"http://127.0.0.1:{port}/echo/{id}";
                }
            }
            else { status = "405 Method Not Allowed"; reply = """{"ok":false,"error":"method"}"""; }

            var bytes = Encoding.UTF8.GetBytes(reply);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n" +
                       (location != null ? $"Location: {location}\r\n" : "") + "\r\n";
            stream.Write(Encoding.ASCII.GetBytes(head));
            stream.Write(bytes);
            Console.WriteLine($"[receive] {method} {path} → {status.Split(' ')[0]} {reply}");
        }
        catch (Exception ex) { Console.WriteLine($"[receive] 요청 처리 실패: {ex.Message}"); }
    }
    listener.Stop();
    return 0;

    string Store(string body)
    {
        var (ok, error, h) = ReceiverRules.Validate(body, cfg.Token, cfg.StudyId);
        if (!ok || h == null) return JsonSerializer.Serialize(new { ok = false, error });
        long bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 21600;
        long Add(string k, long v) => counters.AddOrUpdate($"{bucket}:{k}", v, (_, n) => n + v);
        if (counters.GetValueOrDefault($"{bucket}:n_{h.Pid}") >= 150 || counters.GetValueOrDefault($"{bucket}:g") >= 2000 ||
            counters.GetValueOrDefault($"{bucket}:b_{h.Pid}") + body.Length > 50L * 1024 * 1024 ||
            counters.GetValueOrDefault($"{bucket}:gb") + body.Length > 300L * 1024 * 1024)
            return """{"ok":false,"error":"rate_limited"}""";
        Add($"n_{h.Pid}", 1); Add("g", 1); Add($"b_{h.Pid}", body.Length); Add("gb", body.Length);
        var dir = Path.Combine(outDir, h.Pid);
        Directory.CreateDirectory(dir);
        var name = ReceiverRules.StoredFileName(h);
        File.WriteAllText(Path.Combine(dir, name), body, new UTF8Encoding(false));
        return JsonSerializer.Serialize(new { ok = true, stored = name });
    }
}

static (string Method, string Path, string Body) ReadRequest(NetworkStream s)
{
    // 머리: \r\n\r\n 까지 바이트 단위로 읽고, 본문은 Content-Length 만큼
    var head = new List<byte>();
    while (head.Count < 65536)
    {
        int b = s.ReadByte();
        if (b < 0) break;
        head.Add((byte)b);
        int n = head.Count;
        if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
    }
    var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
    var first = lines[0].Split(' ');
    int len = 0;
    foreach (var l in lines.Skip(1))
        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(l[15..].Trim());
    if (len > ReceiverRules.MaxBodyChars * 2) throw new InvalidDataException("본문이 너무 크다");
    var body = new byte[len];
    int read = 0;
    while (read < len)
    {
        int n = s.Read(body, read, len - read);
        if (n <= 0) break;
        read += n;
    }
    return (first[0], first.Length > 1 ? first[1] : "/", Encoding.UTF8.GetString(body, 0, read));
}

// ───────── decrypt ─────────
// 받은 봉투는 인터넷에서 온 신뢰할 수 없는 입력이다(배포 zip 의 공개키·토큰으로 누구나 만들 수 있다). 그래서:
//  - 이름·참여 ID 는 앱이 만드는 형식만(ReceiverRules), 쓸 경로가 참여자 폴더 밖이면 거부
//  - zip 안 파일 이름은 종류별 허용 목록만
//  - 서명 확인 후, 참여자별로 처음 본 서명 키를 <out>/<pid>/signer.spk 에 고정(TOFU). 다른 키로 온 봉투는 위장 의심으로 거부
int Decrypt()
{
    var inDir = Path.GetFullPath(Opt("--in") ?? throw new ArgumentException("--in 필요"));
    var outDir = Path.GetFullPath(Opt("--out") ?? throw new ArgumentException("--out 필요"));
    var keyDir = Path.GetFullPath(Opt("--key-dir") ?? DefaultKeyDir());
    var keys = new Dictionary<string, RSA>(StringComparer.OrdinalIgnoreCase);
    foreach (var f in Directory.Exists(keyDir) ? Directory.GetFiles(keyDir, "*.private.pem") : Array.Empty<string>())
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(f));
        keys[StudyConfig.ComputeKeyId(rsa)] = rsa;
    }
    if (keys.Count == 0) throw new InvalidOperationException($"개인키 없음: {keyDir}\\*.private.pem");

    var allowed = new Dictionary<string, string[]>
    {
        ["dump"] = new[] { "meta.json", "events.json", "snapshot.json", "engine-snapshot.json", "upload.json" },
        ["annotation"] = new[] { "report.json" },
        ["heartbeat"] = new[] { "heartbeat.json" },
        ["withdraw"] = new[] { "withdraw.json" },
    };

    int dumps = 0, notes = 0, beats = 0, withdraws = 0, skipped = 0, failed = 0;
    void Fail(string file, string why) { failed++; Console.WriteLine($"[decrypt] 실패 {Path.GetFileName(file)}: {why}"); }

    // 1) 머리·서명 확인(복호화 전)
    var envs = new List<(string File, string Json, EnvelopeHeader H, string Spk)>();
    foreach (var file in Directory.EnumerateFiles(inDir, "*.pcae.json", SearchOption.AllDirectories))
    {
        try
        {
            var json = File.ReadAllText(file);
            var h = Envelope.ReadHeader(json);
            if (!ReceiverRules.PidRx.IsMatch(h.Pid) || !ReceiverRules.NameRx.IsMatch(h.Name) || !allowed.ContainsKey(h.Kind))
                throw new InvalidDataException("머리 형식 위반");
            envs.Add((file, json, h, Envelope.VerifySignature(json)));
        }
        catch (Exception ex) { Fail(file, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    // 2) 참여자별 서명 키 고정
    var trusted = new Dictionary<string, string>();
    foreach (var g in envs.GroupBy(e => e.H.Pid))
    {
        var pdir = Path.Combine(outDir, g.Key);
        var signerFile = Path.Combine(pdir, "signer.spk");
        string? pinned = File.Exists(signerFile) ? File.ReadAllText(signerFile).Trim() : null;
        var byKey = g.GroupBy(e => e.Spk).OrderByDescending(k => k.Count()).ToList();
        if (pinned == null)
        {
            pinned = byKey[0].Key;
            if (byKey.Count > 1)
                Console.WriteLine($"[decrypt] ★ 서명 키 충돌: 참여 ID {g.Key} 에 서로 다른 서명 키 {byKey.Count}개({string.Join(", ", byKey.Select(k => k.Count()))}건) — 가장 많은 쪽만 받고 나머지는 위장 의심으로 거부. 참여자에게 확인할 것");
            Directory.CreateDirectory(pdir);
            File.WriteAllText(signerFile, pinned);
        }
        trusted[g.Key] = pinned;
    }

    // 3) 복호화 (같은 참여자 안에서는 만든 시각 순 — 메모는 마지막 것이 report.json)
    foreach (var (file, json, h, spk) in envs.OrderBy(e => e.H.Pid, StringComparer.Ordinal).ThenBy(e => e.H.CreatedAt, StringComparer.Ordinal))
    {
        try
        {
            if (spk != trusted[h.Pid]) { Fail(file, "고정된 서명 키와 다름 — 위장 의심"); continue; }
            if (!keys.TryGetValue(h.Kid, out var key)) throw new InvalidDataException($"키 {h.Kid} 없음");
            var pdir = Path.GetFullPath(Path.Combine(outDir, h.Pid));
            string Inside(params string[] parts)
            {
                var full = Path.GetFullPath(Path.Combine(new[] { pdir }.Concat(parts).ToArray()));
                if (!full.StartsWith(pdir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"참여자 폴더 밖 경로: {full}");
                return full;
            }
            string marker = h.Kind switch
            {
                "dump" => Inside(h.Name, "meta.json"),
                "annotation" => Inside(h.Name, $"report.{Safe(h.CreatedAt)}.json"),
                "heartbeat" => Inside("heartbeats", h.Name + ".json"),
                _ => Inside("withdraw", h.Name + ".json"),
            };
            if (File.Exists(marker)) { skipped++; continue; }

            var (_, zip, _) = Envelope.Open(json, key); // 서명·머리·본문이 바뀌었으면 여기서 실패
            var files = BundleBuilder.Extract(zip);
            foreach (var n in files.Keys.Where(n => !allowed[h.Kind].Contains(n, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                Console.WriteLine($"[decrypt] 경고 {Path.GetFileName(file)}: 허용 목록 밖 항목 {n} 무시");
                files.Remove(n);
            }
            switch (h.Kind)
            {
                case "dump":
                    if (!files.ContainsKey("meta.json")) throw new InvalidDataException("meta.json 없음");
                    Directory.CreateDirectory(Inside(h.Name));
                    foreach (var (n, data) in files) File.WriteAllBytes(Inside(h.Name, n), data);
                    dumps++;
                    break;
                case "annotation":
                    var rep = files.GetValueOrDefault("report.json") ?? throw new InvalidDataException("report.json 없음");
                    Directory.CreateDirectory(Inside(h.Name));
                    File.WriteAllBytes(marker, rep);
                    File.WriteAllBytes(Inside(h.Name, "report.json"), rep); // 가장 최근 메모(만든 시각 순)
                    notes++;
                    break;
                case "heartbeat":
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllBytes(marker, files.GetValueOrDefault("heartbeat.json") ?? throw new InvalidDataException("heartbeat.json 없음"));
                    beats++;
                    break;
                case "withdraw":
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllBytes(marker, files.GetValueOrDefault("withdraw.json") ?? throw new InvalidDataException("withdraw.json 없음"));
                    withdraws++;
                    Console.WriteLine($"[decrypt] ★ 참여 철회 요청: {h.Pid} ({h.CreatedAt}, 서명 확인됨) — 이 참여자의 Drive 폴더와 복호화본을 지울 것");
                    break;
            }
        }
        catch (Exception ex) { Fail(file, $"{ex.GetType().Name}: {ex.Message}"); }
    }
    Console.WriteLine($"[decrypt] 덤프 {dumps} · 메모 {notes} · 하트비트 {beats} · 철회 {withdraws} · 이미 처리 {skipped} · 실패 {failed} → {outDir}");
    return failed == 0 ? 0 : 1;
}

static string Safe(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

// ───────── summary ─────────
int Summary()
{
    var inDir = Path.GetFullPath(Opt("--in") ?? throw new ArgumentException("--in 필요"));
    static string Cell(string? s) => (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
    var sb = new StringBuilder();
    sb.AppendLine("| 참여 ID | 별명 | 마지막 하트비트 | 버전 | 수집기 CPU% | 기록(자동/수동) | 종류 | 메모 | 철회 |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
    foreach (var p in Directory.EnumerateDirectories(inDir).OrderBy(x => x))
    {
        var pid = Path.GetFileName(p);
        if (!ReceiverRules.PidRx.IsMatch(pid)) continue;
        try
        {
            string nick = "", last = "-", app = "-", cpu = "-";
            var hbDir = Path.Combine(p, "heartbeats");
            // 이름(hb-yyyyMMdd-HHmmss) 순 마지막 = 가장 최근
            var lastHb = Directory.Exists(hbDir) ? Directory.GetFiles(hbDir, "hb-*.json").OrderBy(x => x, StringComparer.Ordinal).LastOrDefault() : null;
            if (lastHb != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(lastHb));
                    var r = doc.RootElement;
                    nick = r.TryGetProperty("nickname", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    last = r.TryGetProperty("at", out var at) && at.TryGetDateTimeOffset(out var atv) ? atv.ToString("MM-dd HH:mm") : "-";
                    app = r.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "-" : "-";
                    if (r.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object &&
                        self.TryGetProperty("cpuPctOfMachine", out var c) && c.TryGetDouble(out var cv)) cpu = cv.ToString("0.###");
                }
                catch (Exception ex) { nick = $"(하트비트 읽기 실패: {ex.GetType().Name})"; }
            }
            int auto = 0, manual = 0, memos = 0, bad = 0;
            var kinds = new Dictionary<string, int>();
            foreach (var d in Directory.EnumerateDirectories(p))
            {
                var meta = Path.Combine(d, "meta.json");
                if (!File.Exists(meta)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                    var kind = doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "?" : "?";
                    if (kind == "manual") manual++; else auto++;
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                    if (File.Exists(Path.Combine(d, "report.json"))) memos++;
                }
                catch { bad++; }
            }
            bool withdrawn = Directory.Exists(Path.Combine(p, "withdraw"));
            var kindText = string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")) + (bad > 0 ? $", 읽기 실패×{bad}" : "");
            sb.AppendLine($"| {pid} | {Cell(nick)} | {last} | {Cell(app)} | {cpu} | {auto}/{manual} | {Cell(kindText)} | {memos} | {(withdrawn ? "**철회**" : "")} |");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"| {pid} | (요약 실패: {Cell(ex.GetType().Name)}) | | | | | | | |");
        }
    }
    Console.WriteLine(sb.ToString());
    return 0;
}
