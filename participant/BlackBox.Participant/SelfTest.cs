using BlackBox.Core;
using BlackBox.Hosting;
using BlackBox.Marking;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>
/// 팀 점검용(--selftest): 창 없이 실제 수집기로 마킹 1건 → 덤프 → 봉투 → 대기열 → 전송 → 메모 → 하트비트.
/// 수신기(Apps Script 배포 직후 또는 127.0.0.1 시험 수신기)가 제대로 받는지 확인한다. 참여자 PC 에서 쓰는 기능이 아니다.
/// 참여 ID 는 "selftest"+무작위 — 실제 참여자 폴더와 섞이지 않는다.
/// </summary>
internal static class SelfTest
{
    public static int Run(AppOptions opt, StudyConfig study, ParticipantState realState)
    {
        var started = DateTimeOffset.Now;
        var root = Path.Combine(opt.DataDir, "selftest");
        var state = new ParticipantState();
        state.Accept("selftest" + Ids.NewParticipantId(6), "selftest", autostart: false, study);
        var steps = new List<object>();
        void Step(string name, bool ok, object? detail = null)
        {
            steps.Add(new { step = name, ok, detail });
            Console.WriteLine($"[selftest] {(ok ? "OK  " : "FAIL")} {name} {(detail != null ? Json.Serialize(detail) : "")}");
        }

        Console.WriteLine($"[selftest] 수신 {Texts.EndpointHost(study)} · 키 {study.KeyId} · 참여 ID {state.ParticipantId} · 폴더 {root}");
        var host = new CollectorHost(new CollectorHostOptions
        {
            DumpRoot = Path.Combine(root, "dumps"),
            Post = TimeSpan.FromSeconds(opt.PostSeconds),
            Edition = "participant",
            StripCommandLineArgs = true,
            ReplaySinceBoot = false,
            EtwSessionName = "PcBlackBox-Kernel-SelfTest", // 트레이 앱이 같이 돌아도 세션을 빼앗지 않게
        });
        var written = new TaskCompletionSource<DumpRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Marks.DumpWritten += r => written.TrySetResult(r);

        using var transport = new HttpUploadTransport(study.EndpointUri!, $"PcBlackBox/{TrayApp.Version} (+selftest)");
        using var uploads = new UploadService(study, () => state, root, transport, () => new
        {
            kind = "heartbeat",
            app = TrayApp.Version,
            selftest = true,
            at = DateTimeOffset.Now,
            sources = host.Statuses.ToList(),
            log = OpsLog.Tail(40),
        }, TrayApp.Version);

        int code = 0;
        try
        {
            host.Start(Console.WriteLine);
            Step("collector-start", host.Statuses.Count(s => s.Status == "ok") >= 3,
                host.Statuses.Select(s => new { s.Name, s.Status }).ToList());

            // 증상 대화상자와 같은 경로: 마킹 먼저, 답은 1초 뒤
            var answer = new TaskCompletionSource<ManualMark?>();
            var markTime = host.Marks.MarkManualDeferred("selftest", null, answer.Task, TimeSpan.FromSeconds(opt.PostSeconds + 20));
            _ = Task.Delay(1000).ContinueWith(_ => answer.TrySetResult(new ManualMark("slow", "explorer", "selftest 자동 응답")));
            var done = Task.WhenAny(written.Task, Task.Delay(TimeSpan.FromSeconds(opt.PostSeconds + 90))).Result;
            if (done != written.Task) { Step("dump", false, "덤프 시간 초과"); return 1; }
            var rec = written.Task.Result;
            var files = Directory.GetFiles(rec.Dir, "*.json").Select(Path.GetFileName).ToList();
            Step("dump", files.Contains("meta.json") && files.Contains("engine-snapshot.json"),
                new { dir = Path.GetFileName(rec.Dir), files, rec.Symptom, rec.Target, markTime });

            uploads.EnqueueDump(rec.Dir);
            uploads.EnqueueAnnotation(Path.GetFileName(rec.Dir),
                Json.Serialize(new { symptom = "slow", memo = "selftest 메모", answeredAt = DateTimeOffset.Now, via = "selftest" }));
            var run = uploads.Outbox.ProcessAsync(transport, CancellationToken.None).Result;
            Step("upload", run.Sent == 2 && run.Remaining == 0, run);

            var hb = uploads.SendHeartbeatAsync(CancellationToken.None).Result;
            Step("heartbeat", hb.Outcome == SendOutcome.Ok, hb);

            if (steps.Any(s => !(bool)s.GetType().GetProperty("ok")!.GetValue(s)!)) code = 1;
        }
        catch (Exception ex)
        {
            Step("exception", false, $"{ex.GetType().Name}: {ex.Message}");
            code = 1;
        }
        finally
        {
            host.Stop(TimeSpan.FromSeconds(10));
        }

        var summary = Json.Serialize(new { selftest = code == 0 ? "pass" : "fail", participantId = state.ParticipantId, started, finished = DateTimeOffset.Now, steps }, indented: true);
        Console.WriteLine(summary);
        if (opt.OutFile != null) File.WriteAllText(opt.OutFile, summary);
        return code;
    }
}
