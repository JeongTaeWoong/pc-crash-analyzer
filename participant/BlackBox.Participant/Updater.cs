using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>
/// 자동 업데이트(0.9~). 참여자가 누를 때만 바꾼다 — 새 버전이 있으면 알리고, 메뉴에서 고르면 진행한다(사용자 선택 없는 자동 조치 금지).
///
/// 순서와 각 단계의 실패 처리:
///  1) 확인   update.json + .sig 를 받아 팀 서명 키로 확인(StudyCore.UpdateManifest). 서명이 틀리거나 버전이 높지 않으면 버린다.
///  2) 준비   묶음을 기록 폴더 아래(관리자 실행이면 일반 권한이 못 쓰는 곳)에 받아 크기·SHA-256 확인 → 풀기 → 새 exe 를 --probe 로 한 번 띄워 본다.
///            여기까지 실패하면 지금 버전은 건드리지 않았다.
///  3) 넘김   수집·전송을 정상 종료하고 새 exe 를 --apply-update 로 띄운 뒤 끝난다.
///  4) 바꿈   (새 exe, UpdateApply) 설치 폴더를 이름 바꾸기로 통째로 교체 — 이전 폴더는 .update-old 로 남긴다. 실패하면 이전 버전을 다시 켠다.
///  5) 확인   새 버전을 켜고 "수집기까지 떴다"는 표시를 기다린다. 못 뜨면 .update-old 로 되돌리고 그 버전은 다시 받지 않는다.
///
/// 연구 설정(study.json)은 묶음에 없다. 설치본의 것을 이어 쓴다 — 공개된 묶음에 수신 주소·토큰·문의처가 실리지 않는다.
/// </summary>
internal sealed class Updater
{
    private readonly AppOptions _opt;
    private readonly StudyConfig _study;

    public Updater(AppOptions opt, StudyConfig study)
    {
        _opt = opt;
        _study = study;
    }

    public static string CurrentVersion => TrayApp.Version;
    public UpdateManifest? Available { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }
    public string LastError { get; private set; } = "";
    /// <summary>마지막 확인에서 안내문이 알린 버전("" = 안내문 없음)과, 그것이 되돌린 적 있어 건너뛰는 버전인지.</summary>
    public string LastOffered { get; private set; } = "";
    public bool LastOfferedSkipped { get; private set; }

    private bool TestFeed => _opt.UpdateFeed != null;
    private string FeedUrl => _opt.UpdateFeed ?? UpdateTrust.FeedUrl;

    /// <summary>시험(127.0.0.1 수신기 설정 + --update-feed)에서만 묶음 주소가 127.0.0.1 이어도 된다. 서명 키는 시험에서도 같다.</summary>
    private bool UrlAllowed(Uri u) => UpdateTrust.IsReleaseAsset(u) || (TestFeed && u.IsLoopback);

    /// <summary>
    /// 바꿀 폴더. 관리자 실행이면 보호된 설치본(Program Files)만 바꾼다 — 사용자 폴더의 exe 를 관리자 권한으로 바꿔치기하는 길을 만들지 않는다.
    /// 일반 권한이면 지금 실행 중인 폴더. 반환: (폴더, 못 하는 이유).
    /// </summary>
    public (string? Dir, string? Why) Target(bool autostartOn)
    {
        if (!Elevation.IsElevated) return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), null);
        if (!_opt.DataDirIsDefault && !_study.IsLoopbackEndpoint)
            return (null, "기록 폴더를 따로 지정해 실행한 경우에는 자동 업데이트를 쓰지 않아요.");
        var ea = new ElevatedAutostart(_opt.StartupDir);
        if (!autostartOn || !File.Exists(ea.InstalledExe))
            return (null, "아이콘 메뉴에서 \"Windows 시작 시 자동 실행\"을 켜면 업데이트할 수 있어요.\n(프로그램을 안전한 폴더에 설치해 두어야 바꿀 수 있어요)");
        return (ea.InstallDir, null);
    }

    /// <summary>새 버전이 있으면 안내문을, 없으면 null. 확인 자체가 실패하면 예외(LastError 에도 남긴다).</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct)
    {
        LastCheck = DateTimeOffset.Now;
        try
        {
            if (TestFeed && !(Uri.TryCreate(FeedUrl, UriKind.Absolute, out var f) && f.IsLoopback))
                throw new InvalidOperationException("--update-feed 는 127.0.0.1 주소만");
            using var http = NewClient(TimeSpan.FromSeconds(30));
            var manifest = await GetSmallAsync(http, FeedUrl, UpdateManifest.MaxManifestBytes, ct);
            if (manifest == null) // 아직 배포된 업데이트가 없다(404)
            {
                LastError = LastOffered = "";
                LastOfferedSkipped = false;
                Available = null;
                OpsLog.Write($"[update] 확인: 안내문 없음 current={CurrentVersion} offered=none");
                return null;
            }
            var sig = await GetSmallAsync(http, FeedUrl + ".sig", 1024, ct) ?? throw new InvalidDataException("서명 파일 없음");
            var m = UpdateManifest.ReadVerified(manifest, Encoding.ASCII.GetString(sig), UpdateTrust.Keys, UrlAllowed);
            LastError = "";
            var skip = UpdateState.Load(_opt.DataDir).SkipVersion;
            LastOffered = m.Version;
            LastOfferedSkipped = m.IsNewerThan(CurrentVersion) && m.Version == skip;
            Available = m.IsNewerThan(CurrentVersion) && m.Version != skip ? m : null;
            if (Available == null)
                OpsLog.Write($"[update] 확인: 받을 것 없음 current={CurrentVersion} offered={m.Version} skip={(skip.Length > 0 ? skip : "-")}");
            return Available;
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            throw;
        }
    }

    /// <summary>묶음을 받아 확인하고 풀어서 한 번 띄워 본다. 반환: 풀어 놓은 앱 폴더. 지금 설치본은 건드리지 않는다.</summary>
    public async Task<string> StageAsync(UpdateManifest m, string target, Action<int>? progress, CancellationToken ct)
    {
        var root = Path.Combine(UpdateState.DirIn(_opt.DataDir), m.Version);
        try { return await StageIntoAsync(root, m, target, progress, ct); }
        catch
        {
            // 확인에 실패한 묶음(수십 MB)·풀다 만 폴더를 남기지 않는다
            try { DirSwap.DeleteDir(root); } catch { }
            throw;
        }
    }

    private static async Task<string> StageIntoAsync(string root, UpdateManifest m, string target, Action<int>? progress, CancellationToken ct)
    {
        DirSwap.DeleteDir(root);
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "package.zip");

        using (var http = NewClient(TimeSpan.FromMinutes(30)))
        using (var resp = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long total = 0;
            int n, lastPct = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > m.Size) throw new InvalidDataException("받은 파일이 안내문보다 큼");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                int pct = (int)(total * 100 / m.Size);
                if (pct != lastPct) { lastPct = pct; progress?.Invoke(pct); }
            }
            if (total != m.Size) throw new InvalidDataException($"받은 크기 {total} ≠ 안내문 {m.Size}");
        }
        var hash = UpdatePackage.Sha256Hex(zip);
        if (!string.Equals(hash, m.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("받은 파일의 SHA-256 이 안내문과 다름");

        var app = Path.Combine(root, "app");
        int files = UpdatePackage.Extract(zip, app);
        File.Delete(zip);

        var exe = Path.Combine(app, UpdatePackage.MainExe);
        var fileVersion = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (!UpdateManifest.TryParseVersion(fileVersion, out var fv) || !UpdateManifest.TryParseVersion(m.Version, out var mv) || fv != mv)
            throw new InvalidDataException($"묶음 안 프로그램 버전({fileVersion})이 안내문({m.Version})과 다름");

        Probe(exe, Path.Combine(target, "study.json"), Path.Combine(root, "probe.json"), m.Version);
        OpsLog.Write($"[update] {m.Version} 준비됨 — 서명·크기·SHA-256 확인, 시험 실행 통과 files={files} probe=ok");
        return app;
    }

    /// <summary>새 exe 가 이 PC 에서 뜨는지(런타임·DLL·설정 읽기) 창 없이 한 번 확인한다.</summary>
    private static void Probe(string exe, string studyPath, string outFile, string version)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in new[] { "--probe", "--study-config", studyPath, "--out", outFile }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("시험 실행을 시작하지 못함");
        if (!p.WaitForExit(60000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException("시험 실행이 60초 안에 끝나지 않음");
        }
        if (p.ExitCode != 0) throw new InvalidOperationException($"시험 실행 실패(종료 코드 {p.ExitCode})");
        using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
        if (doc.RootElement.GetProperty("version").GetString() != version) throw new InvalidDataException("시험 실행이 알린 버전이 다름");
    }

    /// <summary>--probe: 참조 어셈블리를 모두 불러 보고 버전을 적는다. 수집은 시작하지 않는다.</summary>
    public static int ProbeSelf(AppOptions opt)
    {
        try
        {
            foreach (var name in typeof(Updater).Assembly.GetReferencedAssemblies()) Assembly.Load(name);
            _ = typeof(BlackBox.Hosting.CollectorHost).FullName;
            var json = JsonSerializer.Serialize(new { version = CurrentVersion, ok = true, elevated = Elevation.IsElevated });
            if (opt.OutFile != null) File.WriteAllText(opt.OutFile, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>준비된 새 exe 에게 교체를 넘긴다. 부른 쪽은 수집·전송을 멈춘 뒤 바로 끝나야 한다.</summary>
    public void StartApply(string stagedApp, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(stagedApp, UpdatePackage.MainExe)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stagedApp };
        foreach (var a in new[] { "--apply-update", "--target", target, "--wait-pid", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
        foreach (var a in PassThroughArgs(_opt)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("교체 프로그램을 시작하지 못함");
        OpsLog.Write($"[update] 교체를 넘김 (PID {p.Id})");
    }

    /// <summary>다음 프로세스로 이어 줄 인자: 기록 폴더(기본이 아니면)와 시험 설정.</summary>
    public static IEnumerable<string> PassThroughArgs(AppOptions o)
    {
        if (o.ShortcutDataDir != null) { yield return "--data-dir"; yield return o.ShortcutDataDir; }
        if (o.StartupDir != null) { yield return "--startup-dir"; yield return o.StartupDir; }
        if (o.UpdateFeed != null) { yield return "--update-feed"; yield return o.UpdateFeed; }
    }

    /// <summary>
    /// 시작하고 몇 분 뒤 한 번: 지난 업데이트의 뒷정리. 확인이 끝난 버전이면 이전 폴더(.update-old)와 받아 둔 묶음을 지운다.
    /// 교체 프로그램이 아직 돌고 있을 수 있는 동안(시작 직후)에는 부르지 않는다.
    /// </summary>
    public void Cleanup(bool healthy, bool autostartOn)
    {
        try
        {
            var st = UpdateState.Load(_opt.DataDir);
            if (st.PendingTo.Length > 0)
            {
                if (st.PendingTo == CurrentVersion && !healthy) return; // 확인 안 됨 — 되돌릴 재료를 남겨 둔다
                if (st.PendingTo == CurrentVersion) st.Result("updated", $"{CurrentVersion} 확인(나중에)");
                st.PendingTo = st.PendingFrom = ""; // 다른 버전의 표시는 낡은 것(직접 다시 설치함) — 지운다
                st.PendingAt = null;
                st.Save(_opt.DataDir);
            }
            var dir = UpdateState.DirIn(_opt.DataDir);
            if (Directory.Exists(dir))
                foreach (var d in Directory.GetDirectories(dir))
                    try { DirSwap.DeleteDir(d); } catch { /* 쓰는 중 — 다음에 */ }
            if (Target(autostartOn).Dir is { } target)
                foreach (var d in new[] { DirSwap.OldOf(target), DirSwap.FailedOf(target), DirSwap.NewOf(target) })
                    try { if (File.Exists(Path.Combine(d, UpdatePackage.MainExe))) DirSwap.DeleteDir(d); } catch { } // 우리 exe 가 든 것만
        }
        catch (Exception ex) { OpsLog.Write($"[update] 정리 실패: {ex.Message}"); }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PcBlackBox/{CurrentVersion}");
        return http;
    }

    /// <summary>작은 파일을 상한까지만 읽는다. 404 면 null.</summary>
    private static async Task<byte[]?> GetSmallAsync(HttpClient http, string url, int max, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new InvalidDataException("받은 파일이 너무 큼");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// --apply-update: 받아 둔 새 버전의 exe 가 자기 폴더 내용으로 설치 폴더를 바꾸고, 새 버전을 켜서 지켜본다.
/// 창을 띄우지 않는다. 결과는 기록 폴더의 update\state.json 에 남기고, 다음에 켜진 앱이 읽어 알림·하트비트로 전한다.
/// </summary>
internal static class UpdateApply
{
    private static readonly TimeSpan HealthWait = TimeSpan.FromSeconds(180);

    public static int Run(AppOptions opt)
    {
        var st = UpdateState.Load(opt.DataDir);
        string newVer = Updater.CurrentVersion, oldVer = "?";
        string target = opt.UpdateTarget ?? "";
        try
        {
            WaitForExit(opt.WaitPid, TimeSpan.FromSeconds(150));
            target = CheckedTarget(opt);
            var installed = VersionOf(Path.Combine(target, UpdatePackage.MainExe));
            if (!UpdateManifest.TryParseVersion(installed, out var ov) || !UpdateManifest.TryParseVersion(newVer, out var nv) || nv <= ov)
                throw new InvalidOperationException($"설치된 버전({installed})보다 높지 않아 바꾸지 않음");
            oldVer = ov.ToString(3);
            DirSwap.Replace(target, AppContext.BaseDirectory, new[] { "study.json" });
        }
        catch (Exception ex)
        {
            // 설치 폴더는 그대로다 — 이전 버전을 다시 켠다
            Save(opt, st, s => s.Result("failed", $"{newVer} 로 바꾸지 못함: {ex.GetType().Name}: {ex.Message}"));
            if (target.Length > 0) TryStart(target, opt, "--update-failed");
            return 4;
        }

        try { File.Delete(UpdateState.HealthPathIn(opt.DataDir)); } catch { }
        Save(opt, st, s => { s.PendingTo = newVer; s.PendingFrom = oldVer; s.PendingAt = DateTimeOffset.Now; });
        var app = TryStart(target, opt, "--updated-from", oldVer);

        var (ok, why) = WaitHealth(app, opt.DataDir, newVer);
        if (ok == true)
        {
            Save(opt, st, s => { s.PendingTo = s.PendingFrom = ""; s.PendingAt = null; s.Result("updated", $"{oldVer} → {newVer}"); });
            try { DirSwap.DeleteDir(DirSwap.OldOf(target)); } catch { /* 다음 정리 때 */ }
            return 0;
        }
        if (ok == null)
        {
            // 아직 뜨는 중일 수 있다 — 멀쩡한 앱을 죽이지 않는다. 이전 폴더는 남겨 두고, 앱이 나중에 스스로 확인한다.
            Save(opt, st, s => s.Result("unconfirmed", $"{newVer}: {why}"));
            return 0;
        }

        try
        {
            if (app != null && !app.HasExited) { try { app.Kill(); } catch { } app.WaitForExit(15000); }
            DirSwap.Restore(target);
            Save(opt, st, s => { s.SkipVersion = newVer; s.PendingTo = s.PendingFrom = ""; s.PendingAt = null; s.Result("rolled-back", $"{newVer} → {oldVer} 로 되돌림: {why}"); });
            TryStart(target, opt, "--update-rolled-back", newVer);
            return 5;
        }
        catch (Exception ex)
        {
            Save(opt, st, s => s.Result("failed", $"{newVer} 되돌리기 실패: {ex.GetType().Name}: {ex.Message} (원인: {why})"));
            return 6;
        }
    }

    /// <summary>
    /// 바꿀 폴더가 우리 설치 폴더인지 확인한다. 관리자 실행(시험 설정 제외)에서는 Program Files 의 설치본만, 그리고 이 exe 가
    /// 보호된 기록 폴더 아래에서 돌 때만 — 다른 프로그램이 이 기능을 빌려 관리자 권한으로 엉뚱한 폴더를 바꾸지 못하게.
    /// </summary>
    private static string CheckedTarget(AppOptions opt)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(opt.UpdateTarget ?? throw new ArgumentException("--target 필요")));
        var studyPath = Path.Combine(target, "study.json");
        if (!File.Exists(Path.Combine(target, UpdatePackage.MainExe)) || !File.Exists(studyPath))
            throw new InvalidOperationException("PC 블랙박스 설치 폴더가 아님");
        if (new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("설치 폴더가 연결(재분석 지점)임");
        bool test = StudyConfig.Load(studyPath).IsLoopbackEndpoint;
        if (Elevation.IsElevated && !test)
        {
            var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(new ElevatedAutostart(null).InstallDir));
            if (!string.Equals(target, install, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("관리자 실행에서는 설치본만 바꿀 수 있음");
            var me = Path.GetFullPath(AppContext.BaseDirectory);
            if (!me.StartsWith(Elevation.SecureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("보호된 폴더에서 실행되지 않음");
        }
        return target;
    }

    private static (bool? Ok, string Why) WaitHealth(Process? app, string dataDir, string version)
    {
        if (app == null) return (false, "새 버전을 켜지 못함");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < HealthWait)
        {
            if (UpdateHealth.TryLoad(dataDir) is { } h && h.Version == version)
                return h.Ok ? (true, "") : (false, $"수집기 시작 실패: {h.Detail}");
            if (app.HasExited) return (false, $"새 버전이 {sw.Elapsed.TotalSeconds:0}초 만에 꺼짐(종료 코드 {app.ExitCode})");
            Thread.Sleep(1000);
        }
        return (null, $"{HealthWait.TotalSeconds:0}초 안에 시작 확인을 받지 못함");
    }

    private static void WaitForExit(int pid, TimeSpan max)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit((int)max.TotalMilliseconds)) throw new TimeoutException("이전 버전이 끝나지 않음");
        }
        catch (ArgumentException) { /* 이미 끝남 */ }
    }

    private static Process? TryStart(string target, AppOptions opt, params string[] extra)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(target, UpdatePackage.MainExe)) { UseShellExecute = false, WorkingDirectory = target };
            psi.ArgumentList.Add("--autostart");
            foreach (var a in extra) psi.ArgumentList.Add(a);
            foreach (var a in Updater.PassThroughArgs(opt)) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }
        catch { return null; }
    }

    private static string VersionOf(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? ""; } catch { return ""; }
    }

    private static void Save(AppOptions opt, UpdateState st, Action<UpdateState> change)
    {
        change(st);
        try { st.Save(opt.DataDir); } catch { }
    }
}
