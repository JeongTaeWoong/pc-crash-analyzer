using BlackBox.Core;
using LibreHardwareMonitor.Hardware;

namespace BlackBox.Sources;

/// <summary>
/// 센서 수집 (2초 주기) — v0.4부터 GPU 기준.
/// 현 보안 구성에서 CPU/보드 센서는 미지원 확정(Defender의 WinRing0 차단 + PawnIO 미설치, NOTES 참고)이라
/// HW 시나리오를 "GPU 온도 상승 → GPU 클럭 하락 → 프레임 저하"로 재편.
/// 수집: 온도·클럭·전력·팬 전체 + 이름이 "GPU"로 시작하는 Load(전력 한계 사용률 TDP% 포함, D3D* 노이즈 제외).
/// LHM 0.9.7은 백엔드 없는 CPU 센서를 null이 아닌 0값으로 반환하므로,
/// 물리적으로 0이 불가능한 타입(온도/클럭/전력)의 0값은 값으로 기록하지 않고 unavailable로 집계한다.
/// Fan 0 RPM(세미패시브 정지)과 Load 0%는 실제 의미가 있으므로 그대로 기록.
/// 스로틀 사유(NVML throttle reason) 직접 센서는 LHM 0.9.7에 없음 — TDP%·온도·클럭 공존 상관으로 추론.
/// </summary>
public sealed class SensorSource : PolledSourceBase
{
    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;

    public SensorSource() : base("sensors", TimeSpan.FromSeconds(2)) { }

    /// <summary>마킹 시점 상태 스냅샷에서 쓰는 가장 최근 샘플.</summary>
    public object? Latest { get; private set; }

    /// <summary>벤치 전용 — 운영 코드에서 쓰지 않는다.</summary>
    internal object SampleOnceForBench() => Sample();

    private static bool Collectable(ISensor s) =>
        s.SensorType is SensorType.Temperature or SensorType.Clock or SensorType.Power or SensorType.Fan
        || (s.SensorType == SensorType.Load && s.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase));

    private static bool ZeroMeansUnavailable(SensorType t) =>
        t is SensorType.Temperature or SensorType.Clock or SensorType.Power;

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
        void Collect(IHardware hw)
        {
            foreach (var s in hw.Sensors)
            {
                if (!Collectable(s) || !s.Value.HasValue) continue;
                float v = s.Value.Value;
                if (v == 0 && ZeroMeansUnavailable(s.SensorType))
                    zeroUnavailable.Add($"{hw.Name}/{s.Name}");
                else
                    values.Add(new { hw = hw.Name, type = s.SensorType.ToString(), name = s.Name, value = Math.Round(v, 1) });
            }
            foreach (var sub in hw.SubHardware) Collect(sub);
        }
        foreach (var hw in _computer.Hardware) Collect(hw);

        var payload = new
        {
            sensors = values,
            zeroAsUnavailableCount = zeroUnavailable.Count,
            zeroAsUnavailableSample = zeroUnavailable.Take(3).ToList(),
        };
        Latest = payload;
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
