// 스파이크 L: 두 번의 Process 범주 읽기 사이에 "새로 켜진" 프로세스의 CPU 를 한 번의 표본으로 계산할 수 있는가. 읽기 전용 + 자식 프로세스 1개.
// 배경(11/12 사례 재생): ResourceSource 는 직전 읽기에도 있던 인스턴스만 차분한다 → 방금 켠 프로세스는 첫 측정에서 빠진다.
//   video1 cpu-hog: PC 전체 88.9% 인데 상위 목록에 범인이 없었다(30초 뒤 측정에서야 99.2% 로 등장).
// 확인할 것:
//   (1) "% Processor Time" 의 RawValue 가 프로세스 시작 이후 누적 CPU 시간(100ns)인가 → 새 프로세스는 RawValue / (두 읽기의 TimeStamp100nSec 차) 로 계산
//   (2) "Elapsed Time" 한 표본으로 프로세스가 켜진 지 몇 초인지 알 수 있는가 → "새 프로세스" 판별
//   (3) (1)의 값이 다음 구간의 정식 두 표본 계산과 같은 눈금인가
// 방법: 이 프로그램이 자기 자신을 "burn <초> <스레드>" 로 띄워 CPU 를 쓰게 한다(자식은 계산만 한다).
using System.Diagnostics;
using System.Text.Json;

if (args.Length >= 3 && args[0] == "burn")
{
    // 먼저 64MB 를 임시 파일에 쓰고 지운다 — "IO Data Bytes/sec" 의 RawValue 가 누적 바이트인지 보려는 것
    var tmp = Path.Combine(Path.GetTempPath(), $"spikeL_{Environment.ProcessId}.bin");
    var buf = new byte[1 << 20];
    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        for (int i = 0; i < 64; i++) fs.Write(buf, 0, buf.Length);
    File.Delete(tmp);
    var end = DateTime.UtcNow.AddSeconds(int.Parse(args[1]));
    var ts = Enumerable.Range(0, int.Parse(args[2])).Select(_ => new Thread(() => { double x = 1; while (DateTime.UtcNow < end) x = Math.Sqrt(x + 1.0001); })).ToList();
    ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join());
    return;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
int cpus = Environment.ProcessorCount;
const int threads = 2;
var cat = new PerformanceCounterCategory("Process");
var self = Process.GetCurrentProcess();

var a = cat.ReadCategory();
long tsA = a["% Processor Time"]["Idle"].Sample.TimeStamp100nSec;
Thread.Sleep(1500);
var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"burn 9 {threads}") { UseShellExecute = false })!;
Thread.Sleep(3000);

var b = cat.ReadCategory();
long tsB = b["% Processor Time"]["Idle"].Sample.TimeStamp100nSec;
// 자식의 인스턴스 이름 찾기 (PID 로)
string? inst = null;
foreach (System.Collections.DictionaryEntry e in b["ID Process"])
    if (((InstanceData)e.Value!).RawValue == child.Id) inst = (string)e.Key;

var result = new Dictionary<string, object?> { ["logicalCpus"] = cpus, ["burnThreads"] = threads, ["childPid"] = child.Id, ["childInstance"] = inst };
if (inst != null)
{
    bool inPrev = a["% Processor Time"].Contains(inst);
    long raw = b["% Processor Time"][inst].RawValue;
    double intervalSec = (tsB - tsA) / 1e7;
    double elapsedSec = CounterSample.Calculate(b["Elapsed Time"][inst].Sample);
    result["firstRead"] = new
    {
        instanceWasInPreviousRead = inPrev,
        rawValue = raw,
        intervalSec = Math.Round(intervalSec, 2),
        elapsedSec_fromElapsedTimeCounter = Math.Round(elapsedSec, 2),
        cpuSecondsIfRawIs100ns = Math.Round(raw / 1e7, 2),
        // 자식이 쓴 양은 64MB(+런타임이 읽은 파일 약간) — RawValue 가 누적 바이트라면 이 근처여야 한다
        ioRawMB = Math.Round(b["IO Data Bytes/sec"][inst].RawValue / 1048576.0, 1),
        pidWasInPreviousRead = a["ID Process"].Values.Cast<InstanceData>().Any(x => x.RawValue == child.Id),
        // 새 프로세스: 누적 CPU 시간 전부가 이 구간 안에서 쓰였다
        machinePct_singleSample = Math.Round(raw / (double)(tsB - tsA) * 100 / cpus, 1),
        // 기대값: threads 개 스레드가 elapsed 동안 → (threads × elapsed / interval) / cpus × 100
        machinePct_expected = Math.Round(threads * Math.Min(elapsedSec, intervalSec) / intervalSec / cpus * 100, 1),
    };

    Thread.Sleep(3000);
    var c = cat.ReadCategory();
    if (c["% Processor Time"].Contains(inst) && c["ID Process"][inst].RawValue == child.Id)
    {
        double two = CounterSample.Calculate(b["% Processor Time"][inst].Sample, c["% Processor Time"][inst].Sample) / cpus;
        result["secondRead_twoSample"] = new { machinePct = Math.Round(two, 1), machinePct_expected = Math.Round(threads * 100.0 / cpus, 1) };
    }
}
child.WaitForExit();
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
