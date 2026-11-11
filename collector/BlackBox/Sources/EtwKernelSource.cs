using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using BlackBox.Core;

namespace BlackBox.Sources;

/// <summary>
/// ETW 커널 프로바이더 구독: ImageLoad / ProcessStart / ProcessStop.
/// ImageLoad는 비시스템 경로(System32·SysWOW64·WinSxS·SystemApps 제외)만 버퍼에 넣는다 —
/// 유휴 기준 대부분이 시스템 DLL이라 노이즈·메모리를 크게 줄인다 (전체/적재 카운터로 효과 실측 가능).
/// 비관리자에서는 기존 스텁 동작(unavailable 보고, 이벤트 없음)을 유지한다.
/// </summary>
public sealed class EtwKernelSource : ISignalSource
{
    public const string DefaultSessionName = "CrashCollector-BlackBox-Kernel";
    private readonly string _sessionName;

    private static readonly string[] ExcludedPathParts =
    {
        @"\windows\system32\",
        @"\windows\syswow64\",
        @"\windows\winsxs\",
        @"\windows\systemapps\",
    };

    private TraceEventSession? _session;
    private Task? _pump;
    private long _imageLoadTotal;
    private long _imageLoadKept;
    private long _processEvents;
    private readonly bool _stripArgs;

    /// <param name="stripCommandLineArgs">true 면 processStart 명령줄에서 인자를 지우고 실행 파일만 남긴다(참여자판).</param>
    /// <param name="sessionName">같은 이름의 세션은 새로 만들 때 대체되므로, 판(콘솔·참여자·자체 점검)마다 다른 이름을 쓴다.</param>
    public EtwKernelSource(bool stripCommandLineArgs = false, string? sessionName = null)
    {
        _stripArgs = stripCommandLineArgs;
        _sessionName = sessionName ?? DefaultSessionName;
    }

    public string Name => "etw-kernel";

    public event Action<SignalEvent>? OnSignal;

    /// <summary>필터 효과 실측용 카운터 (전체 ImageLoad / 버퍼 적재분 / 프로세스 이벤트).</summary>
    public (long ImageLoadTotal, long ImageLoadKept, long ProcessEvents) Counters =>
        (Interlocked.Read(ref _imageLoadTotal), Interlocked.Read(ref _imageLoadKept), Interlocked.Read(ref _processEvents));

    public SourceStatus Start()
    {
        try
        {
            if (!(TraceEventSession.IsElevated() ?? false))
                return SourceStatus.Unavailable(Name, "권한 필요: NT Kernel Logger는 관리자 필수 — 비관리자에선 스텁 동작 유지");

            // 같은 이름의 세션이 남아 있으면(비정상 종료 등) 새로 만들며 대체된다
            _session = new TraceEventSession(_sessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);

            var kernel = _session.Source.Kernel;
            kernel.ImageLoad += OnImageLoad;
            kernel.ProcessStart += OnProcessStart;
            kernel.ProcessStop += OnProcessStop;

            _pump = Task.Factory.StartNew(() => _session.Source.Process(), TaskCreationOptions.LongRunning);
            return SourceStatus.Ok(Name, "Kernel Process+ImageLoad 구독 중, ImageLoad는 비시스템 경로만 적재");
        }
        catch (Exception ex)
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            return SourceStatus.Unavailable(Name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnImageLoad(ImageLoadTraceData data)
    {
        Interlocked.Increment(ref _imageLoadTotal);
        string path = data.FileName ?? "";
        string lower = path.Replace('/', '\\').ToLowerInvariant();
        if (ExcludedPathParts.Any(lower.Contains)) return;

        Interlocked.Increment(ref _imageLoadKept);
        OnSignal?.Invoke(SignalEvent.Create(Name, new
        {
            ev = "imageLoad",
            pid = data.ProcessID,
            proc = SafeProcessName(data.ProcessName),
            path,
        }));
    }

    private void OnProcessStart(ProcessTraceData data)
    {
        Interlocked.Increment(ref _processEvents);
        string cmd = data.CommandLine ?? "";
        if (_stripArgs) cmd = CommandLineText.ExecutableOnly(cmd);
        if (cmd.Length > 200) cmd = cmd[..200] + "...";
        OnSignal?.Invoke(SignalEvent.Create(Name, new
        {
            ev = "processStart",
            pid = data.ProcessID,
            ppid = data.ParentID,
            name = data.ImageFileName,
            cmd,
        }));
    }

    private void OnProcessStop(ProcessTraceData data)
    {
        Interlocked.Increment(ref _processEvents);
        OnSignal?.Invoke(SignalEvent.Create(Name, new
        {
            ev = "processStop",
            pid = data.ProcessID,
            name = data.ImageFileName,
            exit = data.ExitStatus,
        }));
    }

    private static string SafeProcessName(string? name) => string.IsNullOrEmpty(name) ? "?" : name;

    public void Stop()
    {
        try { _session?.Stop(); } catch { }
        try { _pump?.Wait(3000); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
