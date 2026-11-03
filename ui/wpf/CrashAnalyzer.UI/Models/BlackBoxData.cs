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
    public string ActionKind { get; init; } = "";  // 조치 줄: apply / rollback / error (2026-11-02)

    // 목록(ListView) 표시용
    public string TimeText { get; set; } = "";
    public string ShortText => Ui.Short(Text, 90);

    // 2026-11-02 확정안 2-4-C 표: 출처 · 판단 칸, 줄 색, 연결된 엔진 후보
    public string Source { get; set; } = "";
    public string Verdict { get; set; } = "";
    public System.Windows.Media.Brush VerdictFg { get; set; } = Ui.DimFg;
    public System.Windows.Media.Brush RowBg { get; set; } = System.Windows.Media.Brushes.Transparent;
    public string CandId { get; set; } = "";       // 이 줄과 관계있는 엔진 후보 id (없으면 빈 글자)
    public string Title { get; set; } = "";        // '선택한 항목' 카드 제목
}

public sealed class BlackBoxData
{
    public string Source { get; private set; } = "none";   // none / collector / snapshot
    public List<BlackBoxItem> Items { get; } = new();
    public List<(DateTime T, double V)> GpuLoad { get; } = new();
    public List<(DateTime T, double V)> GpuTemp { get; } = new();
    public List<(DateTime T, double V)> CpuTemp { get; } = new();   // 2026-11-02 추가
    public string TargetProcess { get; private set; } = "";         // 증상 대상 프로그램 (예: othergame.exe)
    public DateTime? Mark { get; private set; }
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }
    public string MetaKind { get; private set; } = "";
    public bool ViaHotkey { get; private set; }

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
                if (!string.IsNullOrEmpty(snapshotPath))
                    bb.TargetProcess = S(ReadJson(snapshotPath)?["symptom"]?["target_process"]);
                return bb;
            }
        }

        // 실험용 시나리오: 엔진 스냅샷만 있음
        if (string.IsNullOrEmpty(snapshotPath)) return bb;
        var snap = ReadJson(snapshotPath);
        if (snap == null) return bb;
        bb.Source = "snapshot";
        bb.Mark = ParseTime(S(snap["marked_at"]));
        bb.TargetProcess = S(snap["symptom"]?["target_process"]);
        // 증상이 시작된 시각 = 대상 프로그램을 켠 시각으로 본다 (스냅샷에 프로세스 시작 시각이 따로 없음)
        var started = ParseTime(S(snap["symptom"]?["started_at"]));
        if (started != null && bb.TargetProcess.Length > 0)
            bb.Items.Add(new BlackBoxItem
            {
                Time = started.Value, Lane = "proc", Kind = "프로그램 시작", Text = bb.TargetProcess + " 시작",
                Detail = "증상이 시작된 시각(symptom.started_at)", Title = bb.TargetProcess,
            });
        if (snap["modules"] is JsonArray smods)
            for (int i = 0; i < smods.Count; i++)
            {
                var t = ParseTime(S(smods[i]?["loaded_at"]));
                if (t is null) continue;
                string name = S(smods[i]?["name"]);
                bb.Items.Add(new BlackBoxItem
                {
                    Time = t.Value, Lane = "module", Kind = "모듈 로드", IsModule = true,
                    Text = S(smods[i]?["process"]) + " ← " + name,
                    Detail = name + " (" + S(smods[i]?["company"]) + ")", Title = name,
                });
            }
        if (snap["sensors"]?["samples"] is JsonArray samples)
            for (int i = 0; i < samples.Count; i++)
            {
                var t = ParseTime(S(samples[i]?["time"]));
                if (t is null) continue;
                if (samples[i]?["gpu_temp"] is JsonValue gv && gv.TryGetValue<double>(out double g)) bb.GpuTemp.Add((t.Value, g));
                if (samples[i]?["cpu_temp"] is JsonValue cv && cv.TryGetValue<double>(out double c)) bb.CpuTemp.Add((t.Value, c));
            }
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
            // 타임라인은 문제 순간 앞 2시간 안의 기록부터 (며칠 전 설치 기록은 목록에만 남긴다)
            if (bb.Mark is { } mk2 && (mk2 - min).TotalHours > 2)
            {
                DateTime limit = mk2.AddHours(-2);
                DateTime first = mk2;
                for (int i = 0; i < bb.Items.Count; i++)
                    if (bb.Items[i].Time >= limit && bb.Items[i].Time < first) first = bb.Items[i].Time;
                min = first;
            }
            bb.From = min.AddSeconds(-Math.Max(30, (max - min).TotalSeconds * 0.03));
            bb.To = max.AddSeconds(30);
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
                    string sname = S(s?["name"]);
                    double v = 0;
                    try { v = s?["value"]?.GetValue<double>() ?? 0; } catch (Exception) { continue; }
                    if (sname == "GPU Core")
                    {
                        if (S(s?["type"]) == "Load") GpuLoad.Add((t.Value, v));
                        else if (S(s?["type"]) == "Temperature") GpuTemp.Add((t.Value, v));
                    }
                    // CPU 온도: 인텔 'CPU Package', AMD 'Core (Tctl/Tdie)' (2026-11-02 추가)
                    else if (S(s?["type"]) == "Temperature" && (sname == "CPU Package" || sname.StartsWith("Core (Tctl", StringComparison.Ordinal)))
                        CpuTemp.Add((t.Value, v));
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
    /// 실행기 기록(조치 실행·되돌림)을 덧붙인 사본 (2026-11-02 추가). 조치는 기록 뒤에 일어나므로 시간 범위를 늘린다.
    /// </summary>
    public BlackBoxData WithExtra(List<BlackBoxItem> extra)
    {
        var copy = new BlackBoxData
        {
            Source = Source, Mark = Mark, From = From, To = To, MetaKind = MetaKind, ViaHotkey = ViaHotkey,
            TargetProcess = TargetProcess,
        };
        copy.CpuTemp.AddRange(CpuTemp);
        copy.Items.AddRange(Items);
        copy.GpuLoad.AddRange(GpuLoad);
        copy.GpuTemp.AddRange(GpuTemp);
        for (int i = 0; i < extra.Count; i++)
        {
            copy.Items.Add(extra[i]);
            if (copy.From is null || extra[i].Time < copy.From) copy.From = extra[i].Time;
            if (copy.To is null || extra[i].Time > copy.To) copy.To = extra[i].Time.AddSeconds(5);
        }
        return copy;
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

    /// <summary>스냅샷의 드라이버 (파일 이름, 표시 이름). 타임라인 드라이버 줄에 쓴다 (2026-11-02).</summary>
    public static List<(string Name, string Display)> Drivers(string? snapshotPath)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(snapshotPath)) return list;
        var snap = ReadJson(snapshotPath);
        if (snap?["drivers"] is JsonArray drv)
            for (int i = 0; i < drv.Count; i++)
                list.Add((S(drv[i]?["name"]), S(drv[i]?["display"])));
        return list;
    }
}
