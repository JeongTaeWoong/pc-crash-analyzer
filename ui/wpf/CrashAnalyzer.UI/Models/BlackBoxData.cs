using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrashAnalyzer.UI.Models;

// 블랙박스 기록 페이지용 데이터 (2026-11-02 시안 2-4).
// 수집기 덤프 폴더의 events.json · meta.json 을 읽어 타임라인 줄별 기록으로 바꾼다.
// 실험용 시나리오(engine/data/samples)처럼 원본 기록이 없으면 엔진 스냅샷의 events · change_history 로 대신한다.
// 판단은 하지 않는다. 시각과 종류를 정리해서 보여 주기만 한다.

/// <summary>타임라인·목록의 기록 한 줄.</summary>
public sealed class BlackBoxItem
{
    public DateTime Time { get; init; }
    public string Lane { get; init; } = "event";   // event / proc / module
    public string Kind { get; init; } = "";        // 목록의 '종류' 칸
    public string Text { get; init; } = "";        // 목록의 '내용' 칸
    public string Detail { get; init; } = "";      // '선택한 기록' 칸
    public bool IsModule { get; init; }

    // 목록(ListView) 표시용
    public string TimeText { get; set; } = "";
    public string ShortText => Ui.Short(Text, 90);
}

public sealed class BlackBoxData
{
    public string Source { get; private set; } = "none";   // none / collector / snapshot
    public List<BlackBoxItem> Items { get; } = new();
    public List<(DateTime T, double V)> GpuLoad { get; } = new();
    public List<(DateTime T, double V)> GpuTemp { get; } = new();
    public DateTime? Mark { get; private set; }
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }
    public string MetaKind { get; private set; } = "";
    public bool ViaHotkey { get; private set; }

    /// <summary>타임라인 줄 (키, 왼쪽 이름)</summary>
    public static readonly (string Key, string Name)[] Lanes =
    {
        ("event", "오류·종료 이벤트"),
        ("proc", "프로그램 시작·종료"),
        ("module", "모듈(DLL) 로드"),
        ("gpu_load", "GPU 사용률"),
        ("gpu_temp", "GPU 온도"),
    };

    /// <summary>
    /// 기록 시각 문자열 → 기록한 곳의 현지 시각(시간대 없음). 못 읽으면 null.
    /// 수집기는 소수점 아래를 7자리까지 쓴다. 시간대 표시(+09:00, Z)는 떼고 적힌 시각 그대로 쓴다.
    /// </summary>
    public static DateTime? ParseTime(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 19) return null;
        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dto))
            return dto.DateTime;   // 적힌 시각 그대로 (시간대 변환 안 함)
        return null;
    }

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            string text = File.ReadAllText(path);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            return JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string S(JsonNode? n)
    {
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString();
        }
        return n.ToJsonString();
    }

    /// <param name="sourcePath">사용자가 연 원본 파일 (수집기 덤프면 같은 폴더에 events.json · meta.json 이 있다)</param>
    /// <param name="snapshotPath">엔진에 넘긴 스냅샷 (증상을 채운 사본일 수 있다)</param>
    public static BlackBoxData Load(string? sourcePath, string? snapshotPath)
    {
        var bb = new BlackBoxData();
        if (string.IsNullOrEmpty(sourcePath)) return bb;

        string folder = Path.GetDirectoryName(sourcePath) ?? "";
        string evPath = Path.Combine(folder, "events.json");
        string metaPath = Path.Combine(folder, "meta.json");
        if (File.Exists(evPath))
        {
            var raw = ReadJson(evPath);
            if (raw != null)
            {
                var meta = File.Exists(metaPath) ? ReadJson(metaPath) : null;
                bb.Source = "collector";
                bb.MetaKind = S(meta?["kind"]);
                bb.ViaHotkey = S(meta?["trigger"]?["via"]) == "hotkey";
                bb.Mark = ParseTime(S(raw["markTime"]).Length > 0 ? S(raw["markTime"]) : S(meta?["markTime"]));
                bb.From = ParseTime(S(raw["from"]));
                bb.To = ParseTime(S(raw["to"]));
                if (raw["events"] is JsonArray evs)
                    for (int i = 0; i < evs.Count; i++)
                        bb.AddCollectorEvent(evs[i]);
                return bb;
            }
        }

        // 실험용 시나리오: 엔진 스냅샷만 있음
        if (string.IsNullOrEmpty(snapshotPath)) return bb;
        var snap = ReadJson(snapshotPath);
        if (snap == null) return bb;
        bb.Source = "snapshot";
        bb.Mark = ParseTime(S(snap["marked_at"]));
        if (snap["events"] is JsonArray sev)
        {
            for (int i = 0; i < sev.Count; i++)
            {
                var e = sev[i];
                var t = ParseTime(S(e?["time"]));
                if (t is null) continue;
                string text = S(e?["id"]) + " " + S(e?["provider"]);
                if (S(e?["subject"]).Length > 0) text += " · " + S(e?["subject"]);
                if (S(e?["faulting_module"]).Length > 0) text += " · 오류 모듈 " + S(e?["faulting_module"]);
                bb.Items.Add(new BlackBoxItem
                {
                    Time = t.Value, Lane = "event", Kind = "이벤트 " + S(e?["id"]), Text = text,
                    Detail = e?.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }) ?? "",
                });
            }
        }
        if (snap["change_history"] is JsonArray chs)
        {
            for (int i = 0; i < chs.Count; i++)
            {
                var ch = chs[i];
                var t = ParseTime(S(ch?["time"]));
                if (t is null) continue;
                bb.Items.Add(new BlackBoxItem
                {
                    Time = t.Value, Lane = "proc", Kind = "설치·변경",
                    Text = (S(ch?["subject"]) + " " + S(ch?["version"])).Trim(),
                    Detail = ch?.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }) ?? "",
                });
            }
        }
        if (bb.Items.Count > 0)
        {
            DateTime min = bb.Items[0].Time, max = bb.Items[0].Time;
            for (int i = 1; i < bb.Items.Count; i++)
            {
                if (bb.Items[i].Time < min) min = bb.Items[i].Time;
                if (bb.Items[i].Time > max) max = bb.Items[i].Time;
            }
            if (bb.Mark is { } m)
            {
                if (m < min) min = m;
                if (m > max) max = m;
            }
            bb.From = min;
            bb.To = max;
        }
        return bb;
    }

    private void AddCollectorEvent(JsonNode? x)
    {
        var t = ParseTime(S(x?["t"]));
        if (t is null) return;
        string src = S(x?["source"]);
        var p = x?["payload"];

        if (src == "sensors")
        {
            if (p?["sensors"] is JsonArray sensors)
                for (int k = 0; k < sensors.Count; k++)
                {
                    var s = sensors[k];
                    if (S(s?["name"]) != "GPU Core") continue;
                    double v = 0;
                    try { v = s?["value"]?.GetValue<double>() ?? 0; } catch (Exception) { continue; }
                    if (S(s?["type"]) == "Load") GpuLoad.Add((t.Value, v));
                    else if (S(s?["type"]) == "Temperature") GpuTemp.Add((t.Value, v));
                }
            return;
        }
        if (src == "processes") return; // 10초마다 찍는 프로세스 목록은 넣지 않는다 (문제 순간 상태는 오른쪽 카드에서)

        if (src == "eventlog")
        {
            string msg = string.Join(" ", S(p?["message"]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            string text = S(p?["id"]) + " " + S(p?["provider"]);
            if (S(p?["subject"]).Length > 0) text += " · " + S(p?["subject"]);
            if (S(p?["faultingModule"]).Length > 0) text += " · 오류 모듈 " + S(p?["faultingModule"]);
            Items.Add(new BlackBoxItem { Time = t.Value, Lane = "event", Kind = "이벤트 " + S(p?["id"]), Text = text, Detail = msg });
            return;
        }
        if (src == "etw-kernel")
        {
            string ev = S(p?["ev"]);
            if (ev == "imageLoad")
            {
                string path = S(p?["path"]);
                Items.Add(new BlackBoxItem
                {
                    Time = t.Value, Lane = "module", Kind = "모듈 로드", IsModule = true,
                    Text = S(p?["proc"]) + " ← " + Path.GetFileName(path.Replace('\\', '/')),
                    Detail = path,
                });
            }
            else if (ev == "processStart" || ev == "processStop")
            {
                string word = ev == "processStart" ? "시작" : "종료";
                string text = S(p?["name"]) + " " + word;
                string exit = S(p?["exit"]);
                if (ev == "processStop" && exit.Length > 0 && exit != "0" && exit != "null")
                    text += " (종료 코드 " + exit + ")";
                string cmd = S(p?["cmd"]);
                Items.Add(new BlackBoxItem
                {
                    Time = t.Value, Lane = "proc", Kind = "프로그램 " + word, Text = text,
                    Detail = cmd.Length > 0 ? cmd : text,
                });
            }
        }
    }

    /// <summary>
    /// 문제 순간 스냅샷에 나온 드라이버 · 모듈 · 실시간 백신 이름들 (소문자).
    /// "문제 순간에 실행 중이던 후보" 카드가 엔진 후보 이름과 비교할 때 쓴다.
    /// </summary>
    public static List<string> RunningNames(string? snapshotPath)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(snapshotPath)) return names;
        var snap = ReadJson(snapshotPath);
        if (snap == null) return names;
        if (snap["drivers"] is JsonArray drv)
            for (int i = 0; i < drv.Count; i++)
                names.Add((S(drv[i]?["name"]) + " " + S(drv[i]?["display"])).ToLowerInvariant());
        if (snap["modules"] is JsonArray mods)
            for (int i = 0; i < mods.Count; i++)
                names.Add(S(mods[i]?["name"]).ToLowerInvariant());
        if (snap["security"]?["realtime_av"] is JsonArray av)
            for (int i = 0; i < av.Count; i++)
                names.Add(S(av[i]).ToLowerInvariant());
        return names;
    }
}
