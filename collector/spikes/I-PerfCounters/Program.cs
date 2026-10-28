// 스파이크 I: .NET PerformanceCounter가 한국어 Windows에서 영어 카운터 이름을 받는지,
// PerfOS Memory / GPU Adapter Memory / Process 카운터가 값을 내는지 5초 확인. 읽기 전용.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var result = new Dictionary<string, object?> { ["uiCulture"] = CultureInfo.CurrentUICulture.Name };
var sw = Stopwatch.StartNew();

object Try(Func<object> f) { try { return f(); } catch (Exception ex) { return new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" }; } }

result["categoryExists_Memory"] = Try(() => PerformanceCounterCategory.Exists("Memory"));
result["categoryExists_GPUAdapterMemory"] = Try(() => PerformanceCounterCategory.Exists("GPU Adapter Memory"));
result["categoryExists_Process"] = Try(() => PerformanceCounterCategory.Exists("Process"));

var mem = new (string Name, PerformanceCounter? C)[]
{
    ("Committed Bytes", null), ("Commit Limit", null), ("% Committed Bytes In Use", null), ("Available MBytes", null), ("Pages/sec", null),
};
var memVals = new Dictionary<string, object?>();
foreach (var (name, _) in mem)
{
    memVals[name] = Try(() =>
    {
        using var c = new PerformanceCounter("Memory", name, readOnly: true);
        c.NextValue(); Thread.Sleep(200); // 비율형은 2회 샘플 필요
        return Math.Round(c.NextValue(), 1);
    });
}
result["memory"] = memVals;

result["gpuAdapterMemory"] = Try(() =>
{
    var cat = new PerformanceCounterCategory("GPU Adapter Memory");
    var inst = cat.GetInstanceNames();
    var list = new List<object>();
    foreach (var i in inst)
    {
        using var c = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", i, readOnly: true);
        using var s = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", i, readOnly: true);
        list.Add(new { instance = i, dedicatedMB = Math.Round(c.NextValue() / 1048576.0), sharedMB = Math.Round(s.NextValue() / 1048576.0) });
    }
    return list;
});

result["processCounters_sample"] = Try(() =>
{
    using var c = new PerformanceCounter("Process", "Private Bytes", "explorer", readOnly: true);
    using var h = new PerformanceCounter("Process", "Handle Count", "explorer", readOnly: true);
    return new { explorerPrivateMB = Math.Round(c.NextValue() / 1048576.0), handles = h.NextValue() };
});

// Process 클래스 직접 접근 비용 (ProcessListSource 확장용): PrivateMemorySize64 + HandleCount + TotalProcessorTime 전수
result["processClassSweep"] = Try(() =>
{
    var t = Stopwatch.StartNew(); int n = 0, denied = 0; long priv = 0;
    foreach (var p in Process.GetProcesses())
    {
        try { priv += p.PrivateMemorySize64; _ = p.HandleCount; _ = p.TotalProcessorTime; n++; } catch { denied++; } finally { p.Dispose(); }
    }
    return new { processes = n, denied, totalPrivateGB = Math.Round(priv / 1073741824.0, 1), ms = t.ElapsedMilliseconds };
});


// Process 카테고리 ReadCategory 1회로 전 프로세스의 CPU%/Private Bytes/Handle Count/ID Process를 한 번에 — 접근 거부 없음. 2회 샘플로 CPU% 계산.
result["processCategory"] = Try(() =>
{
    var cat = new PerformanceCounterCategory("Process");
    var t = Stopwatch.StartNew();
    var a = cat.ReadCategory(); long ms1 = t.ElapsedMilliseconds;
    Thread.Sleep(1000);
    t.Restart();
    var b = cat.ReadCategory(); long ms2 = t.ElapsedMilliseconds;
    var cpuA = a["% Processor Time"]; var cpuB = b["% Processor Time"];
    var pidB = b["ID Process"]; var privB = b["Private Bytes"]; var hB = b["Handle Count"];
    var rows = new List<(string Name, int Pid, double Cpu, double PrivMB, long Handles)>();
    foreach (System.Collections.DictionaryEntry e in cpuB)
    {
        var inst = (string)e.Key; if (inst == "_Total" || inst == "Idle") continue;
        if (!cpuA.Contains(inst)) continue;
        double cpu = CounterSample.Calculate(cpuA[inst].Sample, cpuB[inst].Sample) / Environment.ProcessorCount;
        rows.Add((inst, (int)pidB[inst].RawValue, cpu, privB[inst].RawValue / 1048576.0, hB[inst].RawValue));
    }
    return new
    {
        instances = rows.Count, readMs1 = ms1, readMs2 = ms2, logicalCpus = Environment.ProcessorCount,
        topCpu = rows.OrderByDescending(r => r.Cpu).Take(5).Select(r => new { r.Name, r.Pid, cpu = Math.Round(r.Cpu, 1), privMB = Math.Round(r.PrivMB), r.Handles }).ToList(),
    };
});
result["elapsedMs"] = sw.ElapsedMilliseconds;
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
