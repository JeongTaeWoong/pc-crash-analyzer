using System.Text.Json;
using BlackBox.Core;
using BlackBox.Marking;
using Xunit;

namespace BlackBox.Tests;

public class SanitizerTests
{
    private const string Profile = @"C:\Users\kimcs";

    [Fact]
    public void StaticInitializer_DoesNotThrow()
    {
        // 10/29: 정적 필드 순서 때문에 형식 초기화가 실패해 모든 덤프가 죽었다 — 시스템 값 경로를 한 번 지나가게 한다
        var r = Sanitizer.SanitizeJson("{\"a\":\"b\"}");
        Assert.Contains("\"a\"", r);
        Assert.NotNull(Sanitizer.SanitizeText("hello"));
    }

    [Fact]
    public void ProfilePath_Replaced_InEscapedJson()
    {
        var json = JsonSerializer.Serialize(new { path = @"C:\Users\kimcs\AppData\Local\Temp\x.dll" });
        var r = Sanitizer.SanitizeJson(json, Profile, null, null);
        Assert.Contains("%USERPROFILE%", r);
        Assert.DoesNotContain("kimcs", r);
    }

    [Fact]
    public void UserAndMachine_Replaced_InValuesOnly_WithWordBoundary()
    {
        var json = JsonSerializer.Serialize(new
        {
            message = @"사용자 DESKTOP-7Q2K1\kimcs 로그온, 컴퓨터 DESKTOP-7Q2K1",
            module = "kimcs2.dll",       // 낱말 경계 — 다른 이름의 일부는 치환하지 않는다
            kimcs = "key stays",         // 키는 치환하지 않는다(문서 구조 유지)
            n = 3,
        });
        var r = Sanitizer.SanitizeJson(json, Profile, "kimcs", "DESKTOP-7Q2K1");
        using var doc = JsonDocument.Parse(r);
        var root = doc.RootElement;
        Assert.Equal(@"사용자 %COMPUTERNAME%\%USERNAME% 로그온, 컴퓨터 %COMPUTERNAME%", root.GetProperty("message").GetString());
        Assert.Equal("kimcs2.dll", root.GetProperty("module").GetString());
        Assert.Equal("key stays", root.GetProperty("kimcs").GetString());
        Assert.Equal(3, root.GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("user")]   // 흔한 낱말 — 치환하면 "user32.dll" 같은 값과 문서가 망가진다
    [InlineData("name")]
    [InlineData("ab")]     // 너무 짧다
    public void CommonOrShortNames_NotReplaced(string user)
    {
        var json = JsonSerializer.Serialize(new { name = $"{user} test user32" });
        Assert.Equal(json, Sanitizer.SanitizeJson(json, Profile, user, null));
    }

    [Fact]
    public void IndentedJson_StaysIndented()
    {
        var json = JsonSerializer.Serialize(new { m = "kimcs here" }, new JsonSerializerOptions { WriteIndented = true });
        var r = Sanitizer.SanitizeJson(json, Profile, "kimcs", null);
        Assert.Contains("\n", r);
        Assert.Contains("%USERNAME% here", r);
    }

    [Fact]
    public void NotJson_StillReplacesNames()
    {
        // 10/29 검토: JSON 으로 못 읽으면 이름 치환을 건너뛰던 것 → 원문 전체에 치환
        var r = Sanitizer.SanitizeJson(@"plain C:\Users\kimcs\x kimcs", Profile, "kimcs", null);
        Assert.Equal(@"plain %USERPROFILE%\x %USERNAME%", r);
    }

    [Fact]
    public void ProfilePrefix_OfAnotherUser_NotReplaced()
    {
        // 사용자 kim 일 때 C:\Users\kimberly 가 "%USERPROFILE%berly" 가 되면 안 된다
        var json = JsonSerializer.Serialize(new { a = @"C:\Users\kim\Desktop\x.txt", b = @"C:\Users\kimberly\x.txt", c = @"C:\Users\kim" });
        using var doc = JsonDocument.Parse(Sanitizer.SanitizeJson(json, @"C:\Users\kim", null, null));
        Assert.Equal(@"%USERPROFILE%\Desktop\x.txt", doc.RootElement.GetProperty("a").GetString());
        Assert.Equal(@"C:\Users\kimberly\x.txt", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("%USERPROFILE%", doc.RootElement.GetProperty("c").GetString());
    }

    [Fact]
    public void ShortProfilePath_Replaced()
    {
        var json = JsonSerializer.Serialize(new { a = @"C:\Users\KIMCHE~1\AppData\Local\Temp\x.dmp" });
        var r = Sanitizer.SanitizeJson(json, @"C:\Users\kimcheolsu", null, null, profileShort: @"C:\Users\KIMCHE~1");
        Assert.DoesNotContain("KIMCHE", r);
        Assert.Contains("%USERPROFILE%", r);
    }

    [Fact]
    public void ShortKoreanName_ReplacedOnlyAsPathSegmentOrDomainUser()
    {
        var json = JsonSerializer.Serialize(new
        {
            a = @"D:\작업\민수\렌더.blend",
            b = @"DESKTOP-7Q2K1\민수",
            c = "민수네 회사 프로젝트",     // 일반 문장 속 두 글자 이름은 낱말 경계로 못 가르므로 남는다(알려진 한계)
        });
        using var doc = JsonDocument.Parse(Sanitizer.SanitizeJson(json, @"C:\Users\민수", "민수", null));
        Assert.Equal(@"D:\작업\%USERNAME%\렌더.blend", doc.RootElement.GetProperty("a").GetString());
        Assert.Equal(@"DESKTOP-7Q2K1\%USERNAME%", doc.RootElement.GetProperty("b").GetString());
        Assert.Equal("민수네 회사 프로젝트", doc.RootElement.GetProperty("c").GetString());
    }
}

public class CommandLineTextTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --token=SECRET", "\"C:\\Program Files\\App\\app.exe\" <인자 생략>")]
    [InlineData("C:\\Program Files\\App\\app.exe --password hunter2", "C:\\Program Files\\App\\app.exe <인자 생략>")]
    [InlineData("notepad.exe C:\\Users\\a\\secret.txt", "notepad.exe <인자 생략>")]
    [InlineData("svchost -k netsvcs", "svchost <인자 생략>")]
    [InlineData("C:\\Windows\\explorer.exe", "C:\\Windows\\explorer.exe")]
    [InlineData("curl -H \"Authorization: Bearer X\" -o setup.exe", "curl <인자 생략>")]          // 10/29 검토: 통째로 남던 경우
    [InlineData("C:\\Tools\\curl.exe -H \"Authorization: Bearer X\"", "C:\\Tools\\curl.exe <인자 생략>")]
    [InlineData("C:\\Windows\\system32\\cmd.exe /c C:\\secret\\run.exe", "C:\\Windows\\system32\\cmd.exe <인자 생략>")]
    [InlineData("", "")]
    public void ExecutableOnly(string cmd, string expected) => Assert.Equal(expected, CommandLineText.ExecutableOnly(cmd));
}

public class ManualMarkNormalizeTests
{
    [Fact]
    public void Normalize_TargetSymptomMemo()
    {
        var m = MarkService.NormalizeManual(new ManualMark("SLOW", " blender ", "  렌더 중\n멈춤\t" + new string('가', 400)));
        Assert.Equal("slow", m.Symptom);
        Assert.Equal("blender.exe", m.Target);
        Assert.NotNull(m.Memo);
        Assert.DoesNotContain("\n", m.Memo);
        Assert.Equal(300, m.Memo!.Length);
    }

    [Fact]
    public void Normalize_UnknownSymptom_Dropped() =>
        Assert.Null(MarkService.NormalizeManual(new ManualMark("hacked", null, "")).Symptom);
}
