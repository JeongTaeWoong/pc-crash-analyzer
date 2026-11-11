using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Forms;
using BlackBox.Participant.Ui;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>화면 점검용(--render-forms DIR): 창을 화면 밖에 띄워 그림 파일로 저장한다. 설정·네트워크·수집 없음.</summary>
internal static class RenderForms
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(3072);
        var study = new StudyConfig
        {
            Endpoint = "https://script.google.com/macros/s/EXAMPLE/exec",
            Token = "render_only_0123456789",
            PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
            KeyId = StudyConfig.ComputeKeyId(rsa),
            Contact = "정태웅 · 카카오톡",
        };
        var apps = new List<(string, string)> { ("blender.exe", "Blender (blender.exe)"), ("chrome.exe", "Google Chrome (chrome.exe)"), ("Code.exe", "Visual Studio Code (Code.exe)") };
        var now = DateTimeOffset.Now;
        var rows = new List<RecordsForm.Row>
        {
            new("20261029-153012-crash-1000", "", now.AddMinutes(-40), "crash-1000", "blender.exe", "프로그램이 갑자기 꺼졌어요 · 렌더 중", "전송됨"),
            new("20261029-160155-manual", "", now.AddMinutes(-12), "manual", "chrome.exe", "점점 느려졌어요", "대기 중"),
            new("20261029-161502-power-41", "", now.AddMinutes(-2), "power-41", null, null, "전송됨"),
        };

        var forms = new (string File, Form Form)[]
        {
            ("consent.png", new ConsentForm(study, "k7m2p9x4q3ab")),
            ("report-new.png", new ReportForm(ReportMode.NewMark, now, null, null, "blender.exe", apps, 120)),
            ("report-annotate.png", new ReportForm(ReportMode.Annotate, now.AddMinutes(-40), "app_crash", "blender.exe", null, Array.Empty<(string, string)>(), 240, "프로그램 강제 종료")),
            ("records.png", new RecordsForm(rows, Path.GetTempPath())),
            ("withdraw.png", new WithdrawForm("k7m2p9x4q3ab")),
        };
        foreach (var (file, form) in forms)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-10000, -10000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, file), ImageFormat.Png);
            Console.WriteLine($"[render] {file} {form.Width}x{form.Height}");
            form.Close();
            form.Dispose();
        }
        return 0;
    }
}
