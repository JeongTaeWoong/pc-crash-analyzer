using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CrashAnalyzer.Ai;

/// <summary>
/// 엔진 스냅샷(engine-snapshot.json)을 번호 붙은 증거 항목의 글로 편다 — AI 에 보내는 것은 이 글뿐이다.
/// 평가 실험의 묶음(eval/cases.py build_bundle, 판 b3-2026-11-15)을 그대로 옮긴 것이다. 평가에서 잰 방식(A3: 요약 묶음,
/// 엔진 출력 없음)과 제품이 보내는 입력이 같아야 평가 숫자가 제품에 대한 숫자가 된다. 같은 스냅샷에서 Python 과 글자까지
/// 같은지는 시험(CrashAnalyzer.Ai.Tests, golden)이 본다. 여기서는 판단하지 않는다 — 고르고 줄 세우는 것뿐이다.
/// </summary>
public static class EvidenceBundle
{
    public const string Version = "b3-2026-11-15";

    public sealed record Result(string Text, IReadOnlySet<string> Ids);

    public static string UserMessage(string bundleText) => "이 PC에서 수집해 정리한 자료입니다. 원인을 찾아 주세요.\n\n" + bundleText;

    public static Result Build(JsonElement s)
    {
        var lines = new List<string>();
        var ids = new HashSet<string>();
        void Head(string title) => lines.Add("\n## " + title);
        void Add(string id, string text) { ids.Add(id); lines.Add($"[{id}] {text}"); }

        var sym = Obj(s, "symptom");
        Head("사건");
        Add("sym", Compact(w =>
        {
            w.WriteStartObject();
            WriteProp(w, "marked_at", Get(s, "marked_at"));
            WriteProp(w, "trigger", Get(s, "trigger"));
            WriteProp(w, "symptom", Get(sym, "type"));
            WriteProp(w, "scope", Get(sym, "scope"));
            var target = Get(sym, "target_process");
            WriteProp(w, "target_process", Truthy(target) ? target : null);
            var fm = Get(sym, "faulting_modules");
            w.WritePropertyName("faulting_modules");
            if (Truthy(fm)) fm!.Value.WriteTo(w); else { w.WriteStartArray(); w.WriteEndArray(); }
            w.WriteEndObject();
        }));
        Add("host", Compact(w => { if (Get(s, "host") is { } h) h.WriteTo(w); else { w.WriteStartObject(); w.WriteEndObject(); } }));

        // 같은 덤프에 병합된 다른 자동 트리거 (수집기 필드 merged_triggers). 위의 증상은 첫 트리거의 것이다.
        int n = 0;
        foreach (var m in Arr(s, "merged_triggers"))
            Add($"mrg{++n}", Compact(w => { w.WriteStartObject(); w.WriteBoolean("merged_into_this_dump", true); CopyProps(w, m); w.WriteEndObject(); }));
        if (Get(s, "earlier_event_in_dump") is { } prev && Truthy(prev))
            Add("prev", Compact(w => { w.WriteStartObject(); w.WriteBoolean("earlier_event_in_this_dump", true); CopyProps(w, prev); w.WriteEndObject(); }));

        if (Arr(s, "events").Any())
        {
            Head("이벤트 로그 (Windows 이벤트, 덤프 창 안)");
            n = 0;
            foreach (var e in Arr(s, "events")) Add($"ev{++n}", Raw(e));
        }

        if (Get(s, "process_exits") is { } px && Truthy(px))
        {
            Head("덤프 구간에서 끝난 프로세스 (종료 코드가 0 이 아닌 것, 이름·코드별 묶음)");
            lines.Add("읽는 법: window_stops = 구간에서 끝난 프로세스 전체 수, nonzero_stops = 그중 종료 코드가 0 이 아닌 수. "
                      + "행 하나 = 같은 이름·같은 코드의 종료 묶음(count = 횟수, last_at = 마지막 종료가 기록된 시각, "
                      + "sec_before_mark = 마킹 몇 초 전인지 — 기록이 1초쯤 늦게 도착하므로 0 근처 음수는 마킹 직전을 뜻한다, "
                      + "lived_sec = 마지막으로 끝난 것이 켜져 있던 시간, 없으면 구간 이전에 켜졌다). is_target = 사용자가 마킹할 때 지목한 프로그램. "
                      + "0 이 아닌 코드가 곧 오류는 아니다: 평소에도 0 이 아닌 코드로 끝나는 프로그램이 있다.");
            Add("exits", Compact(w => WriteNonLists(w, px)));
            n = 0;
            foreach (var row in Arr(px, "rows")) Add($"exit{++n}", Raw(row));
        }

        if (Get(s, "resource_recent") is { } rr && Truthy(rr))
        {
            Head("마킹 직전 구간의 자원 사용 (10초 표본, CPU 는 30초마다 측정)");
            lines.Add("읽는 법: measurements = 그 프로세스가 상위 목록에 잡힌 측정 횟수(전체 측정 횟수는 cpu_measurements). "
                      + "cpu_avg·io_mbps_avg = 구간 전체 평균(잡히지 않은 측정은 0 으로 셈 — 구간 끝에 켜진 프로그램은 낮게 나온다). "
                      + "cpu_avg_active·io_mbps_avg_active = 잡힌 측정만의 평균. cpu_last·io_mbps_last = 마지막 측정(마킹 무렵)의 값, null 이면 그때 상위 목록에 없었다. "
                      + "busy_ge90_samples = 디스크가 90% 이상 바빴던 표본 수(표본 1개 = 10초). busy_pct_recent·latency_ms_recent·mbps_recent = 마지막 표본 6개(약 1분, 마킹 무렵)의 평균.");
            Add("res", Compact(w => WriteNonLists(w, rr)));
            foreach (var (key, prefix) in new[] { ("top_cpu", "cpu"), ("top_io", "io"), ("top_memory", "mem"), ("top_memory_growth", "grow"), ("disks", "disk") })
            {
                n = 0;
                foreach (var row in Arr(rr, key))
                    Add($"{prefix}{++n}", Compact(w => { w.WriteStartObject(); w.WriteString("list", key); CopyProps(w, row); w.WriteEndObject(); }));
            }
        }

        if (Get(s, "resource_trends") is { } rt && Truthy(rt))
        {
            Head("최근 60분 추세 (1분 표본, 프로세스가 10분 이상 있어야 행이 생긴다)");
            Add("trend", Compact(w =>
            {
                w.WriteStartObject();
                foreach (var p in rt.EnumerateObject())
                {
                    // Python: v not in (None, [], {})
                    bool empty = p.Value.ValueKind == JsonValueKind.Null
                                 || (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0)
                                 || (p.Value.ValueKind == JsonValueKind.Object && !p.Value.EnumerateObject().Any());
                    if (!empty) p.WriteTo(w);
                }
                w.WriteEndObject();
            }));
        }

        if (Arr(s, "modules").Any())
        {
            Head("대상 프로세스에 올라와 있던 비시스템 모듈");
            n = 0;
            foreach (var m in Arr(s, "modules").Take(60)) Add($"mod{++n}", Raw(m));
        }

        if (Arr(s, "drivers").Any())
        {
            Head("표준 경로 밖 커널 드라이버·파일 시스템 필터");
            n = 0;
            foreach (var d in Arr(s, "drivers")) Add($"drv{++n}", Raw(d));
        }
        if (Get(s, "device_filters") is { } df && Truthy(df)) Add("filt", Raw(df));
        if (Get(s, "security") is { } sec && Truthy(sec))
        {
            Head("보안 제품");
            Add("sec", Raw(sec));
        }

        if (Arr(s, "change_history").Any())
        {
            Head("최근 설치·변경 (날짜만 알 수 있다)");
            n = 0;
            foreach (var ch in Arr(s, "change_history")) Add($"chg{++n}", Raw(ch));
        }

        if (Arr(s, "processes").Any())
        {
            Head("마킹 시점에 실행 중이던 프로세스 (이름×개수)");
            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in Arr(s, "processes"))
            {
                string name = p.GetString() ?? "";
                if (!counts.ContainsKey(name)) { counts[name] = 0; order.Add(name); }
                counts[name]++;
            }
            // Python: sorted(counts.items(), key=lambda x: x[0].lower()) - 같은 키는 처음 나온 순서 그대로
            var sorted = order.OrderBy(x => x.ToLowerInvariant(), StringComparer.Ordinal);
            Add("proc", string.Join(", ", sorted.Select(name => counts[name] > 1 ? $"{name}×{counts[name]}" : name)));
        }

        return new Result(string.Join("\n", lines).Trim(), ids);
    }

    // ---- JSON 을 Python 의 json.dumps(ensure_ascii=False, separators=(",", ":")) 와 같은 모양으로 ----
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Compact(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) write(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Raw(JsonElement e) => Compact(e.WriteTo);

    private static JsonElement? Get(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    private static JsonElement? Obj(JsonElement s, string name) => Get(s, name) is { ValueKind: JsonValueKind.Object } o ? o : null;

    private static IEnumerable<JsonElement> Arr(JsonElement? obj, string name) =>
        Get(obj, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>Python 의 참·거짓: 없음, null, 빈 문자열·배열·객체, 0, false 는 거짓.</summary>
    private static bool Truthy(JsonElement? e) => e is { } v && v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => v.GetString() is { Length: > 0 },
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
        _ => true,
    };

    private static void WriteProp(Utf8JsonWriter w, string name, JsonElement? value)
    {
        w.WritePropertyName(name);
        if (value is { } v) v.WriteTo(w); else w.WriteNullValue();
    }

    private static void CopyProps(Utf8JsonWriter w, JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        foreach (var p in obj.EnumerateObject()) p.WriteTo(w);
    }

    /// <summary>Python: {k: v for k, v in d.items() if not isinstance(v, list)}</summary>
    private static void WriteNonLists(Utf8JsonWriter w, JsonElement obj)
    {
        w.WriteStartObject();
        foreach (var p in obj.EnumerateObject())
            if (p.Value.ValueKind != JsonValueKind.Array) p.WriteTo(w);
        w.WriteEndObject();
    }
}
