using System.Diagnostics;
using System.IO;
using System.Text;

namespace CrashAnalyzer.UI.Services;

// 시연: 문제 일으키기 — 2026-11-18 추가 (교수님 앞 시연용)
//
// 저장소의 시험 프로그램 FaultLab(collector/experiments/FaultLab)으로 문제를 일부러 일으키고,
// UI 에서 켠 수집기가 그 순간을 기록하면 앱이 새 기록을 자동으로 검진한다.
//   * 튕김·응답 없음은 Windows 이벤트 로그에 남으므로 수집기가 스스로 감지한다.
//   * 느려짐(CPU·디스크·메모리)은 로그에 남지 않으므로, 부하가 걸린 뒤 수집기에 "m slow FaultLab.exe" 를 보내 기록하게 한다
//     (사용자가 Ctrl+Shift+F12 를 누르는 것과 같다).
// FaultLab 은 자기 프로세스 안에서만 문제를 일으키고 시스템 설정을 바꾸지 않는다. 디스크 모드의 임시 파일은 끝나면 지운다.
public sealed class DemoFault
{
    /// <summary>faultlab = 시험 프로그램, godot = 실제 프로그램(Godot) 버그·고친 버전 (2026-11-18 추가)</summary>
    public string Kind { get; init; } = "faultlab";
    /// <summary>godot 일 때: demo-godot 폴더 안의 실행 파일 상대 경로</summary>
    public string GodotExe { get; init; } = "";
    /// <summary>godot 일 때 보여 주는 방식 (2026-11-18 추가): headless = 창 없이, editor = 편집기를 열자마자 가져오기,
    /// editor-drop = 그림을 뺀 프로젝트로 편집기를 열고, 사용자가 그림을 끌어다 넣는다</summary>
    public string Mode { get; init; } = "headless";
    /// <summary>문제가 없어야 하는 실행(고친 버전 확인)인가</summary>
    public bool ExpectOk { get; init; }
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string[] Args { get; init; } = Array.Empty<string>();
    /// <summary>느려짐처럼 로그에 안 남는 문제: 수집기에 보낼 마킹 증상(slow 등). 비면 수집기가 스스로 감지한다.</summary>
    public string MarkSymptom { get; init; } = "";
    /// <summary>이 표식 줄(FaultLab 출력)이 나온 뒤 MarkDelaySec 초가 지나면 마킹한다</summary>
    public string MarkAfterLine { get; init; } = "";
    public int MarkDelaySec { get; init; }
    /// <summary>문제가 일어난 뒤 검진 결과가 나올 때까지 대략 걸리는 시간(초) — 안내 문구용</summary>
    public int ExpectSec { get; init; }
}

public static class DemoFaults
{
    public const string ProcessName = "FaultLab.exe";

    public static readonly DemoFault[] All =
    {
        new()
        {
            Id = "crash", Label = "프로그램 튕김",
            Description = "5초 뒤 시험 프로그램이 스스로 꺼져요. 수집기가 바로 알아채요.",
            Args = new[] { "crash-b" }, ExpectSec = 45,
        },
        new()
        {
            Id = "hang", Label = "응답 없음(멈춤)",
            Description = "창을 띄운 뒤 멈추고, 6초 뒤 Windows 에 '응답 없음'으로 알리고 꺼져요.",
            Args = new[] { "hang-report", "60", "6" }, ExpectSec = 45,
        },
        new()
        {
            Id = "leak", Label = "메모리 누수(느려짐)",
            Description = "메모리를 1.5GB까지 조금씩 차지한 뒤 그대로 들고 있어요.",
            Args = new[] { "leak", "1536", "60", "50" }, MarkSymptom = "slow", MarkAfterLine = "LOAD_READY_AT=", MarkDelaySec = 3, ExpectSec = 60,
        },
        new()
        {
            Id = "cpu", Label = "CPU 과부하(느려짐)",
            Description = "40초 동안 CPU 를 모두 써요.",
            Args = new[] { "cpu", "40" }, MarkSymptom = "slow", MarkAfterLine = "LOAD_AT=", MarkDelaySec = 12, ExpectSec = 50,
        },
        new()
        {
            Id = "disk", Label = "디스크 과부하(느려짐)",
            Description = "40초 동안 임시 파일을 쓰고 읽기를 반복해요(끝나면 지워요).",
            Args = new[] { "disk", "40", "512" }, MarkSymptom = "slow", MarkAfterLine = "LOAD_AT=", MarkDelaySec = 12, ExpectSec = 50,
        },
    };

    // ─── 실제 프로그램 버그: Godot 4.3 이 JPEG 이 든 SVG 를 가져오다 꺼짐 (godotengine/godot#98341, 4.5 에서 고쳐짐) ───
    // Godot 실행 파일은 저장소에 넣지 않는다(130~160MB). 저장소 옆의 demo-godot 폴더(또는 CE_DEMO_GODOT)에 둔다:
    //   demo-godot\godot\Godot_v4.3-stable_win64.exe, demo-godot\godot-fixed\Godot_v4.5-stable_win64.exe, demo-godot\project-original\
    public static readonly DemoFault[] Real =
    {
        new()
        {
            Kind = "godot", Id = "godot43", Label = "실제 버그: Godot 4.3 그림 가져오기 튕김",
            Description = "게임 엔진 Godot 4.3 이 사진(JPEG)이 들어 있는 SVG 그림을 가져오다 꺼지는 알려진 버그(#98341)예요. 창 없이 실행돼요.",
            GodotExe = @"godot\Godot_v4.3-stable_win64.exe", ExpectSec = 45,
        },
        new()
        {
            Kind = "godot", Id = "godot45", Label = "고친 버전으로 확인: Godot 4.5 같은 작업",
            Description = "버그가 고쳐진 Godot 4.5 로 같은 프로젝트를 가져와요. 정상으로 끝나면 '업데이트'가 해결책임을 보여 줘요.",
            GodotExe = @"godot-fixed\Godot_v4.5-stable_win64.exe", ExpectOk = true, ExpectSec = 15,
        },
    };

    // ─── 시연용 '업데이트' (2026-11-18 추가) ───
    // 엔진 점검 "프로그램 업데이트 후 재현"의 조치(update:Godot_v4.3-stable_win64.exe)를 실행기가 실행할 수 있게,
    // demo-godot\installed.txt 에 '지금 설치된 버전'(4.3/4.5)을 적어 둔다. 실제 PC 의 프로그램은 바꾸지 않는다.
    // 실행기는 바꾸기 전 버전을 기록해 두고, 되돌리기 때 그 버전으로 다시 적는다.
    public const string DemoAppExe = "Godot_v4.3-stable_win64.exe";
    public const string BuggyVersion = "4.3";
    public const string FixedVersion = "4.5";

    /// <summary>MainWindow 가 시작할 때 채운다 (없으면 null)</summary>
    public static string? DemoRoot { get; set; }

    public static string InstalledVersion(string root)
    {
        try
        {
            string f = Path.Combine(root, "installed.txt");
            if (File.Exists(f) && File.ReadAllText(f).Trim() == FixedVersion) return FixedVersion;
        }
        catch (IOException) { }
        return BuggyVersion;
    }

    public static void SetInstalledVersion(string root, string version) =>
        File.WriteAllText(Path.Combine(root, "installed.txt"), version == FixedVersion ? FixedVersion : BuggyVersion);

    /// <summary>지금 설치된 버전으로 실행하는 항목</summary>
    public static DemoFault InstalledFault(string root) => InstalledVersion(root) == FixedVersion ? Real[1] : Real[0];

    /// <summary>지금 설치된 버전 + 보여 주는 방식 (headless / editor / editor-drop)</summary>
    public static DemoFault InstalledFault(string root, string mode)
    {
        DemoFault b = InstalledFault(root);
        string how = mode switch
        {
            "editor" => " 편집기 화면을 띄우고, 프로젝트를 여는 동안 그림을 가져와요. 다 보면 편집기를 닫아 주세요.",
            "editor-drop" => " 그림을 뺀 프로젝트로 편집기를 열고 그림 폴더도 함께 열어요. 그림(.svg)을 Godot 왼쪽 아래 '파일 시스템' 칸으로 끌어다 놓으세요. 다 보면 편집기를 닫아 주세요.",
            _ => "",
        };
        return new DemoFault
        {
            Kind = b.Kind, Id = b.Id + "-" + mode, Label = b.Label, Description = b.Description + how,
            GodotExe = b.GodotExe, ExpectOk = b.ExpectOk, ExpectSec = b.ExpectSec, Mode = mode,
        };
    }

    public static string ModeKo(string mode) => mode switch
    {
        "editor" => "편집기 화면 · 열자마자",
        "editor-drop" => "편집기 화면 · 그림 직접 넣기",
        _ => "창 없이",
    };

    /// <summary>update:Godot_v4.3-stable_win64.exe 처럼 시연용 Godot 를 고친 버전으로 바꾸는 조치인가</summary>
    public static bool IsDemoUpdate(string automation)
    {
        if (string.IsNullOrWhiteSpace(automation)) return false;
        string[] parts = automation.Split(':');
        if (parts.Length < 2 || parts[0].Trim() != "update") return false;
        string subject = Path.GetFileName(parts[1].Trim().Replace('/', '\\'));
        if (!subject.Equals(DemoAppExe, StringComparison.OrdinalIgnoreCase)) return false;
        return DemoRoot is not null && File.Exists(Path.Combine(DemoRoot, Real[1].GodotExe));
    }

    /// <summary>demo-godot 폴더: 환경 변수 CE_DEMO_GODOT → 저장소 옆 → 저장소 안</summary>
    public static string? GodotRoot(string repoRoot)
    {
        string? env = Environment.GetEnvironmentVariable("CE_DEMO_GODOT");
        string[] tries = { env ?? "", Path.Combine(repoRoot, "..", "demo-godot"), Path.Combine(repoRoot, "demo-godot") };
        for (int i = 0; i < tries.Length; i++)
            if (tries[i].Length > 0 && Directory.Exists(Path.Combine(tries[i], "project-original")))
                return Path.GetFullPath(tries[i]);
        return null;
    }

    /// <summary>Godot 실행: 매번 원본 프로젝트를 임시 폴더에 새로 복사해서(가져오기 캐시 없이) 같은 일을 시킨다</summary>
    /// <param name="surviveSec">0 보다 크면(자동 시연의 다시 확인): 편집기가 이 시간 동안 꺼지지 않으면 그림을 문제없이 가져온 것으로 보고
    /// 편집기를 닫은 뒤 0 을 돌려준다 (2026-11-18 추가)</param>
    public static async Task<int> RunGodotAsync(string godotRoot, DemoFault fault, Action<string> progress, CancellationToken ct = default,
                                                int surviveSec = 0)
    {
        string exe = Path.Combine(godotRoot, fault.GodotExe);
        if (!File.Exists(exe)) throw new FileNotFoundException("Godot 실행 파일이 없어요: " + exe);
        string work = Path.Combine(Path.GetTempPath(), "pc_crash_analyzer", "demo-godot", "game-project");
        string temp = Path.Combine(Path.GetTempPath(), "pc_crash_analyzer", "demo-godot");
        string drop = Path.Combine(temp, "끌어다 넣을 그림");
        if (Directory.Exists(work)) Directory.Delete(work, true);   // 앱이 만든 임시 폴더만 지운다
        if (Directory.Exists(drop)) Directory.Delete(drop, true);
        bool dropMode = fault.Mode == "editor-drop";
        CopyDir(Path.Combine(godotRoot, "project-original"), work, dropMode ? drop : null);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = fault.Mode == "headless",
        };
        string[] args = fault.Mode == "headless"
            ? new[] { "--headless", "--disable-crash-handler", "--path", work, "--import" }
            : new[] { "--editor", "--disable-crash-handler", "--path", work };
        for (int i = 0; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        progress(fault.Label + " — 프로젝트를 여는 중…");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Godot 를 시작하지 못했어요.");
        if (dropMode)
        {
            // 편집기가 뜰 시간을 준 뒤 그림 폴더를 연다 (사용자가 여기서 Godot 로 끌어다 놓는다)
            await Task.Delay(4000, ct).ConfigureAwait(false);
            string[] svgs = Directory.GetFiles(drop, "*.svg");
            var ex = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            ex.ArgumentList.Add(svgs.Length > 0 ? "/select," + svgs[0] : drop);
            try { Process.Start(ex)?.Dispose(); } catch (Exception) { }
            progress("그림(.svg)을 Godot 왼쪽 아래 '파일 시스템' 칸으로 끌어다 놓으세요. 끝나면 편집기를 닫아 주세요.");
        }
        else if (fault.Mode == "editor" && surviveSec <= 0)
            progress("Godot 편집기가 프로젝트를 여는 중이에요. 다 보면 편집기를 닫아 주세요.");

        if (surviveSec > 0)
        {
            Task exited = p.WaitForExitAsync(ct);
            if (await Task.WhenAny(exited, Task.Delay(surviveSec * 1000, ct)).ConfigureAwait(false) == exited)
                return p.ExitCode;   // 그 사이 꺼짐 (0 이 아니면 버그가 그대로)
            // 꺼지지 않고 버팀 = 그림을 문제없이 가져옴 → 편집기를 닫는다 (임시 프로젝트라 저장할 것이 없다)
            progress("Godot 가 " + surviveSec + "초 동안 꺼지지 않았어요 — 편집기를 닫아요");
            try { p.CloseMainWindow(); } catch (Exception) { }
            if (await Task.WhenAny(exited, Task.Delay(5000, ct)).ConfigureAwait(false) != exited)
                try { p.Kill(); } catch (Exception) { }
            return 0;
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    /// <summary>svgTo 가 있으면 .svg 는 프로젝트가 아니라 그 폴더로 복사한다 (그림 직접 넣기용)</summary>
    private static void CopyDir(string src, string dst, string? svgTo = null)
    {
        Directory.CreateDirectory(dst);
        if (svgTo != null) Directory.CreateDirectory(svgTo);
        string[] files = Directory.GetFiles(src);
        for (int i = 0; i < files.Length; i++)
        {
            bool svg = files[i].EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            string to = svg && svgTo != null ? svgTo : dst;
            File.Copy(files[i], Path.Combine(to, Path.GetFileName(files[i])), true);
        }
        string[] dirs = Directory.GetDirectories(src);
        for (int i = 0; i < dirs.Length; i++)
        {
            if (Path.GetFileName(dirs[i]) == ".godot") continue;   // 가져오기 캐시는 빼야 매번 다시 가져온다
            CopyDir(dirs[i], Path.Combine(dst, Path.GetFileName(dirs[i])), svgTo);
        }
    }

    public static string? ExePath(string repoRoot)
    {
        string p = Path.Combine(repoRoot, "collector", "experiments", "FaultLab", "bin", "Release", "net8.0-windows", "FaultLab.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// FaultLab 을 띄우고, 필요하면 표식 줄을 기다렸다가 mark(증상 대상) 를 부른다. 진행 상황은 progress 로 알린다.
    /// 끝나면(프로세스 종료) 종료 코드를 돌려준다. 작업 스레드에서 부를 것.
    /// </summary>
    public static async Task<int> RunAsync(string exe, DemoFault fault, Action<string> mark, Action<string> progress,
                                           CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,   // 콘솔 창만 숨긴다. '응답 없음' 모드의 창은 그대로 뜬다
            StandardOutputEncoding = Encoding.UTF8,
        };
        for (int i = 0; i < fault.Args.Length; i++) psi.ArgumentList.Add(fault.Args[i]);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("시험 프로그램을 시작하지 못했어요.");
        bool marked = fault.MarkSymptom.Length == 0;
        var markTimer = new TaskCompletionSource();

        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || marked || fault.MarkAfterLine.Length == 0) return;
            if (e.Data.StartsWith(fault.MarkAfterLine, StringComparison.Ordinal)) markTimer.TrySetResult();
        };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        progress("시험 프로그램 실행 중: " + fault.Label);

        if (!marked)
        {
            // 표식 줄이 안 나와도(느린 PC 등) 90초 뒤에는 기록한다
            await Task.WhenAny(markTimer.Task, Task.Delay(90_000, ct)).ConfigureAwait(false);
            for (int s = fault.MarkDelaySec; s > 0; s--)
            {
                progress($"{fault.Label} 중… {s}초 뒤 수집기에 기록을 남겨요");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            mark($"{fault.MarkSymptom} {ProcessName}");
            marked = true;
            progress("기록 중… 문제 전후를 모아 저장하는 데 30초쯤 걸려요");
        }

        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }
}
