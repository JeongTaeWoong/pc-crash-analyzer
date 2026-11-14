// FaultLab — 문제 상황을 일부러 일으키는 테스트 프로그램 (멘토링 2차 반영). 골든 셋 생성기.
// 모든 모드는 자기 프로세스 안에서만 문제를 일으키며 시스템 설정을 바꾸지 않는다.
//
//   crash-a | crash-b | crash-c : CrashDummy와 동일 (즉시 예외 / 5초 후 FailFast / 15초 후 예외)
//   plugin  [dll경로]           : 네이티브 플러그인 DLL 안에서 접근 위반 → 오류 모듈이 서드파티 DLL(s6 패턴)
//   hang    [초=20]             : 창을 띄운 뒤 UI 스레드를 멈춤 → 사용자가 창을 클릭·닫으면 1002(응답 없음)
//   cpu     [초=30] [스레드=논리코어수] : CPU 과부하
//   disk    [초=30] [MB=512]    : 임시 파일 쓰기/읽기 반복 후 삭제
//   leak    [MB=2048] [유지초=60] [간격ms=50] : 메모리를 단계적으로 점유하고 유지 (RADAR_PRE_LEAK 유발 가능성)
//
// 표식 줄(ASCII, 실험실 스크립트가 읽는다): CRASH_AT= / HANG_AT= / LOAD_AT=(부하 시작) / LOAD_READY_AT=(leak: 점유 완료)
// / FAULTHOST_EXIT=. plugin 모드는 FaultHost 가 0xC0000005 로 끝나지 않으면 종료 코드 2.
//   idle    [초=600]            : 아무 문제도 없이 떠 있기만 한다(가끔 조금 일함) — "곁에 있던 정상 프로그램" 역할
//
// 다른 이름으로 빌드된 경우(실험실 사례 생성기 lab/vm/aliases.json, 11/12): 인자 없이 실행하면 exe 옆의 task.cfg 를 읽는다.
//   run=leak 3481 100        ← 원래 명령줄 인자
//   title=창 제목            ← 응답 없음 모드의 창 제목
//   host=...exe / plugin=...dll  ← plugin 모드의 네이티브 호스트·DLL 파일 이름
// 이유: 사례를 AI 에 넣어 평가할 때 프로그램 이름("FaultLab")·명령줄("leak")·창 제목이 정답을 알려 주면 안 된다.
// 그래서 이 프로그램이 남기는 흔적(프로세스 이름, 파일 이름, 예외 문구)에는 시험용임을 드러내는 낱말을 쓰지 않는다.
using System.Diagnostics;
using System.Runtime.InteropServices;

string appName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "app");
var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (args.Length == 0)
{
    string cfgPath = Path.Combine(AppContext.BaseDirectory, "task.cfg");
    if (File.Exists(cfgPath))
    {
        foreach (var line in File.ReadAllLines(cfgPath))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) cfg[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (cfg.TryGetValue("run", out var run) && run.Length > 0)
            args = run.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
// 출력이 파일로 넘겨질 때(실험실 게스트의 faultlab.log) 한글이 깨지지 않게 UTF-8로 고정. *_AT= 표식 줄은 ASCII다.
try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }
Console.WriteLine($"FAULTLAB MODE={mode} PID={Environment.ProcessId} START={DateTimeOffset.Now:O}");
Console.Out.Flush();

switch (mode)
{
    case "crash-a": Crash(); break;
    case "crash-b":
        Thread.Sleep(5000);
        Mark("CRASH_AT");
        Environment.FailFast("Unrecoverable state in worker thread");
        break;
    case "crash-c": Thread.Sleep(15000); Crash(); break;

    case "plugin":
    {
        // 네이티브 호스트(FaultHost.exe)가 DLL 안에서 죽는다 → 오류 모듈 = FaultPlugin.dll (s6 패턴)
        string dll = args.Length > 1 ? args[1]
            : cfg.TryGetValue("plugin", out var pl) ? Path.Combine(AppContext.BaseDirectory, pl)
            : FindPlugin();
        string host = Path.Combine(Path.GetDirectoryName(dll) ?? ".", cfg.TryGetValue("host", out var hn) ? hn : "FaultHost.exe");
        if (!File.Exists(host)) { Console.WriteLine($"네이티브 호스트 없음: {host} — plugin/CMakeLists.txt 빌드 필요"); Environment.ExitCode = 2; return; }
        Mark("CRASH_AT");
        var ph = Process.Start(new ProcessStartInfo(host, $"\"{dll}\"") { UseShellExecute = false });
        ph!.WaitForExit();
        Console.WriteLine($"FaultHost exit=0x{ph.ExitCode:x} (0xc0000005면 재현 성공)");
        Console.WriteLine($"FAULTHOST_EXIT=0x{ph.ExitCode:x8}");
        // 재현 실패를 호출자가 알 수 있게 종료 코드로 알린다. 11/12: VCRUNTIME140.dll 이 없는 PC에서 0xc0000135 로
        // 시작도 못 했는데 FaultLab 은 0으로 끝나 "성공"으로 기록됐다.
        if (unchecked((uint)ph.ExitCode) != 0xC0000005) Environment.ExitCode = 2;
        break;
    }

    case "plugin-inproc":
    {
        // 비교용: .NET 프로세스 안에서 직접 호출하면 CLR이 가로채 오류 모듈이 coreclr.dll로 기록된다
        string dll = args.Length > 1 ? args[1] : FindPlugin();
        Console.WriteLine($"plugin={dll}");
        if (!File.Exists(dll)) { Console.WriteLine("FaultPlugin.dll 없음 — plugin/README.md의 빌드 절차 참고"); return; }
        nint lib = NativeLibrary.Load(dll);
        nint fn = NativeLibrary.GetExport(lib, "fault_crash");
        var crash = Marshal.GetDelegateForFunctionPointer<FaultCrash>(fn);
        Mark("CRASH_AT");
        crash(); // DLL 안에서 null 쓰기 → 이벤트 1000 오류 모듈 = FaultPlugin.dll
        break;
    }

    case "hang":
    {
        int sec = Arg(1, 20);
        Console.WriteLine($"창을 띄우고 {sec}초간 UI 스레드를 멈춥니다. 창을 클릭하면 '응답 없음'이 되고, 닫기를 누르면 1002가 기록됩니다.");
        var form = new System.Windows.Forms.Form { Text = cfg.TryGetValue("title", out var t1) ? t1 : appName, Width = 420, Height = 160 };
        form.Shown += (_, _) => { Mark("HANG_AT"); Thread.Sleep(sec * 1000); };
        System.Windows.Forms.Application.Run(form);
        break;
    }

    case "hang-report":
    {
        // 사람 없이 응답 없음 보고를 재현: UI 스레드를 멈춘 뒤, 같은 프로세스의 배경 스레드가 자기 창에 대해 WerReportHang 을 호출한다.
        // Microsoft Learn: "no-response reporting 을 시작하고, 완료 후 그 창을 만든 프로세스를 종료한다". 대상은 이 테스트 프로그램 자신뿐이다.
        int sec = Arg(1, 60), after = Arg(2, 6);
        Console.WriteLine($"창을 띄우고 UI 스레드를 {sec}초 멈춘 뒤, {after}초 후 자기 창에 대해 WerReportHang 호출 (이 프로세스만 종료됨)");
        var form = new System.Windows.Forms.Form { Text = cfg.TryGetValue("title", out var t2) ? t2 : appName, Width = 420, Height = 160 };
        form.Shown += (_, _) =>
        {
            var hwnd = form.Handle;
            new Thread(() =>
            {
                Thread.Sleep(after * 1000);
                Mark("WER_REPORT_HANG_AT");
                int hr = WerReportHang(hwnd, appName);
                Console.WriteLine($"WerReportHang hr=0x{hr:x8}");
            }) { IsBackground = true }.Start();
            Mark("HANG_AT");
            Thread.Sleep(sec * 1000);
        };
        System.Windows.Forms.Application.Run(form);
        break;
    }

    case "cpu":
    {
        int sec = Arg(1, 30), threads = Arg(2, Environment.ProcessorCount);
        Console.WriteLine($"CPU 과부하: {threads}스레드 × {sec}초");
        Mark("LOAD_AT");
        var end = DateTime.UtcNow.AddSeconds(sec);
        var ts = Enumerable.Range(0, threads).Select(_ => new Thread(() => { double x = 1; while (DateTime.UtcNow < end) x = Math.Sqrt(x + 1.0001); })).ToList();
        ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join());
        break;
    }

    case "disk":
    {
        int sec = Arg(1, 30), mb = Arg(2, 512);
        string path = Path.Combine(Path.GetTempPath(), $"{appName.ToLowerInvariant()}_cache_{Environment.ProcessId}.tmp");
        Console.WriteLine($"디스크 과다 사용: {mb}MB 파일 쓰기/읽기 반복 {sec}초 → {path}");
        Mark("LOAD_AT");
        var buf = new byte[1 << 20];
        new Random(1).NextBytes(buf);
        var end = DateTime.UtcNow.AddSeconds(sec);
        try
        {
            while (DateTime.UtcNow < end)
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
                    for (int i = 0; i < mb && DateTime.UtcNow < end; i++) fs.Write(buf, 0, buf.Length);
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                    while (fs.Read(buf, 0, buf.Length) > 0 && DateTime.UtcNow < end) { }
            }
        }
        finally { try { File.Delete(path); } catch { } }
        break;
    }

    case "leak":
    {
        // stepMs: 50MB 를 한 번 확보한 뒤 쉬는 시간. 기본 50ms(몇 초 만에 다 차는 급격한 점유),
        // 크게 주면 몇 분에 걸쳐 서서히 새는 누수가 된다 — 수집기의 분 단위 추세(프로세스당 10분 이상 필요)에 잡히는 쪽.
        int mb = Arg(1, 2048), hold = Arg(2, 60), stepMs = Arg(3, 50);
        Console.WriteLine($"메모리 점유: {mb}MB까지 50MB씩({stepMs}ms 간격) 확보 후 {hold}초 유지");
        Mark("LOAD_AT");
        var blocks = new List<byte[]>();
        for (int got = 0; got < mb; got += 50)
        {
            var b = new byte[50 << 20];
            for (int i = 0; i < b.Length; i += 4096) b[i] = 1; // 실제 커밋되도록 페이지마다 터치
            blocks.Add(b);
            Thread.Sleep(stepMs);
        }
        Console.WriteLine($"점유 완료: {blocks.Count * 50}MB");
        Mark("LOAD_READY_AT"); // 여기부터 유지 구간. 느린 PC에서는 확보에만 몇 분이 걸린다(11/12 office-old: 4분 이상)
        Thread.Sleep(hold * 1000);
        GC.KeepAlive(blocks);
        break;
    }

    case "idle":
    {
        // 정상 프로그램 역할: 떠 있기만 하고 5초마다 아주 잠깐 일한다. 문제의 원인이 아니다.
        int sec = Arg(1, 600);
        Mark("LOAD_AT");
        var end = DateTime.UtcNow.AddSeconds(sec);
        var keep = new byte[24 << 20];
        for (int i = 0; i < keep.Length; i += 4096) keep[i] = 1;
        while (DateTime.UtcNow < end)
        {
            double x = 1;
            var until = DateTime.UtcNow.AddMilliseconds(40);
            while (DateTime.UtcNow < until) x = Math.Sqrt(x + 1.0001);
            Thread.Sleep(5000);
        }
        GC.KeepAlive(keep);
        break;
    }

    default:
        Console.WriteLine("usage: FaultLab idle [sec] | crash-a|crash-b|crash-c|plugin [dll]|hang [sec]|hang-report [sec] [after]|cpu [sec] [threads]|disk [sec] [MB]|leak [MB] [holdSec] [stepMs]");
        break;
}

int Arg(int i, int def) => args.Length > i && int.TryParse(args[i], out var v) ? v : def;

static void Mark(string label)
{
    Console.WriteLine($"{label}={DateTimeOffset.Now:O}");
    Console.Out.Flush();
}

static void Crash()
{
    Mark("CRASH_AT");
    throw new InvalidOperationException("Sequence contains no matching element");
}

static string FindPlugin()
{
    // exe 폴더 → 위로 올라가며 plugin/build/Release/FaultPlugin.dll 탐색
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
    {
        foreach (var cand in new[]
                 {
                     Path.Combine(dir.FullName, "FaultPlugin.dll"),
                     Path.Combine(dir.FullName, "plugin", "build", "Release", "FaultPlugin.dll"),
                 })
            if (File.Exists(cand)) return cand;
    }
    return "FaultPlugin.dll";
}

[DllImport("faultrep.dll", CharSet = CharSet.Unicode)]
static extern int WerReportHang(IntPtr hwndHungApp, string? pwzHungApplicationName);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void FaultCrash();
