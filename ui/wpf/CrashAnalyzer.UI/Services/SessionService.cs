using System.IO;
using System.Text.Json.Nodes;

namespace CrashAnalyzer.UI.Services;

/// <summary>세션(확인→해결→검증) 도구 호출 래퍼. 상태는 엔진이 session_path 파일에 저장한다.</summary>
public sealed class SessionService
{
    private readonly EngineClient _engine;

    public string? SessionPath { get; private set; }
    public string? SnapshotPath { get; private set; }

    public SessionService(EngineClient engine) => _engine = engine;

    public JsonNode Start(string snapshotPath, string mode = "guide")
    {
        SnapshotPath = snapshotPath;
        SessionPath = Path.Combine(RepoPaths.SessionDir(), $"session_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        return _engine.CallTool("session_start", new JsonObject
        {
            ["session_path"] = SessionPath,
            ["snapshot_path"] = snapshotPath,
            ["mode"] = mode,
        });
    }

    public JsonNode Report(string checkId, string result) =>
        Call("session_report", new JsonObject { ["check_id"] = checkId, ["result"] = result });

    public JsonNode ApplyFix() => Call("session_apply_fix", new JsonObject());

    /// <summary>해결책 적용 기록 (2026-11-02 추가). remedyId 가 비면 엔진이 고른 현재 추천, appliedAt 이 비면 지금 시각.</summary>
    public JsonNode ApplyFix(string remedyId, string appliedAt)
    {
        var args = new JsonObject();
        if (remedyId.Length > 0) args["remedy_id"] = remedyId;
        if (appliedAt.Length > 0) args["applied_at"] = appliedAt;
        return Call("session_apply_fix", args);
    }

    /// <summary>후보 하나의 근거 문장 (2026-11-02 추가). explain 은 세션이 아니라 스냅샷을 받는다.</summary>
    public JsonNode Explain(string candidateId)
    {
        if (SnapshotPath is null) throw new InvalidOperationException("세션이 시작되지 않았습니다");
        var args = new JsonObject { ["snapshot_path"] = SnapshotPath };
        if (candidateId.Length > 0) args["candidate_id"] = candidateId;
        return _engine.CallTool("explain", args);
    }

    public JsonNode VerifyAfter(string afterSnapshotPath) =>
        Call("session_verify", new JsonObject { ["after_snapshot_path"] = afterSnapshotPath });

    public JsonNode VerifyAnswer(bool recurred, int observedMinutes) =>
        Call("session_verify", new JsonObject { ["recurred"] = recurred, ["observed_minutes"] = observedMinutes });

    public JsonNode Status() => Call("session_status", new JsonObject());

    private JsonNode Call(string tool, JsonObject args)
    {
        if (SessionPath is null) throw new InvalidOperationException("세션이 시작되지 않았습니다");
        args["session_path"] = SessionPath;
        return _engine.CallTool(tool, args);
    }
}
