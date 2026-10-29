using System.Text.RegularExpressions;
using BlackBox.Core;
using BlackBox.Marking;
using BlackBox.Sources;

namespace BlackBox.Hosting;

/// <summary>수집기 조립 옵션. 콘솔판(Program.cs)과 참여자판(participant/BlackBox.Participant)이 같은 조립을 쓴다.</summary>
public sealed record CollectorHostOptions
{
    /// <summary>덤프 폴더. 마킹 전에는 아무것도 쓰지 않는다(CLAUDE.md 2).</summary>
    public string DumpRoot { get; init; } = Path.Combine(Environment.CurrentDirectory, "dumps");

    /// <summary>마킹 전 구간.</summary>
    public TimeSpan Pre { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>마킹 후 구간.</summary>
    public TimeSpan Post { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>ETW 프로세스 시작 명령줄에서 인자를 지우고 실행 파일만 남긴다(참여자판: 인자에 토큰·주소가 들어갈 수 있다).</summary>
    public bool StripCommandLineArgs { get; init; }

    /// <summary>meta.json 의 edition 값 ("console" / "participant").</summary>
    public string Edition { get; init; } = "console";

    /// <summary>시작 시 이번 부팅 이후 41·BugCheck·WHEA 기록을 재생할지 (v0.6.1).</summary>
    public bool ReplaySinceBoot { get; init; } = true;
}

/// <summary>
/// 소스·링버퍼·롤업·마킹 서비스를 묶는다. 수집·버퍼·덤프만 — 분석·조치·업로드는 여기 없다(CLAUDE.md 1·3).
/// 단축키는 호출자 몫이다(콘솔판은 즉시 마킹, 참여자판은 증상 대화상자를 띄운다).
/// </summary>
public sealed class CollectorHost : IDisposable
{
    public const string Version = "0.7";

    public CollectorHostOptions Options { get; }
    public RingBuffer Buffer { get; }
    public EventLogSource EventLog { get; }
    public SensorSource Sensors { get; }
    public ProcessListSource Processes { get; }
    public ResourceSource Resources { get; }
    public EtwKernelSource Etw { get; }
    public MinuteRollup Rollup { get; }
    public BlackBoxConfig Config { get; }
    public MarkService Marks { get; }
    public SnapshotStrategy Strategy { get; } = SnapshotStrategy.ForegroundPlusRecent;
    public DateTimeOffset StartedAt { get; private set; }
    public int ReplayedSinceBoot { get; private set; }

    /// <summary>소스별 시작 결과. 호출자가 단축키 상태 등을 덧붙일 수 있다.</summary>
    public List<SourceStatus> Statuses { get; } = new();

    private readonly ISignalSource[] _sources;
    private bool _started;

    public CollectorHost(CollectorHostOptions? options = null)
    {
        Options = options ?? new CollectorHostOptions();
        // 버퍼 보존 기간: T+post 시점 덤프에서 T-pre 이벤트가 밀려나지 않도록 pre+post+여유
        Buffer = new RingBuffer(Options.Pre + Options.Post + TimeSpan.FromSeconds(30));
        EventLog = new EventLogSource();
        Sensors = new SensorSource();
        // 모듈 스냅샷 전략: 2026-10-08 벤치 결과 ForegroundPlusRecent 확정 (NOTES.md "자동 감지 마킹" 절)
        Processes = new ProcessListSource(Strategy);
        Resources = new ResourceSource();
        Etw = new EtwKernelSource(Options.StripCommandLineArgs);
        _sources = new ISignalSource[] { EventLog, Sensors, Processes, Resources, Etw };
        Rollup = new MinuteRollup(TimeSpan.FromHours(24));
        Config = BlackBoxConfig.Load();
        var snapshotter = new StateSnapshotter(Sensors);
        var writer = new DumpWriter(Options.DumpRoot, Options.Edition, Version);
        Marks = new MarkService(Buffer, snapshotter, writer, Options.Pre, Options.Post, Statuses, Config, Rollup);
    }

    public IReadOnlyList<ISignalSource> Sources => _sources;

    /// <summary>소스 시작 → 롤업 → 자동 감지 연결 → 부팅 재생. 소스 하나가 실패해도 나머지는 계속한다(CLAUDE.md 4).</summary>
    public void Start(Action<string>? log = null)
    {
        if (_started) return;
        _started = true;
        log ??= Console.WriteLine;
        StartedAt = DateTimeOffset.Now;

        foreach (var source in _sources)
        {
            source.OnSignal += Buffer.Append;
            SourceStatus status;
            try { status = source.Start(); }
            catch (Exception ex) { status = SourceStatus.Unavailable(source.Name, $"{ex.GetType().Name}: {ex.Message}"); }
            Statuses.Add(status);
            log($"[source] {Json.Serialize(status)}");
        }

        // v0.6: 24시간 분 단위 롤업 (메모리에만) — 덤프 시 60분 기울기 요약(resource_trends)으로 나간다
        Rollup.Start(() => Resources.Latest, () => Sensors.LatestGpu, () => Processes.LatestSummary, TimeSpan.FromMinutes(1));
        log($"[config] {Json.Serialize(new { testArtifacts = Config.TestArtifacts, denylist = Config.Denylist })}");

        // 자동 감지: 구독 중인 이벤트(= 전부 크래시/전원/디스플레이/WHEA 계열)는 수신 즉시 자동 마킹
        EventLog.OnTrigger += Marks.OnAutoTrigger;

        if (Options.ReplaySinceBoot)
        {
            var dumped = DumpedRecordIds(Options.DumpRoot);
            ReplayedSinceBoot = EventLog.ReplaySinceBoot(dumped.Contains);
            log($"[boot] 이번 부팅 이후 기록 재생 {ReplayedSinceBoot}건 (41·BugCheck·WHEA, 이미 덤프한 {dumped.Count}건 제외)");
        }
    }

    /// <summary>
    /// v0.6.1: 부팅 직후 기록(41·BugCheck·WHEA)은 수집기가 켜지기 전에 써져 실시간으로 못 받는다 → 이번 부팅 이후 기록을 한 번 재생.
    /// 이미 덤프한 레코드는 기존 dumps/*/meta.json 의 RecordId 로 거른다(읽기만).
    /// </summary>
    public static HashSet<long> DumpedRecordIds(string dumpRoot)
    {
        var ids = new HashSet<long>();
        try
        {
            if (Directory.Exists(dumpRoot))
                foreach (var meta in Directory.EnumerateFiles(dumpRoot, "meta.json", SearchOption.AllDirectories))
                    foreach (Match m in Regex.Matches(File.ReadAllText(meta), "\"RecordId\":\\s*(\\d+)"))
                        ids.Add(long.Parse(m.Groups[1].Value));
        }
        catch { /* 기존 덤프를 못 읽으면 중복 덤프가 생길 수 있을 뿐 — 수집은 계속 */ }
        return ids;
    }

    /// <summary>진행 중인 덤프를 기다린 뒤 소스를 멈춘다.</summary>
    public void Stop(TimeSpan drain)
    {
        if (!_started) return;
        _started = false;
        try { EventLog.OnTrigger -= Marks.OnAutoTrigger; } catch { }
        Marks.Drain(drain);
        foreach (var source in _sources)
        {
            try { source.Stop(); source.Dispose(); } catch { }
        }
        try { Rollup.Dispose(); } catch { }
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(10));
}
