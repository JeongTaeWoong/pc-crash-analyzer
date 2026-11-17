using System.Text.Json;
using CrashAnalyzer.Ai;
using Xunit;

namespace CrashAnalyzer.Ai.Tests;

public class EvidenceBundleTests
{
    public static IEnumerable<object[]> Golden() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "golden"), "*.snapshot.json").Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(Golden))]
    public void The_bundle_is_the_same_text_the_evaluation_harness_makes(string file)
    {
        // 평가 실험(Python)이 같은 스냅샷으로 만든 묶음과 글자까지 같아야 한다: 평가에서 잰 입력 = 제품이 보내는 입력
        string dir = Path.Combine(AppContext.BaseDirectory, "golden");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, file)));
        string expected = File.ReadAllText(Path.Combine(dir, file.Replace(".snapshot.json", ".bundle.txt"))).Replace("\r\n", "\n");

        var bundle = EvidenceBundle.Build(doc.RootElement);

        Assert.Equal(expected, bundle.Text);
        Assert.Contains("sym", bundle.Ids);
        Assert.Contains("host", bundle.Ids);
    }

    [Fact]
    public void There_are_reference_bundles_to_compare_with() => Assert.True(Golden().Count() >= 4);

    [Fact]
    public void A_snapshot_with_almost_nothing_still_gives_a_bundle()
    {
        using var doc = JsonDocument.Parse("""{"schema_version":"1.1","symptom":{"type":"unknown","target_process":""}}""");
        var bundle = EvidenceBundle.Build(doc.RootElement);
        Assert.StartsWith("## 사건", bundle.Text);
        Assert.Contains("\"target_process\":null", bundle.Text);
        Assert.Contains("\"faulting_modules\":[]", bundle.Text);
    }
}

public class PromptTests
{
    [Fact]
    public void The_prompt_and_schema_come_from_the_harness_export()
    {
        Assert.StartsWith("당신은 Windows PC 문제의 원인을 찾는 진단 도우미입니다", AiPrompt.System);
        Assert.StartsWith("p", AiPrompt.PromptVersion);
        Assert.Equal(EvidenceBundle.Version, AiPrompt.BundleVersion);          // 묶음을 만드는 코드와 내보낸 판이 같은 판인가
        var schema = AiPrompt.AnswerSchema();
        Assert.Equal("object", schema["type"].GetString());
        Assert.True(schema["properties"].TryGetProperty("recommended_actions", out _));
    }
}

public class AiAnswerTests
{
    private const string Good = """
        {"verdict":"culprit_found","candidates":[{"name":"git.exe","kind":"process","confidence":"high","evidence_ids":["ev1"],"reason":"오류 모듈이 git.exe 자신입니다."}],
         "recommended_actions":[{"level":"Lv1","action":"Git 을 다시 실행해 봅니다","target":"git.exe","why":"일시적인 문제일 수 있습니다"}],
         "explanation_for_user":"Git 이 스스로 꺼졌습니다."}
        """;

    [Fact]
    public void A_well_formed_answer_is_read()
    {
        var a = AiAnswer.Parse(Good);
        Assert.NotNull(a);
        Assert.Equal("git.exe", a!.Candidates[0].Name);
        Assert.Equal("Lv1", a.Actions[0].Level);
        Assert.Equal("원인을 찾았습니다", a.VerdictText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"verdict":"culprit_found"}""")]
    public void A_broken_answer_is_not_an_answer(string text) => Assert.Null(AiAnswer.Parse(text));
}

public class AdviceCheckTests
{
    private static AiAnswer Answer(string candidateName, string kind, string reason, params (string Level, string Action, string Target, string Why)[] actions) =>
        new("culprit_found", [new AiCandidate(candidateName, kind, "high", [], reason)], actions.Select(a => new AiAction(a.Level, a.Action, a.Target, a.Why)).ToList(), "");

    [Fact]
    public void Updating_the_graphics_driver_for_a_programs_own_bug_is_flagged()
    {
        // 2026-11-16 개발용 답에서 실제로 나온 모양: 원인은 Godot 자체인데 그래픽 드라이버도 바꾸라고 함
        var a = Answer("Godot_v4.3-stable_win64.exe", "process", "오류 모듈이 Godot 자신입니다.",
            ("Lv1", "Godot 을 다시 실행해 봅니다", "Godot", "일시적일 수 있습니다"),
            ("Lv3", "Godot 을 최신 버전으로 업데이트합니다", "Godot", "알려진 버그일 수 있습니다"),
            ("Lv3", "그래픽 드라이버를 최신 버전으로 업데이트합니다", "그래픽 카드 드라이버", "렌더링 문제일 수 있습니다"));

        var f = AdviceCheck.Check(a);

        var one = Assert.Single(f);
        Assert.Equal(2, one.ActionIndex);
        Assert.Equal(AdviceCheck.Flag.NotTheCause, one.Flag);
    }

    [Fact]
    public void The_same_step_as_a_conditional_next_step_is_not_flagged()
    {
        var a = Answer("Godot_v4.3-stable_win64.exe", "process", "오류 모듈이 Godot 자신입니다.",
            ("Lv3", "Godot 업데이트 뒤에도 반복되면 그래픽 드라이버 업데이트를 검토합니다", "그래픽 카드 드라이버", "그래도 계속되면 화면 쪽을 봅니다"));
        Assert.Empty(AdviceCheck.Check(a));
    }

    [Fact]
    public void A_driver_step_is_fine_when_a_driver_is_the_candidate()
    {
        var a = Answer("nvlddmkm.sys", "driver", "그래픽 드라이버에서 응답 지연이 기록됐습니다.",
            ("Lv3", "그래픽 드라이버를 업데이트합니다", "NVIDIA 드라이버", "응답 지연의 직접 원인입니다"));
        Assert.Empty(AdviceCheck.Check(a));
    }

    [Fact]
    public void Repairing_a_runtime_and_adding_memory_for_an_app_bug_are_flagged()
    {
        var a = Answer("python.exe", "process", "Python 이 스스로 꺼졌습니다.",
            ("Lv3", "Visual C++ 재배포 패키지를 복구합니다", "Microsoft Visual C++ 2022", "런타임 문제일 수 있습니다"),
            ("Lv4", "메모리를 증설합니다", "RAM", "여유가 적습니다"));
        var f = AdviceCheck.Check(a);
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.Equal(AdviceCheck.Flag.NotTheCause, x.Flag));
    }

    [Fact]
    public void Turning_the_antivirus_off_is_flagged_but_scheduling_or_excluding_is_not()
    {
        var bad = Answer("MsMpEng.exe", "process", "Defender 전체 검사가 CPU 를 쓰고 있었습니다.",
            ("Lv2", "Windows Defender 실시간 보호를 끕니다", "Windows 보안", "검사가 느려짐의 원인입니다"));
        var ok = Answer("MsMpEng.exe", "process", "Defender 전체 검사가 CPU 를 쓰고 있었습니다.",
            ("Lv0", "검사가 끝날 때까지 기다립니다", "Microsoft Defender", "고장이 아닙니다"),
            ("Lv2", "전체 검사 예약 시간을 PC 를 쓰지 않는 때로 옮깁니다", "Windows 보안의 검사 일정", "되돌리려면 예약을 다시 바꾸면 됩니다"),
            ("Lv2", "작업 폴더를 Defender 검사 예외에 추가합니다", "Windows 보안", "백신을 끄지 않고 부담을 줄입니다"));

        Assert.Contains(AdviceCheck.Check(bad), x => x.Flag == AdviceCheck.Flag.DisablesSecurity);
        Assert.Empty(AdviceCheck.Check(ok));
    }

    [Fact]
    public void Reinstalling_Windows_is_flagged()
    {
        var a = Answer("notepad++.exe", "process", "Notepad++ 가 꺼졌습니다.", ("Lv3", "Windows 를 초기화합니다", "Windows", "깨끗한 상태로 돌립니다"));
        Assert.Contains(AdviceCheck.Check(a), x => x.Flag == AdviceCheck.Flag.SystemDamage);
    }

    [Fact]
    public void An_answer_without_candidates_gets_no_not_the_cause_flag()
    {
        var a = new AiAnswer("no_problem_found", [], [new AiAction("Lv0", "드라이버 기록을 지켜봅니다", "이벤트 뷰어", "참고용")], "");
        Assert.Empty(AdviceCheck.Check(a));
    }
}

public class KeyStoreTests
{
    [Fact]
    public void A_key_is_stored_protected_and_comes_back_only_through_the_store()
    {
        string path = Path.Combine(Path.GetTempPath(), "pca-ai-key-test-" + Guid.NewGuid().ToString("N") + ".bin");
        const string fake = "sk-test-NOT-A-REAL-KEY-0123456789abcd";       // 시험용 가짜 문자열 — 실제 키가 아니다
        try
        {
            KeyStore.Save("  " + fake + "\n", path);

            Assert.True(KeyStore.Exists(path));
            Assert.Equal(fake, KeyStore.Load(path));
            Assert.DoesNotContain(fake, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));   // 파일에는 평문이 없다
            Assert.Equal("····abcd", KeyStore.Masked(fake));

            File.WriteAllBytes(path, [1, 2, 3, 4]);                                                       // 망가진 파일은 "키 없음"
            Assert.Null(KeyStore.Load(path));
            KeyStore.Delete(path);
            Assert.False(KeyStore.Exists(path));
            Assert.Null(KeyStore.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}

public class PricingAndStatusTests
{
    [Fact]
    public void Cost_follows_the_price_table()
    {
        Assert.Equal(0.0017, Pricing.CostUsd("claude-haiku-5-5", 7000, 2000), 4);     // 실험에서 잰 값(약 0.002달러)과 같은 크기
        Assert.Equal(0.034, Pricing.CostUsd("claude-sonnet-5-5", 7000, 2000), 3);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var r = await ClaudeDiagnosis.RunAsync(null, "claude-haiku-5-5", "x");
        Assert.Equal("no_key", r.Status);
        Assert.Null(r.Answer);
        Assert.Equal(0, r.InputTokens);
        Assert.Contains("키", ClaudeDiagnosis.StatusText(r.Status));
    }
}
