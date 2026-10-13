using System.Diagnostics;
using System.IO;
using System.Text;

namespace CrashAnalyzer.UI.Services;

/// <summary>
/// 수집기(BlackBox) 상주 통합: UI 안에서 시작/중지하고, 로그를 받아 보여 주고,
/// 새 덤프(engine-snapshot.json)가 생기면 알린다. 최종형(트레이 상주)의 1단계.
/// </summary>
public sealed class CollectorHost : IDisposable
{
    private readonly string _projectDir;
    private readonly string? _exePath;
    private readonly string _dumpsDir;
    private Process? _proc;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _seenDumps = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? OutputLine;
    public event Action<string>? NewDump;
    public event Action<int>? Exited;

    public bool IsRunning => _proc is { HasExited: false };

    public CollectorHost(string repoRoot)
    {
        _projectDir = RepoPaths.BlackBoxProjectDir(repoRoot);
        _exePath = RepoPaths.BlackBoxExePath(repoRoot);
        _dumpsDir = Path.Combine(_projectDir, "dumps");
    }

    public void Start()
    {
        if (IsRunning) return;
        if (_exePath is null)
            throw new FileNotFoundException(
                "BlackBox.exe가 없습니다. 먼저 빌드하세요: dotnet build collector/BlackBox -c Release");

        var psi = new ProcessStartInfo(_exePath)
        {
            WorkingDirectory = _projectDir, // 덤프가 collector/BlackBox/dumps 에 생기도록
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("수집기 시작 실패");
        _proc.EnableRaisingEvents = true;
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null) OutputLine?.Invoke(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) OutputLine?.Invoke("[err] " + e.Data); };
        _proc.Exited += (_, _) => Exited?.Invoke(_proc?.ExitCode ?? -1);
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        Directory.CreateDirectory(_dumpsDir);
        _watcher = new FileSystemWatcher(_dumpsDir)
        {
            IncludeSubdirectories = true,
            Filter = "engine-snapshot.json",
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => OnDumpFile(e.FullPath);
        _watcher.Changed += (_, e) => OnDumpFile(e.FullPath);
    }

    private void OnDumpFile(string path)
    {
        lock (_seenDumps)
        {
            if (!_seenDumps.Add(path)) return; // 같은 덤프의 Created+Changed 중복 억제
        }
        // 쓰기 완료 대기 후 알림
        Task.Run(async () =>
        {
            await Task.Delay(800);
            if (File.Exists(path)) NewDump?.Invoke(path);
        });
    }

    /// <summary>q 입력으로 정상 종료(진행 중 덤프 드레인 포함)를 시도하고, 안 되면 강제 종료.</summary>
    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        var p = _proc;
        if (p is null || p.HasExited) { _proc = null; return; }
        try
        {
            p.StandardInput.WriteLine("q");
            p.StandardInput.Flush();
            if (!p.WaitForExit(10_000)) p.Kill(true);
        }
        catch { try { p.Kill(true); } catch { /* 이미 종료 */ } }
        _proc = null;
    }

    public void Dispose() => Stop();
}
