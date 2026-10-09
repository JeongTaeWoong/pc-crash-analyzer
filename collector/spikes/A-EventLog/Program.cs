// 스파이크 A: EvtSubscribe(EventLogWatcher) 실시간 구독 + 최근 7일 조회
// Application 1000(앱 크래시)/1001(WER), System 41(Kernel-Power)/4101(TDR)/WHEA 17~20
using System.Diagnostics.Eventing.Reader;
using System.Text.Encodings.Web;
using System.Text.Json;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var result = new Dictionary<string, object?> { ["signal"] = "A-EventLog", ["ranAt"] = DateTime.Now.ToString("s") };
string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

const string AppCond = "EventID=1000 or EventID=1001";
const string SysCond = "(EventID=41 or EventID=4101) or (Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID>=17 and EventID<=20))";

// 1) 실시간 구독이 열리는지 (열자마자 닫는다)
var subs = new Dictionary<string, string>();
foreach (var (log, cond) in new[] { ("Application", AppCond), ("System", SysCond) })
{
    try
    {
        using var watcher = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, $"*[System[{cond}]]"));
        watcher.Enabled = true;
        Thread.Sleep(300);
        watcher.Enabled = false;
        subs[log] = "ok";
    }
    catch (Exception ex) { subs[log] = $"unavailable: {ex.GetType().Name}: {ex.Message}"; }
}
result["subscribe"] = subs;

// 2) 최근 7일 발생 이력 (timediff 단위: ms)
const string Within7d = "TimeCreated[timediff(@SystemTime) <= 604800000]";
var counts = new Dictionary<string, int>();
var samples = new Dictionary<string, object>();

void Scan(string log, string cond)
{
    var query = new EventLogQuery(log, PathType.LogName, $"*[System[({cond}) and {Within7d}]]") { ReverseDirection = true };
    using var reader = new EventLogReader(query);
    for (EventRecord? ev = reader.ReadEvent(); ev != null; ev = reader.ReadEvent())
    {
        using (ev)
        {
            string key = $"{log}/{ev.ProviderName}/{ev.Id}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (!samples.ContainsKey(log)) // 로그당 가장 최근 1건만 샘플로
            {
                string msg = "";
                try { msg = ev.FormatDescription() ?? ""; } catch { }
                msg = msg.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase).Replace("\r\n", " ").Replace("\n", " ");
                samples[log] = new { id = ev.Id, provider = ev.ProviderName, time = ev.TimeCreated?.ToString("s"), message = msg.Length > 250 ? msg[..250] + "..." : msg };
            }
        }
    }
}

try
{
    Scan("Application", AppCond);
    Scan("System", SysCond);
    result["last7days"] = counts;
    result["recentSamplePerLog"] = samples;
    result["status"] = "ok";
}
catch (Exception ex)
{
    result["status"] = "unavailable";
    result["reason"] = $"{ex.GetType().Name}: {ex.Message}";
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
