using System.Text.Json;
using BlackBox.Core;

namespace BlackBox.Marking;

/// <summary>
/// 덤프 창에서 끝난 프로세스의 종료 코드를 엔진 스냅샷용으로 간추린다 (확장 필드 process_exits, 11/15).
///
/// 왜: Windows 크래시 이벤트(Application 1000/1001) 없이 꺼지는 프로그램이 있다 — 자체 크래시 처리기가 예외를 잡고
/// 종료 코드만 남기는 경우. 11/14~15 실험실에서 Blender 5.1.0 이 종료 코드 11 로 꺼졌고 이벤트 로그에는 아무것도 없었다(3/3).
/// 원시 기록(etw-kernel processStop)에는 종료 코드가 있었지만 엔진 스냅샷에는 없었고, 꺼진 프로그램은 마킹 시점의
/// 프로세스 목록에도 없으므로 스냅샷만 보는 쪽은 그 프로그램이 있었다는 것조차 알 수 없었다.
///
/// 여기서는 적기만 한다. 0 이 아닌 코드가 크래시인지 평소 종료인지는 판단하지 않는다
/// (개발용 덤프 124개: 0 이 아닌 종료가 덤프당 중앙값 12건, 그중 대부분이 backgroundTaskHost.exe 의 1).
/// 그래서 이름·코드별로 묶어 횟수와 마지막 시각만 남긴다. 마킹 대상 프로그램은 코드가 0 이어도 적는다.
/// </summary>
public static class ProcessExits
{
    public const int MaxRows = 25;

    private sealed class Group
    {
        public string Name = "";
        public uint Code;
        public int Count;
        public DateTimeOffset LastAt;
        public double? LivedSec;   // 마지막으로 끝난 인스턴스가 살아 있던 시간 — 시작이 버퍼 안에 있을 때만
        public bool IsTarget;
    }

    /// <returns>창 안에 프로세스 종료 기록이 하나도 없으면 null (ETW 소스가 없던 덤프 — 필드를 뺀다).</returns>
    public static object? Summarize(IReadOnlyList<SignalEvent> events, DateTimeOffset markTime, string? target = null)
    {
        string wanted = StripExe(target ?? "");
        var starts = new Dictionary<int, DateTimeOffset>();           // pid → 마지막 시작 시각 (PID 재사용 대비: 종료를 보면 지운다)
        var groups = new Dictionary<(string, uint), Group>();
        int stops = 0, nonzero = 0;

        foreach (var e in events)
        {
            if (e.Source != "etw-kernel") continue;
            try
            {
                using var doc = JsonDocument.Parse(e.PayloadJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ev", out var evp)) continue;
                string? ev = evp.GetString();
                if (ev == "processStart")
                {
                    starts[root.GetProperty("pid").GetInt32()] = e.Timestamp;
                    continue;
                }
                if (ev != "processStop") continue;

                stops++;
                int pid = root.GetProperty("pid").GetInt32();
                string name = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "?" : "?";
                // ExitStatus 는 부호 있는 32비트로 기록된다: 0xC0000005 는 -1073741819 로 들어 있다
                uint code = root.TryGetProperty("exit", out var ex) && ex.ValueKind == JsonValueKind.Number ? unchecked((uint)ex.GetInt64()) : 0;
                double? lived = starts.Remove(pid, out var startedAt) && startedAt <= e.Timestamp
                    ? Math.Round((e.Timestamp - startedAt).TotalSeconds, 1)
                    : null;

                bool isTarget = wanted.Length > 0 && string.Equals(StripExe(name), wanted, StringComparison.OrdinalIgnoreCase);
                if (code != 0) nonzero++;
                if (code == 0 && !isTarget) continue;

                var key = (name.ToLowerInvariant(), code);
                if (!groups.TryGetValue(key, out var g))
                    groups[key] = g = new Group { Name = name, Code = code, IsTarget = isTarget };
                g.Count++;
                if (e.Timestamp >= g.LastAt) { g.LastAt = e.Timestamp; g.LivedSec = lived; }
            }
            catch { /* 형식이 다른 이벤트는 건너뛴다 */ }
        }

        if (stops == 0) return null;

        // 마킹 대상 먼저, 그다음 마킹에 가까운 순
        var ordered = groups.Values.OrderByDescending(g => g.IsTarget).ThenByDescending(g => g.LastAt).ToList();
        var rows = new List<Dictionary<string, object>>();
        foreach (var g in ordered.Take(MaxRows))
        {
            var row = new Dictionary<string, object>
            {
                ["name"] = g.Name,
                ["exit_code"] = g.Code,
                ["exit_hex"] = $"0x{g.Code:X8}",
                ["count"] = g.Count,
                ["last_at"] = g.LastAt,
                ["sec_before_mark"] = Math.Round((markTime - g.LastAt).TotalSeconds, 1),   // 음수 = 마킹 뒤에 끝났다
            };
            if (g.LivedSec.HasValue) row["lived_sec"] = g.LivedSec.Value;
            if (g.IsTarget) row["is_target"] = true;
            rows.Add(row);
        }

        var result = new Dictionary<string, object>
        {
            ["window_stops"] = stops,        // 창 안에서 끝난 프로세스 전체
            ["nonzero_stops"] = nonzero,     // 그중 종료 코드가 0 이 아닌 것
            ["rows"] = rows,
        };
        if (ordered.Count > MaxRows) result["rows_omitted"] = ordered.Count - MaxRows;
        return result;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
