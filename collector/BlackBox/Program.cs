// BlackBox 수집기 v0.2 — 수집·버퍼·덤프만 한다. 분석 없음, 자동 조치 없음, 마킹 전 디스크 쓰기 없음.
// v0.2: 자동 감지 마킹 (Application 1000/1001, System 41/4101/WHEA 수신 → 즉시 자동 덤프)
using BlackBox.Bench;
using BlackBox.Core;
using BlackBox.Marking;
using BlackBox.Sources;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

if (args.Contains("--bench-snapshot"))
{
    SnapshotBench.Run();
    return;
}
if (args.Contains("--bench-sensors"))
{
    SnapshotBench.RunSensors();
    return;
}
if (Array.IndexOf(args, "--verify-eventlog") is var vi && vi >= 0)
{
    // 과거 이벤트 로그를 실시간 구독과 같은 파서·분류기로 다시 읽는다 (읽기 전용, 덤프 없음). 인자: 일수(생략·0 = 로그 전체)
    int days = vi + 1 < args.Length && int.TryParse(args[vi + 1], out var d) ? d : 0;
    Console.WriteLine(Json.Serialize(EventLogSource.VerifyHistory(days), indented: true));
    return;
}

var pre = TimeSpan.FromMinutes(5);    // 마킹 전 구간
var post = TimeSpan.FromSeconds(30);  // 마킹 후 구간

// 버퍼 보존 기간: T+post 시점 덤프에서 T-pre 이벤트가 밀려나지 않도록 pre+post+여유
var buffer = new RingBuffer(pre + post + TimeSpan.FromSeconds(30));

// 모듈 스냅샷 전략: 2026-10-08 벤치 결과 ForegroundPlusRecent 확정 — 344ms/60s (1코어 0.69%)
// vs FullSweepEvery60s 3859ms/60s (1코어 7.05%). NOTES.md "자동 감지 마킹" 절 참고.
var snapshotStrategy = SnapshotStrategy.ForegroundPlusRecent;

var eventLogSource = new EventLogSource();
var sensorSource = new SensorSource();
var processSource = new ProcessListSource(snapshotStrategy);
var resourceSource = new ResourceSource(); // v0.6: 커밋/가용 메모리·GPU 어댑터 메모리·프로세스 CPU 상위 (느려짐 메커니즘 A·B·E)
var etwSource = new EtwKernelSource(); // 관리자: Kernel Process+ImageLoad 구독 / 비관리자: 스텁(unavailable)
var sources = new ISignalSource[]
{
    eventLogSource,
    sensorSource,
    processSource,
    resourceSource,
    etwSource,
};

var statuses = new List<SourceStatus>();
foreach (var source in sources)
{
    source.OnSignal += buffer.Append;
    var status = source.Start();
    statuses.Add(status);
    Console.WriteLine($"[source] {Json.Serialize(status)}");
}

// v0.6: 24시간 분 단위 롤업 (메모리에만) — 덤프 시 60분 기울기 요약(resource_trends)으로 나간다
using var rollup = new MinuteRollup(TimeSpan.FromHours(24));
rollup.Start(() => resourceSource.Latest, () => sensorSource.LatestGpu, () => processSource.LatestSummary, TimeSpan.FromMinutes(1));

var config = BlackBoxConfig.Load();
Console.WriteLine($"[config] {Json.Serialize(new { testArtifacts = config.TestArtifacts, denylist = config.Denylist })}");
var snapshotter = new StateSnapshotter(sensorSource);
var writer = new DumpWriter(Path.Combine(Environment.CurrentDirectory, "dumps"));
var markService = new MarkService(buffer, snapshotter, writer, pre, post, statuses, config, rollup);

// 자동 감지: 구독 중인 이벤트(= 전부 크래시/전원/디스플레이/WHEA 계열)는 수신 즉시 자동 마킹
eventLogSource.OnTrigger += markService.OnAutoTrigger;

using var hotkey = new HotkeyListener();
var hotkeyStatus = hotkey.Start(() => markService.MarkManual("hotkey"));
statuses.Add(hotkeyStatus);
Console.WriteLine($"[source] {Json.Serialize(hotkeyStatus)}");

// v0.6.1: 부팅 직후 기록(41·BugCheck·WHEA)은 수집기가 켜지기 전에 써져 실시간으로 못 받는다 → 이번 부팅 이후 기록을 한 번 재생.
// 이미 덤프한 레코드는 기존 dumps/*/meta.json 의 RecordId 로 거른다(읽기만).
var dumpedRecordIds = new HashSet<long>();
try
{
    var dumpRoot = Path.Combine(Environment.CurrentDirectory, "dumps");
    if (Directory.Exists(dumpRoot))
        foreach (var meta in Directory.EnumerateFiles(dumpRoot, "meta.json", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(meta), "\"RecordId\":\\s*(\\d+)"))
                dumpedRecordIds.Add(long.Parse(m.Groups[1].Value));
}
catch { /* 기존 덤프를 못 읽으면 중복 덤프가 생길 수 있을 뿐 — 수집은 계속 */ }
int replayed = eventLogSource.ReplaySinceBoot(dumpedRecordIds.Contains);
Console.WriteLine($"[boot] 이번 부팅 이후 기록 재생 {replayed}건 (41·BugCheck·WHEA, 이미 덤프한 {dumpedRecordIds.Count}건 제외)");

Console.WriteLine($"[ready] 버퍼 {pre.TotalMinutes:0}분 유지, 마킹 윈도 -{pre.TotalMinutes:0}분~+{post.TotalSeconds:0}초, 모듈 스냅샷 전략={snapshotStrategy}");
Console.WriteLine("[ready] 수동 마킹: Ctrl+Shift+F12 또는 m+Enter (증상·대상 지정: m slow blender) | 버퍼 통계: s+Enter | 종료: q+Enter");

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break; // stdin EOF
    var cmd = line.Trim();
    if (cmd.StartsWith("m ", StringComparison.OrdinalIgnoreCase))
    {
        // m [증상] [대상]  예: m slow blender / m freeze / m Unity.exe
        var manual = ManualMark.Parse(cmd[2..]);
        markService.MarkManual("console", manual);
        continue;
    }
    switch (cmd.ToLowerInvariant())
    {
        case "m":
            markService.MarkManual("console");
            break;
        case "s":
            var (count, bytes) = buffer.Stats;
            var bySource = buffer.StatsBySource()
                .ToDictionary(kv => kv.Key, kv => new { count = kv.Value.Count, bytes = kv.Value.Bytes });
            var etw = etwSource.Counters;
            Console.WriteLine($"[stats] {Json.Serialize(new
            {
                at = DateTimeOffset.Now,
                bufferEvents = count,
                approxBytes = bytes,
                approxKB = Math.Round(bytes / 1024.0, 1),
                bySource,
                etwCounters = new { imageLoadTotal = etw.ImageLoadTotal, imageLoadKept = etw.ImageLoadKept, processEvents = etw.ProcessEvents },
                rollup = new { minutes = rollup.Count, approxKB = Math.Round(rollup.ApproxBytes / 1024.0, 1) },
                gcTotalMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            })}");
            break;
        case "q":
            goto shutdown;
        case "":
            break;
        default:
            Console.WriteLine("[help] m=마킹, m <증상> <대상>=지정 마킹(증상: slow freeze app_crash fps_drop …), s=통계, q=종료");
            break;
    }
}

shutdown:
Console.WriteLine("[shutdown] 진행 중인 덤프 대기...");
markService.Drain(TimeSpan.FromSeconds(60));
foreach (var source in sources)
{
    try { source.Stop(); source.Dispose(); } catch { }
}
var finalStats = buffer.Stats;
Console.WriteLine($"[exit] {Json.Serialize(new { bufferEvents = finalStats.Count, approxBytes = finalStats.ApproxBytes })}");
