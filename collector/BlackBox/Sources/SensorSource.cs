using BlackBox.Core;
using LibreHardwareMonitor.Hardware;

namespace BlackBox.Sources;

/// <summary>
/// 센서 수집 (2초 주기) — v0.4부터 GPU 기준.
/// 현 보안 구성에서 CPU/보드 센서는 미지원 확정(Defender의 WinRing0 차단 + PawnIO 미설치, NOTES 참고)이라
/// HW 시나리오를 "GPU 온도 상승 → GPU 클럭 하락 → 프레임 저하"로 재편.
/// 수집: 온도·클럭·전력·팬 전체 + 이름이 "GPU"로 시작하는 Load(전력 한계 사용률 TDP% 포함, D3D* 노이즈 제외)
///       + GPU 메모리 Used/Total/Free(v0.6.1, 스파이크 E 재검증 10/29 — 느려짐 B 메커니즘 VRAM 압박).
/// LHM 0.9.7은 백엔드 없는 CPU 센서를 null이 아닌 0값으로 반환하므로,
/// 물리적으로 0이 불가능한 타입(온도/클럭/전력)의 0값은 값으로 기록하지 않고 unavailable로 집계한다.
/// Fan 0 RPM(세미패시브 정지)과 Load 0%는 실제 의미가 있으므로 그대로 기록.
/// 스로틀 사유(NVML throttle reason) 직접 센서는 LHM 0.9.7에 없음(10/29 재확인) — TDP%·온도·클럭 공존 상관으로 추론.
/// 주 GPU: 메모리 총량이 가장 큰 GPU (10/29 발견: 하드웨어 열거 순서상 CPU 내장 그래픽이 먼저 나와 롤업이 iGPU 값을 쓰고 있었음).
/// </summary>
public sealed class SensorSource : PolledSourceBase
{
    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;

    public SensorSource() : base("sensors", TimeSpan.FromSeconds(2)) { }

    /// <summary>마킹 시점 상태 스냅샷에서 쓰는 가장 최근 샘플.</summary>
    public object? Latest { get; private set; }

    /// <summary>롤업용 주 GPU 요약(온도·핫스팟·클럭·전력·메모리).</summary>
    public GpuSample? LatestGpu { get; private set; }

    /// <summary>벤치 전용 — 운영 코드에서 쓰지 않는다.</summary>
    internal object SampleOnceForBench() => Sample();

    private static bool IsGpuMemorySensor(ISensor s) =>
        s.SensorType == SensorType.Data
        && s.Name is "GPU Memory Used" or "GPU Memory Total" or "GPU Memory Free";

    private static bool Collectable(ISensor s) =>
        s.SensorType is SensorType.Temperature or SensorType.Clock or SensorType.Power or SensorType.Fan
        || (s.SensorType == SensorType.Load && s.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase))
        || IsGpuMemorySensor(s);

    private static bool ZeroMeansUnavailable(SensorType t) =>
        t is SensorType.Temperature or SensorType.Clock or SensorType.Power;

    /// <summary>
    /// LHM 메모리 센서 값을 MB로. 0.9.7-pre749 실측(10/29): NVIDIA "GPU Memory Total" 이 Data 타입인데 값은 바이트(17171480576).
    /// 이 버전엔 SmallData 형식이 없다(컴파일 확인). 예전 LHM 은 Data=GB 였으므로 크기로 판별한다: 1e6 초과면 바이트, 아니면 GB.
    /// </summary>
    public static double ToMB(SensorType type, double v)
    {
        if (v > 1e6) return v / 1048576.0;
        return type == SensorType.Data ? v * 1024.0 : v;
    }

    private sealed class GpuReading
    {
        public string Name = "";
        public double? Temp, Hot, Clock, Power, MemUsedMB, MemTotalMB;
    }

    protected override object Sample()
    {
        if (_computer == null)
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
            _computer.Open();
        }

        _computer.Accept(_visitor);

        var values = new List<object>();
        var zeroUnavailable = new List<string>();
        var gpus = new List<GpuReading>();
        void Collect(IHardware hw)
        {
            bool isGpu = hw.HardwareType.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);
            GpuReading? g = isGpu ? new GpuReading { Name = hw.Name } : null;
            foreach (var s in hw.Sensors)
            {
                if (!Collectable(s) || !s.Value.HasValue) continue;
                float v = s.Value.Value;
                if (v == 0 && ZeroMeansUnavailable(s.SensorType))
                {
                    zeroUnavailable.Add($"{hw.Name}/{s.Name}");
                    continue;
                }
                if (IsGpuMemorySensor(s))
                {
                    double mb = Math.Round(ToMB(s.SensorType, v));
                    values.Add(new { hw = hw.Name, type = "GpuMemoryMB", name = s.Name, value = mb });
                    if (g != null && s.Name == "GPU Memory Used") g.MemUsedMB ??= mb;
                    if (g != null && s.Name == "GPU Memory Total") g.MemTotalMB ??= mb;
                    continue;
                }
                values.Add(new { hw = hw.Name, type = s.SensorType.ToString(), name = s.Name, value = Math.Round(v, 1) });
                if (g != null)
                {
                    if (s.SensorType == SensorType.Temperature && s.Name == "GPU Core") g.Temp ??= v;
                    else if (s.SensorType == SensorType.Temperature && s.Name == "GPU Hot Spot") g.Hot ??= v;
                    else if (s.SensorType == SensorType.Clock && s.Name == "GPU Core") g.Clock ??= v;
                    else if (s.SensorType == SensorType.Power && s.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) g.Power ??= v;
                }
            }
            if (g != null) gpus.Add(g);
            foreach (var sub in hw.SubHardware) Collect(sub);
        }
        foreach (var hw in _computer.Hardware) Collect(hw);

        // 주 GPU = 메모리 총량 최대 (없으면 이름에 NVIDIA/Radeon RX/Arc 가 든 것, 그래도 없으면 첫 GPU)
        var primary = gpus.Where(x => x.MemTotalMB.HasValue).OrderByDescending(x => x.MemTotalMB).FirstOrDefault()
                      ?? gpus.FirstOrDefault(x => x.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                      ?? gpus.FirstOrDefault();

        var payload = new
        {
            primaryGpu = primary?.Name,
            sensors = values,
            zeroAsUnavailableCount = zeroUnavailable.Count,
            zeroAsUnavailableSample = zeroUnavailable.Take(3).ToList(),
        };
        Latest = payload;
        LatestGpu = primary == null || (primary.Temp ?? primary.Hot ?? primary.Clock ?? primary.Power ?? primary.MemUsedMB) is null
            ? null
            : new GpuSample(DateTimeOffset.Now, primary.Temp, primary.Hot, primary.Clock, primary.Power,
                primary.MemUsedMB, primary.MemTotalMB, primary.Name);
        return payload;
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _computer?.Close(); } catch { }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
