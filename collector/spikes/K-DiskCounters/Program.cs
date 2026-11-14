// 스파이크 K: 디스크 수준 신호(PhysicalDisk 성능 카운터)가 값을 내는지 5초 확인. 읽기 전용.
// 배경(11/12 첫 사례 세션): 느리게 만든 가상 머신(500 IOPS 제한)에서 CPU 2~8%·메모리 여유인데도 느렸다 —
// 수집기는 "디스크가 얼마나 바쁜가"를 보지 않아 원인을 말할 수 없었다.
// 확인할 것: (1) 한국어 Windows에서 영어 범주·카운터 이름이 동작하는가 (2) 인스턴스 이름 형식
//            (3) 비율형 카운터가 1초 간격 두 번째 샘플부터 값을 내는가 (4) 읽는 데 드는 시간
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var result = new Dictionary<string, object?> { ["uiCulture"] = CultureInfo.CurrentUICulture.Name };

object Try(Func<object> f) { try { return f(); } catch (Exception ex) { return new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }; } }

result["categoryExists_PhysicalDisk"] = Try(() => PerformanceCounterCategory.Exists("PhysicalDisk"));
result["categoryExists_LogicalDisk"] = Try(() => PerformanceCounterCategory.Exists("LogicalDisk"));

string[] names = { "% Idle Time", "Avg. Disk Queue Length", "Current Disk Queue Length", "Avg. Disk sec/Transfer", "Disk Transfers/sec", "Disk Bytes/sec" };
result["physicalDisk"] = Try(() =>
{
    var cat = new PerformanceCounterCategory("PhysicalDisk");
    var instances = cat.GetInstanceNames();
    var open = Stopwatch.StartNew();
    var counters = instances.ToDictionary(i => i, i => names.Select(n => new PerformanceCounter("PhysicalDisk", n, i, readOnly: true)).ToArray());
    foreach (var cs in counters.Values) foreach (var c in cs) c.NextValue(); // 비율형 첫 호출은 0
    long openMs = open.ElapsedMilliseconds;

    var rows = new List<object>();
    long readMsTotal = 0;
    for (int k = 0; k < 5; k++)
    {
        Thread.Sleep(1000);
        var sw = Stopwatch.StartNew();
        var sample = new Dictionary<string, object>();
        foreach (var (inst, cs) in counters)
        {
            var v = cs.Select(c => c.NextValue()).ToArray();
            sample[inst] = new
            {
                idlePct = Math.Round(v[0], 1),
                avgQueue = Math.Round(v[1], 3),
                curQueue = Math.Round(v[2], 0),
                secPerTransferMs = Math.Round(v[3] * 1000, 2),
                transfersPerSec = Math.Round(v[4], 1),
                mbPerSec = Math.Round(v[5] / 1048576.0, 2),
            };
        }
        readMsTotal += sw.ElapsedMilliseconds;
        rows.Add(new { t = DateTimeOffset.Now.ToString("HH:mm:ss"), sample });
    }
    foreach (var cs in counters.Values) foreach (var c in cs) c.Dispose();
    return new { instances, openMs, readMsPerSample = Math.Round(readMsTotal / 5.0, 1), samples = rows };
});

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
