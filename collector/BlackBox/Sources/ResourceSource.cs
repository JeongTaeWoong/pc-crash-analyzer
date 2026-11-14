using System.Diagnostics;
using BlackBox.Core;

namespace BlackBox.Sources;

/// <summary>
/// 시스템 자원 샘플러(10초): 커밋 메모리·가용 메모리·페이징, GPU 어댑터 전용/공유 메모리(VidMm), 전체 CPU,
/// 프로세스별 CPU%/IO 상위(Process 카운터 카테고리 1회 읽기 — 접근 거부 없음).
/// 느려짐 메커니즘 A(앱 메모리)·B(VRAM)·E(외부 간섭)의 원천. 스파이크 I(2026-10-29, ko-KR)에서 영어 카운터 이름이 그대로 동작함을 확인.
/// 프로세스별 GPU 메모리 카운터는 Microsoft가 오류를 인정해(KB4490156) 쓰지 않는다 — 어댑터 총량만.
/// </summary>
public sealed class ResourceSource : PolledSourceBase
{
    private PerformanceCounter? _committed, _commitLimit, _committedPct, _availMB, _pagesPerSec;
    private PerformanceCounterCategory? _gpuCat;
    private List<(string Inst, PerformanceCounter Ded, PerformanceCounter Shared)>? _gpu;
    private PerformanceCounterCategory? _procCat;
    private InstanceDataCollectionCollection? _prevProc;
    private readonly int _cpuCount = Environment.ProcessorCount;
    private long _tick = -1;
    private List<ProcUsage> _lastTop = new();
    private double _lastCpuTotal = double.NaN;
    // 디스크 수준 신호(스파이크 K, 2026-11-12): PhysicalDisk 인스턴스("1 C:")별 카운터. 읽기 1ms.
    private static readonly string[] DiskCounterNames = { "% Idle Time", "Avg. Disk Queue Length", "Avg. Disk sec/Transfer", "Disk Transfers/sec", "Disk Bytes/sec" };
    private List<(string Inst, PerformanceCounter[] C)>? _disks;
    private long _disksOpenedAtTick;
    private DateTimeOffset? _cpuAt;

    public ResourceSource() : base("resources", TimeSpan.FromSeconds(10)) { }

    /// <summary>롤업·스냅샷용 최신 샘플.</summary>
    public ResourceSample? Latest { get; private set; }

    protected override object Sample()
    {
        var sw = Stopwatch.StartNew();
        var at = DateTimeOffset.Now;
        var notes = new List<string>();

        // 1) 시스템 메모리
        _committed ??= new PerformanceCounter("Memory", "Committed Bytes", readOnly: true);
        _commitLimit ??= new PerformanceCounter("Memory", "Commit Limit", readOnly: true);
        _committedPct ??= new PerformanceCounter("Memory", "% Committed Bytes In Use", readOnly: true);
        _availMB ??= new PerformanceCounter("Memory", "Available MBytes", readOnly: true);
        _pagesPerSec ??= new PerformanceCounter("Memory", "Pages/sec", readOnly: true);
        double committedMB = _committed.NextValue() / 1048576.0;
        double limitMB = _commitLimit.NextValue() / 1048576.0;
        double committedPct = _committedPct.NextValue();
        double availMB = _availMB.NextValue();
        double pages = _pagesPerSec.NextValue(); // 비율형: 첫 호출은 0 — 다음 10초 샘플부터 유효

        // 2) GPU 어댑터 메모리 (인스턴스는 LUID 기준, 보통 3개: dGPU·iGPU·소프트웨어 어댑터)
        double gpuDed = double.NaN, gpuShared = double.NaN;
        var gpuRows = new List<object>();
        try
        {
            if (_gpu == null)
            {
                _gpuCat ??= new PerformanceCounterCategory("GPU Adapter Memory");
                _gpu = _gpuCat.GetInstanceNames().Select(i => (i,
                    new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", i, readOnly: true),
                    new PerformanceCounter("GPU Adapter Memory", "Shared Usage", i, readOnly: true))).ToList();
            }
            gpuDed = 0; gpuShared = 0;
            foreach (var (inst, ded, sh) in _gpu)
            {
                double d = ded.NextValue() / 1048576.0, s = sh.NextValue() / 1048576.0;
                gpuDed += d; gpuShared += s;
                if (d >= 1) gpuRows.Add(new { instance = inst, dedicatedMB = Math.Round(d), sharedMB = Math.Round(s) });
            }
        }
        catch (Exception ex)
        {
            _gpu = null; // 어댑터 구성이 바뀌면 다음 샘플에서 다시 열거
            gpuDed = double.NaN; gpuShared = double.NaN; gpuRows.Clear(); // 부분합을 값으로 내보내지 않는다
            notes.Add($"gpu: {ex.GetType().Name}: {ex.Message}");
        }

        // 2b) 디스크: 디스크마다 "쉬는 시간 비율·대기열·응답 시간·초당 전송". CPU·메모리가 한가한데 느린 PC(느린 디스크)의 근거.
        //     _Total 은 디스크 평균이라 한 디스크만 바쁜 경우를 가린다 — 디스크별로 적는다. 비율형이라 첫 샘플은 0(다음 10초부터 유효).
        _tick++;
        object diskObj;
        try
        {
            if (_disks == null || _tick - _disksOpenedAtTick >= 30) // 5분마다 다시 열거(USB 디스크 연결·해제)
            {
                if (_disks != null) foreach (var (_, cs) in _disks) foreach (var c in cs) { try { c.Dispose(); } catch { } }
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                _disks = cat.GetInstanceNames().Where(i => i != "_Total").OrderBy(i => i, StringComparer.Ordinal)
                    .Select(i => (i, DiskCounterNames.Select(n => new PerformanceCounter("PhysicalDisk", n, i, readOnly: true)).ToArray())).ToList();
                foreach (var (_, cs) in _disks) foreach (var c in cs) c.NextValue();
                _disksOpenedAtTick = _tick;
                diskObj = new { status = "warming", reason = "비율형 카운터 첫 샘플 — 다음 샘플부터 유효" };
            }
            else
            {
                var rows = new List<object>();
                foreach (var (inst, cs) in _disks)
                {
                    double idle = Math.Clamp(cs[0].NextValue(), 0, 100);
                    rows.Add(new
                    {
                        name = inst,
                        busyPct = Math.Round(100 - idle, 1),
                        queue = Math.Round(cs[1].NextValue(), 2),
                        latencyMs = Math.Round(cs[2].NextValue() * 1000, 2),
                        iops = Math.Round(cs[3].NextValue(), 1),
                        mbps = Math.Round(cs[4].NextValue() / 1048576.0, 2),
                    });
                }
                diskObj = new { disks = rows };
            }
        }
        catch (Exception ex)
        {
            _disks = null; // 다음 샘플에서 다시 열거
            diskObj = new { status = "unavailable", reason = $"{ex.GetType().Name}: {ex.Message}" };
        }

        // 3) 프로세스 CPU/IO — ReadCategory가 ~260ms(553 인스턴스, 스파이크 I)라 30초마다(3틱) 읽고 그 사이엔 직전 값을 유지
        var top = _lastTop;
        double cpuTotal = _lastCpuTotal;
        if (_tick % 3 == 0) try
        {
            _procCat ??= new PerformanceCounterCategory("Process");
            var cur = _procCat.ReadCategory();
            if (_prevProc != null)
            {
                var cpuA = _prevProc["% Processor Time"]; var cpuB = cur["% Processor Time"];
                var pidA = _prevProc["ID Process"]; var pid = cur["ID Process"]; var priv = cur["Private Bytes"]; var hnd = cur["Handle Count"];
                var ioA = _prevProc["IO Data Bytes/sec"]; var ioB = cur["IO Data Bytes/sec"];
                var rows = new List<ProcUsage>();
                // 직전 읽기의 PID → 인스턴스 이름. 인스턴스 이름(chrome#3)은 30초 사이 다른 프로세스로 재배정될 수 있어
                // 이름이 아니라 PID 로 짝을 맞춘다 (Idle·_Total 은 PID 0 을 같이 쓰므로 뺀다).
                var prevInstByPid = new Dictionary<long, string>();
                foreach (System.Collections.DictionaryEntry e in pidA)
                {
                    var name = (string)e.Key;
                    if (name.Equals("Idle", StringComparison.OrdinalIgnoreCase) || name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    prevInstByPid[((InstanceData)e.Value!).RawValue] = name;
                }
                long interval100ns = cpuA.Contains("Idle") && cpuB.Contains("Idle")
                    ? cpuB["Idle"].Sample.TimeStamp100nSec - cpuA["Idle"].Sample.TimeStamp100nSec : 0;
                foreach (System.Collections.DictionaryEntry e in cpuB)
                {
                    var inst = (string)e.Key;
                    if (inst.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                    {
                        if (cpuA.Contains(inst))
                            cpuTotal = Math.Clamp(100 - CounterSample.Calculate(cpuA[inst].Sample, cpuB[inst].Sample) / _cpuCount, 0, 100);
                        continue;
                    }
                    if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase) || !pid.Contains(inst)) continue;
                    long p = pid[inst].RawValue;
                    double cpu, io;
                    if (prevInstByPid.TryGetValue(p, out var prevInst) && cpuA.Contains(prevInst))
                    {
                        cpu = CounterSample.Calculate(cpuA[prevInst].Sample, cpuB[inst].Sample) / _cpuCount;
                        io = ioA.Contains(prevInst) && ioB.Contains(inst) ? CounterSample.Calculate(ioA[prevInst].Sample, ioB[inst].Sample) / 1048576.0 : 0;
                    }
                    else if (interval100ns > 0)
                    {
                        // 직전 읽기에 없던 PID = 이 구간 안에서 새로 켜진 프로세스. 차분할 앞 표본이 없으므로 누적값을 그대로 쓴다:
                        // "% Processor Time" 원시값 = 시작 이후 누적 CPU 시간(100ns), "IO Data Bytes/sec" 원시값 = 누적 바이트
                        // (스파이크 L, 2026-11-12: 2스레드 3초 → 5.97 CPU초, 64MB 쓰기 → 64MB).
                        // 이게 없으면 방금 켠 프로그램은 첫 측정에서 빠진다 — 11/12 사례: PC 전체 88.9% 인데 상위 목록에 범인이 없었다.
                        cpu = cpuB[inst].RawValue / (double)interval100ns * 100 / _cpuCount;
                        io = ioB.Contains(inst) ? ioB[inst].RawValue / 1048576.0 / (interval100ns / 1e7) : 0;
                    }
                    else continue;
                    rows.Add(new ProcUsage((int)p, StripInstanceSuffix(inst), Math.Round(Math.Clamp(cpu, 0, 100), 1),
                        Math.Round(priv[inst].RawValue / 1048576.0, 1), hnd[inst].RawValue, Math.Round(Math.Max(io, 0), 2)));
                }
                top = rows.OrderByDescending(r => r.Cpu).Take(10).ToList();
                foreach (var r in rows.OrderByDescending(r => r.IoMBps).Take(3))
                    if (!top.Any(t => t.Pid == r.Pid) && r.IoMBps >= 1) top.Add(r);
                _lastTop = top; _lastCpuTotal = cpuTotal; _cpuAt = at;
            }
            _prevProc = cur;
        }
        catch (Exception ex)
        {
            _prevProc = null;
            notes.Add($"process: {ex.GetType().Name}: {ex.Message}");
        }

        Latest = new ResourceSample(at, committedPct, committedMB, limitMB, availMB, pages, gpuDed, gpuShared, cpuTotal, top);

        return new
        {
            scanMs = sw.ElapsedMilliseconds,
            memory = new
            {
                committedMB = Math.Round(committedMB), commitLimitMB = Math.Round(limitMB), committedPct = Math.Round(committedPct, 1),
                availMB = Math.Round(availMB), pagesPerSec = Math.Round(pages, 1),
            },
            gpu = double.IsNaN(gpuDed) ? new { status = "unavailable" } : (object)new { dedicatedMB = Math.Round(gpuDed), sharedMB = Math.Round(gpuShared), adapters = gpuRows },
            disk = diskObj,
            cpuTotalPct = double.IsNaN(cpuTotal) ? null : (double?)Math.Round(cpuTotal, 1),
            // cpuAt: 이 topCpu·cpuTotalPct 를 실제로 잰 시각(30초마다). 사이 샘플은 같은 값을 다시 싣는다 — 요약할 때 중복을 걸러내는 열쇠
            cpuAt = _cpuAt,
            topCpu = top.Select(t => new { t.Pid, t.Name, t.Cpu, t.PrivMB, t.Handles, io = t.IoMBps }).ToList(),
            notes = notes.Count > 0 ? notes : null,
        };
    }

    private static string StripInstanceSuffix(string inst)
    {
        int i = inst.LastIndexOf('#');
        return i > 0 && int.TryParse(inst[(i + 1)..], out _) ? inst[..i] : inst;
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var c in new[] { _committed, _commitLimit, _committedPct, _availMB, _pagesPerSec })
            try { c?.Dispose(); } catch { }
        if (_gpu != null)
            foreach (var (_, d, s) in _gpu) { try { d.Dispose(); s.Dispose(); } catch { } }
        if (_disks != null)
            foreach (var (_, cs) in _disks) foreach (var c in cs) { try { c.Dispose(); } catch { } }
    }
}
