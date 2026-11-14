// BlackBox 수집기 콘솔판 — 수집·버퍼·덤프만 한다. 분석 없음, 자동 조치 없음, 마킹 전 디스크 쓰기 없음.
// v0.2: 자동 감지 마킹 (Application 1000/1001, System 41/4101/WHEA 수신 → 즉시 자동 덤프)
// v0.7: 조립은 Hosting/CollectorHost 로 옮김 — 참여자판(participant/BlackBox.Participant)과 같은 조립을 쓴다.
using BlackBox.Bench;
using BlackBox.Core;
using BlackBox.Hosting;
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

if (Array.IndexOf(args, "--resource-recent") is var ri && ri >= 0)
{
    // 이미 만든 덤프의 events.json 에서 엔진용 최근 자원 요약(resource_recent)을 다시 계산해 출력한다 (읽기 전용, 수집 없음).
    // 요약 규칙을 바꿨을 때 예전 덤프로 확인하는 용도.
    if (ri + 1 >= args.Length || !File.Exists(args[ri + 1])) { Console.Error.WriteLine("usage: BlackBox --resource-recent <events.json>"); Environment.ExitCode = 2; return; }
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[ri + 1]));
    var arr = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("events");
    var replay = new List<SignalEvent>();
    foreach (var e in arr.EnumerateArray())
        replay.Add(new SignalEvent(e.GetProperty("t").GetDateTimeOffset(), e.GetProperty("source").GetString() ?? "", e.GetProperty("payload").GetRawText()));
    Console.WriteLine(Json.Serialize(RecentResources.Summarize(replay), indented: true));
    return;
}

var host = new CollectorHost(new CollectorHostOptions
{
    DumpRoot = Path.Combine(Environment.CurrentDirectory, "dumps"),
    Edition = "console",
});
host.Start();
var markService = host.Marks;

using var hotkey = new HotkeyListener();
var hotkeyStatus = hotkey.Start(() => markService.MarkManual("hotkey"));
host.Statuses.Add(hotkeyStatus);
Console.WriteLine($"[source] {Json.Serialize(hotkeyStatus)}");

var pre = host.Options.Pre;
var post = host.Options.Post;
Console.WriteLine($"[ready] 버퍼 {pre.TotalMinutes:0}분 유지, 마킹 윈도 -{pre.TotalMinutes:0}분~+{post.TotalSeconds:0}초, 모듈 스냅샷 전략={host.Strategy}");
Console.WriteLine("[ready] 수동 마킹: Ctrl+Shift+F12 또는 m+Enter (증상·대상 지정: m slow blender) | 버퍼 통계: s+Enter | 종료: q+Enter");

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break; // stdin EOF
    // 표준 입력을 UTF-8 로 넘기는 호출자는 첫 줄 앞에 BOM 을 붙이기도 한다. 이쪽도 UTF-8 로 읽었다면 U+FEFF 로 남으므로 떼어 낸다.
    // (이쪽 입력 코드 페이지가 다르면 깨진 글자로 읽혀 여기서 못 고친다 — 호출자가 ASCII 로 보내야 한다. 11/12 lab/vm/guest-case.ps1)
    var cmd = line.Trim().TrimStart('﻿').Trim();
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
            var (count, bytes) = host.Buffer.Stats;
            var bySource = host.Buffer.StatsBySource()
                .ToDictionary(kv => kv.Key, kv => new { count = kv.Value.Count, bytes = kv.Value.Bytes });
            var etw = host.Etw.Counters;
            Console.WriteLine($"[stats] {Json.Serialize(new
            {
                at = DateTimeOffset.Now,
                bufferEvents = count,
                approxBytes = bytes,
                approxKB = Math.Round(bytes / 1024.0, 1),
                bySource,
                etwCounters = new { imageLoadTotal = etw.ImageLoadTotal, imageLoadKept = etw.ImageLoadKept, processEvents = etw.ProcessEvents },
                rollup = new { minutes = host.Rollup.Count, approxKB = Math.Round(host.Rollup.ApproxBytes / 1024.0, 1) },
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
host.Stop(TimeSpan.FromSeconds(60));
var finalStats = host.Buffer.Stats;
Console.WriteLine($"[exit] {Json.Serialize(new { bufferEvents = finalStats.Count, approxBytes = finalStats.ApproxBytes })}");
