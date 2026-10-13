// 스파이크 E: LibreHardwareMonitorLib로 CPU/GPU/보드 센서 읽기.
// 비관리자면 Ring0 드라이버 로드가 안 돼 CPU/SuperIO는 빈 값 예상 — 그래도 시도하고 결과를 기록.
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "E-Sensors", ["ranAt"] = DateTime.Now.ToString("s") };
using (var identity = WindowsIdentity.GetCurrent())
    result["isElevated"] = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
var values = new List<object>();
var nullSensors = new List<string>();

try
{
    computer.Open();
    computer.Accept(new UpdateVisitor());

    bool all = args.Contains("all"); // "all" 인자: 전체 센서 타입 덤프 (스로틀/리밋 센서 탐색용)
    void Collect(IHardware hw)
    {
        foreach (var s in hw.Sensors)
        {
            if (all || s.SensorType is SensorType.Temperature or SensorType.Clock or SensorType.Power)
            {
                if (s.Value.HasValue)
                    values.Add(new { hardware = hw.Name, type = s.SensorType.ToString(), name = s.Name, value = Math.Round(s.Value.Value, 1) });
                else
                    nullSensors.Add($"{hw.Name}/{s.SensorType}/{s.Name}");
            }
        }
        foreach (var sub in hw.SubHardware) Collect(sub);
    }
    foreach (var hw in computer.Hardware) Collect(hw);

    result["hardwareDetected"] = computer.Hardware.Select(h => $"{h.HardwareType}: {h.Name}").ToList();
    result["sensorValues"] = values.Take(all ? 250 : 60).ToList();
    result["nullSensors_미지원"] = nullSensors.Take(30).ToList();
    if (values.Count > 0)
    {
        result["status"] = "ok";
        result["note"] = $"값 있는 센서 {values.Count}개, null 센서 {nullSensors.Count}개";
    }
    else
    {
        result["status"] = "unavailable";
        result["reason"] = "센서 값이 하나도 없음 (비관리자라 Ring0 드라이버 미로드일 가능성)";
    }
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}
finally
{
    try { computer.Close(); } catch { }
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

class UpdateVisitor : IVisitor
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
