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

var pre = TimeSpan.FromMinutes(5);    // 마킹 전 구간
var post = TimeSpan.FromSeconds(30);  // 마킹 후 구간

// 버퍼 보존 기간: T+post 시점 덤프에서 T-pre 이벤트가 밀려나지 않도록 pre+post+여유
var buffer = new RingBuffer(pre + post + TimeSpan.FromSeconds(30));

// 모듈 스냅샷 전략: 2026-10-08 벤치 결과 ForegroundPlusRecent 확정 — 344ms/60s (1코어 0.69%)
// vs FullSweepEvery60s 3859ms/60s (1코어 7.05%). NOTES.md "자동 감지 마킹" 절 참고.
var snapshotStrategy = SnapshotStrategy.ForegroundPlusRecent;

var eventLogSource = new EventLogSource();
var sensorSource = new SensorSource();
var etwSource = new EtwKernelSource(); // 관리자: Kernel Process+ImageLoad 구독 / 비관리자: 스텁(unavailable)
var sources = new ISignalSource[]
{
    eventLogSource,
    sensorSource,
    new ProcessListSource(snapshotStrategy),
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

var config = BlackBoxConfig.Load();
Console.WriteLine($"[config] {Json.Serialize(new { testArtifacts = config.TestArtifacts, denylist = config.Denylist })}");
var snapshotter = new StateSnapshotter(sensorSource);
var writer = new DumpWriter(Path.Combine(Environment.CurrentDirectory, "dumps"));
var markService = new MarkService(buffer, snapshotter, writer, pre, post, statuses, config);

// 자동 감지: 구독 중인 이벤트(= 전부 크래시/전원/디스플레이/WHEA 계열)는 수신 즉시 자동 마킹
eventLogSource.OnTrigger += markService.OnAutoTrigger;

using var hotkey = new HotkeyListener();
var hotkeyStatus = hotkey.Start(() => markService.MarkManual("hotkey"));
statuses.Add(hotkeyStatus);
Console.WriteLine($"[source] {Json.Serialize(hotkeyStatus)}");

Console.WriteLine($"[ready] 버퍼 {pre.TotalMinutes:0}분 유지, 마킹 윈도 -{pre.TotalMinutes:0}분~+{post.TotalSeconds:0}초, 모듈 스냅샷 전략={snapshotStrategy}");
Console.WriteLine("[ready] 수동 마킹: Ctrl+Shift+F12 또는 m+Enter | 버퍼 통계: s+Enter | 종료: q+Enter");

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break; // stdin EOF
    switch (line.Trim().ToLowerInvariant())
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
                gcTotalMB = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
            })}");
            break;
        case "q":
            goto shutdown;
        case "":
            break;
        default:
            Console.WriteLine("[help] m=마킹, s=통계, q=종료");
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
