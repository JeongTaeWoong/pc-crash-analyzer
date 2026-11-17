using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrashAnalyzer.UI.Services.Executor;

// 실행기 기록(journal) 모델 — 2026-11-16 추가
//
// 조치를 실행하기 전에 "원래 상태"를 먼저 파일에 적고, 실행·되돌리기의 모든 단계를 같은 파일에 남긴다.
// 실행 도중 PC 가 꺼져도 다음에 UI 를 켤 때 이 파일을 보고 원래대로 되돌릴 수 있다.
// (중간발표 10번 슬라이드: 실행 전 원복 지점 기록 · 악화되면 자동 원복 · 모든 조치 로그)

/// <summary>조치 기록 하나의 상태</summary>
public static class EntryState
{
    public const string Running = "running";         // 실행 중 (여기서 꺼지면 다음 실행 때 되돌린다)
    public const string Verifying = "verifying";     // 실행 끝, 결과 확인 중 (새 오류가 나면 되돌린다)
    public const string Kept = "kept";               // 효과가 있어 그대로 둠
    public const string RolledBack = "rolled_back";  // 원래대로 되돌림
    public const string Failed = "failed";           // 실행 실패 (바꾼 것은 되돌림)
    public const string Refused = "refused";         // 실행하지 않음 (지원 안 함 / 권한 없음 / Lv3 이상)
}

/// <summary>실제로 바꾼 것 하나 (되돌리기 단위)</summary>
public sealed class ExecStep
{
    public string Kind { get; set; } = "";       // process_stop / service_stop / service_start_type / firewall_rule / defender_exclusion / defender_realtime
    public string Target { get; set; } = "";     // 서비스 이름, 프로세스 이름, 규칙 이름 등
    public string Before { get; set; } = "";     // 바꾸기 전 상태 (되돌릴 때 쓴다)
    public string After { get; set; } = "";      // 바꾼 뒤 상태
    public string Detail { get; set; } = "";     // 되돌릴 때 필요한 추가 정보 (프로그램 경로 등)
    public bool Applied { get; set; }            // 실제로 바꿨는가
    public bool RolledBack { get; set; }
}

/// <summary>화면·타임라인에 보이는 진행 기록 한 줄</summary>
public sealed class ExecEvent
{
    public DateTime Time { get; set; }
    public string Kind { get; set; } = "info";   // info / record / apply / rollback / error / done
    public string Text { get; set; } = "";
}

/// <summary>조치 기록 하나 (점검 1개의 조치, 또는 해결책 1개의 조치들)</summary>
public sealed class ExecEntry
{
    public string Id { get; set; } = "";
    public string SessionPath { get; set; } = "";
    public string Context { get; set; } = "";    // "check:<check_id>" 또는 "remedy:<remedy_id>"
    public string Label { get; set; } = "";
    public int Level { get; set; }               // 위험도 Lv (1 자동 · 2 원클릭 동의 · 3 명시적 승인 · 4 직접)
    public bool DryRun { get; set; }             // 시험 실행 (실제로 바꾸지 않음)
    public string State { get; set; } = EntryState.Running;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime? AutoRollbackAt { get; set; } // 시간이 정해진 조치(실시간 보호 10분 일시 중지 등)를 되돌릴 시각
    public List<string> Automations { get; set; } = new();
    public List<ExecStep> Steps { get; set; } = new();
    public List<ExecEvent> Events { get; set; } = new();

    [JsonIgnore] public bool IsActive => State == EntryState.Running || State == EntryState.Verifying;
}

/// <summary>기록 파일 읽기·쓰기. 위치: %LOCALAPPDATA%\PcCrashAnalyzer\executor\journal.json (CE_EXEC_JOURNAL 로 바꿀 수 있음)</summary>
public sealed class ExecJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Path { get; }
    public List<ExecEntry> Entries { get; private set; } = new();

    public ExecJournal(string? path = null)
    {
        Path = path ?? DefaultPath();
        Load();
    }

    public static string DefaultPath()
    {
        string? env = Environment.GetEnvironmentVariable("CE_EXEC_JOURNAL");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCrashAnalyzer", "executor");
        return System.IO.Path.Combine(dir, "journal.json");
    }

    private void Load()
    {
        try
        {
            if (File.Exists(Path))
                Entries = JsonSerializer.Deserialize<List<ExecEntry>>(File.ReadAllText(Path), Options) ?? new();
        }
        catch (Exception)
        {
            // 망가진 기록 파일은 옆에 남겨 두고 새로 시작한다 (되돌릴 정보를 지우지 않기 위해 덮어쓰지 않음)
            try { File.Copy(Path, Path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch (Exception) { }
            Entries = new();
        }
    }

    /// <summary>바꾸기 전·후마다 부른다. 임시 파일에 쓴 뒤 바꿔 끼워서, 쓰는 도중 꺼져도 파일이 깨지지 않게 한다.</summary>
    public void Save()
    {
        // 오래된 기록은 50개까지만 남긴다 (진행 중인 것은 지우지 않음)
        while (Entries.Count > 50)
        {
            int drop = -1;
            for (int i = 0; i < Entries.Count; i++)
                if (!Entries[i].IsActive) { drop = i; break; }
            if (drop < 0) break;
            Entries.RemoveAt(drop);
        }
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Entries, Options));
        File.Move(tmp, Path, true);
    }
}

/// <summary>엔진의 실행 방식(execution) → 중간발표의 위험도 단계 Lv</summary>
public static class RiskLevel
{
    /// <summary>
    /// auto → Lv1(자동 실행) · confirm + 되돌릴 수 있음 → Lv2(원클릭 동의) · confirm + 되돌릴 수 없음 → Lv3(명시적 승인)
    /// · manual → Lv4(직접 수행) · guide(안내 모드) → 0
    /// </summary>
    public static int FromAction(string execution, bool reversible) => execution switch
    {
        "auto" => 1,
        "confirm" => reversible ? 2 : 3,
        "manual" => 4,
        _ => 0,
    };

    public static string Name(int level) => level switch
    {
        1 => "Lv1 · 자동 실행",
        2 => "Lv2 · 동의 후 실행",
        3 => "Lv3 · 명시적 승인",
        4 => "Lv4 · 직접 작업",
        _ => "안내",
    };
}
