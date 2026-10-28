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
            notes.Add($"gpu: {ex.GetType().Name}: {ex.Message}");
        }

        // 3) 프로세스 CPU/IO — ReadCategory가 ~260ms(553 인스턴스, 스파이크 I)라 30초마다(3틱) 읽고 그 사이엔 직전 값을 유지
        _tick++;
        var top = _lastTop;
        double cpuTotal = _lastCpuTotal;
        if (_tick % 3 == 0) try
        {
            _procCat ??= new PerformanceCounterCategory("Process");
            var cur = _procCat.ReadCategory();
            if (_prevProc != null)
            {
                var cpuA = _prevProc["% Processor Time"]; var cpuB = cur["% Processor Time"];
                var pid = cur["ID Process"]; var priv = cur["Private Bytes"]; var hnd = cur["Handle Count"];
                var ioA = _prevProc["IO Data Bytes/sec"]; var ioB = cur["IO Data Bytes/sec"];
                var rows = new List<ProcUsage>();
                foreach (System.Collections.DictionaryEntry e in cpuB)
                {
                    var inst = (string)e.Key;
                    if (!cpuA.Contains(inst)) continue;
                    double cpu = CounterSample.Calculate(cpuA[inst].Sample, cpuB[inst].Sample) / _cpuCount;
                    if (inst.Equals("Idle", StringComparison.OrdinalIgnoreCase)) { cpuTotal = Math.Clamp(100 - cpu, 0, 100); continue; }
                    if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    double io = ioA.Contains(inst) ? CounterSample.Calculate(ioA[inst].Sample, ioB[inst].Sample) / 1048576.0 : 0;
                    rows.Add(new ProcUsage((int)pid[inst].RawValue, StripInstanceSuffix(inst), Math.Round(cpu, 1),
                        Math.Round(priv[inst].RawValue / 1048576.0, 1), hnd[inst].RawValue, Math.Round(io, 2)));
                }
                top = rows.OrderByDescending(r => r.Cpu).Take(10).ToList();
                foreach (var r in rows.OrderByDescending(r => r.IoMBps).Take(3))
                    if (!top.Any(t => t.Pid == r.Pid) && r.IoMBps >= 1) top.Add(r);
                _lastTop = top; _lastCpuTotal = cpuTotal;
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
            cpuTotalPct = double.IsNaN(cpuTotal) ? null : (double?)Math.Round(cpuTotal, 1),
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
    }
}
