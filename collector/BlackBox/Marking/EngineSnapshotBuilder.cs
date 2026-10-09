using System.Text.Json;
using BlackBox.Core;

namespace BlackBox.Marking;

/// <summary>
/// 덤프를 추론 엔진 입력 규격(engine/docs/snapshot_schema.md v1.1)으로 변환한다.
/// 스키마 약속: 수집 실패 항목은 빈 값 대신 필드 자체를 생략, 시각은 ISO-8601+오프셋,
/// 센서가 없으면 sensors를 아예 뺀다 (0을 넣지 않는다).
/// 변환은 포맷팅일 뿐 분석이 아니다 — 추론은 엔진의 몫.
/// </summary>
public static class EngineSnapshotBuilder
{
    public static object Build(
        DateTimeOffset markTime,
        string kind,
        EventLogTrigger? trigger,
        IReadOnlyList<SignalEvent> events,
        EngineFacts facts,
        List<string>? crashModulePaths)
    {
        // kind → trigger.kind / symptom 매핑.
        // manual은 실제 제품 흐름에서 UI가 symptom을 사용자에게 받아 채운다(스키마 명세) — 여기선 unknown.
        string triggerKind, symType, symScope;
        switch (kind)
        {
            case "crash-1000":
            case "crash-1001":
                triggerKind = "auto_app_crash"; symType = "app_crash"; symScope = "single_app"; break;
            case "power-41":
                triggerKind = "auto_system"; symType = "unexpected_reboot"; symScope = "system_wide"; break;
            case "display-4101":
                triggerKind = "auto_system"; symType = "flicker"; symScope = "system_wide"; break;
            case "whea":
                triggerKind = "auto_system"; symType = "freeze"; symScope = "system_wide"; break; // WHEA 전용 타입이 스키마에 없어 임시 매핑
            default:
                triggerKind = "hotkey"; symType = "unknown"; symScope = "unknown"; break;
        }

        // 버퍼 이벤트에서 엔진용 events / sensors.samples / processes 추출
        var engineEvents = new List<object>();
        var sensorSamples = new List<Dictionary<string, object>>();
        List<string> processes = new();
        foreach (var e in events)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                var root = doc.RootElement;
                switch (e.Source)
                {
                    case "eventlog":
                    {
                        var ev = new Dictionary<string, object> { ["time"] = e.Timestamp, ["id"] = root.GetProperty("id").GetInt32() };
                        if (root.TryGetProperty("provider", out var pv) && pv.GetString() is { } prov) ev["provider"] = prov;
                        if (root.TryGetProperty("subject", out var su) && su.GetString() is { } subj) ev["subject"] = subj;
                        if (root.TryGetProperty("faultingModule", out var fm) && fm.GetString() is { } mod) ev["faulting_module"] = mod;
                        engineEvents.Add(ev);
                        break;
                    }
                    case "sensors" when root.TryGetProperty("sensors", out var arr):
                    {
                        double? gpuTemp = null, gpuClock = null;
                        foreach (var s in arr.EnumerateArray())
                        {
                            if (!(s.GetProperty("hw").GetString() ?? "").Contains("NVIDIA")) continue;
                            if (s.GetProperty("name").GetString() != "GPU Core") continue;
                            var t = s.GetProperty("type").GetString();
                            if (t == "Temperature") gpuTemp = s.GetProperty("value").GetDouble();
                            else if (t == "Clock") gpuClock = s.GetProperty("value").GetDouble();
                        }
                        if (gpuTemp.HasValue || gpuClock.HasValue)
                        {
                            var sample = new Dictionary<string, object> { ["time"] = e.Timestamp };
                            if (gpuTemp.HasValue) sample["gpu_temp"] = gpuTemp.Value;
                            if (gpuClock.HasValue) sample["gpu_clock"] = gpuClock.Value;
                            sensorSamples.Add(sample);
                        }
                        break;
                    }
                    case "processes" when root.TryGetProperty("processes", out var procs):
                    {
                        var names = new List<string>();
                        foreach (var p in procs.EnumerateArray())
                            if (p.TryGetProperty("name", out var nm) && nm.GetString() is { } n) names.Add(n);
                        if (names.Count > 0) processes = names; // 가장 최신 스냅샷이 남는다
                        break;
                    }
                }
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        string targetProcess = trigger?.ProcessName ?? "";
        var snapshot = new Dictionary<string, object?>
        {
            ["schema_version"] = "1.1",
            ["snapshot_id"] = $"{markTime:yyyyMMdd-HHmmss}-{kind}",
            ["marked_at"] = markTime,
            ["trigger"] = trigger != null
                ? new { kind = triggerKind, event_id = trigger.EventId }
                : (object)new { kind = triggerKind },
            ["host"] = new { os = $"Windows {Environment.OSVersion.Version}", sensor_support = sensorSamples.Count > 0 },
            ["symptom"] = new
            {
                type = symType,
                target_process = targetProcess,
                scope = symScope,
                started_at = trigger?.EventTime ?? markTime,
                faulting_modules = trigger?.FaultingModule is { Length: > 0 } f ? new[] { f } : Array.Empty<string>(),
            },
        };

        if (processes.Count > 0)
            snapshot["processes"] = processes.Take(300).ToList();

        if (crashModulePaths is { Count: > 0 } && targetProcess.Length > 0)
            snapshot["modules"] = crashModulePaths
                .Select(p => new { process = targetProcess, name = Path.GetFileName(p), path = p }).ToList();

        var drivers = new List<object>();
        foreach (var (name, _) in facts.NonStandardDrivers)
            drivers.Add(new { name });
        foreach (var mf in facts.Minifilters)
            drivers.Add(new { name = mf, type = "kernel_filter" });
        if (drivers.Count > 0)
            snapshot["drivers"] = drivers;

        if (facts.KeyboardUpper.Count > 0 || facts.MouseUpper.Count > 0)
            snapshot["device_filters"] = new { keyboard_upper = facts.KeyboardUpper, mouse_upper = facts.MouseUpper };

        if (facts.SecurityProducts.Count > 0)
            snapshot["security"] = new { realtime_av = facts.SecurityProducts };

        if (facts.RecentInstalls.Count > 0)
            snapshot["change_history"] = facts.RecentInstalls
                .Select(i => new { time = $"{i.Date}T00:00:00{markTime:zzz}", kind = "install", subject = i.Name, version = i.Version })
                .ToList();

        if (engineEvents.Count > 0)
            snapshot["events"] = engineEvents;

        if (sensorSamples.Count > 0)
            snapshot["sensors"] = new { samples = sensorSamples };

        return snapshot;
    }
}
