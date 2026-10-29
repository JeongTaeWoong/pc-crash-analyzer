using System.Runtime.InteropServices;
using System.Windows.Forms;
using BlackBox.Participant.Ui;
using StudyCore;

namespace BlackBox.Participant;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppOptions opt;
        try { opt = AppOptions.Parse(args); }
        catch (Exception ex) { return Fail($"실행 옵션 오류: {ex.Message}", headless: true, code: 64); }

        if (opt.Headless)
        {
            AttachConsole(-1); // terminal output
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OpsLog.Write($"[ui-error] {e.Exception.GetType().Name}: {e.Exception.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OpsLog.Write($"[fatal] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { OpsLog.Write($"[task-error] {e.Exception.GetBaseException().Message}"); e.SetObserved(); };

        if (opt.RenderFormsDir != null) return RenderForms.Run(opt.RenderFormsDir);

        // 1) 연구 설정 (배포본: exe 옆 study.json)
        StudyConfig study;
        try { study = StudyConfig.Load(opt.StudyConfigPath); }
        catch (Exception ex)
        {
            return Fail($"설정 파일(study.json)을 읽지 못했어요.\n받은 zip 을 다시 풀어 주세요.\n\n{ex.Message}", opt.Headless, 2);
        }
        var errors = study.Validate();
        if (errors.Count > 0)
            return Fail("설정 파일(study.json)이 올바르지 않아요. 연구팀에 알려 주세요.\n\n" + string.Join("\n", errors), opt.Headless, 2);

        // 시험 전용 옵션(동의 건너뛰기 등)은 127.0.0.1 시험 수신기에서만 — 배포본에서 동의 화면을 건너뛸 길을 막는다
        if (opt.HasTestFlags && !study.IsLoopbackEndpoint)
            return Fail("--test-* / --startup-dir 옵션은 127.0.0.1 시험 수신기 설정에서만 쓸 수 있어요.", opt.Headless, 2);
        // 시험 실행은 자기 데이터 폴더에서만 — 시험 동의가 실제 데이터 폴더에 남지 않게(10/29 검토 지적)
        if ((opt.HasTestFlags || study.IsLoopbackEndpoint) && opt.DataDirIsDefault)
            return Fail("시험 수신기 설정이나 --test-* 옵션은 --data-dir 로 별도 폴더를 줄 때만 쓸 수 있어요.", opt.Headless, 2);

        // 2) 압축 보기 안에서 바로 실행하면 임시 폴더라 곧 지워진다
        if (!opt.Headless && Installation.RunningFromTemp())
            return Fail("압축 파일 안에서 바로 실행하셨어요.\n\nzip 파일을 오른쪽 클릭 → \"압축 풀기\"로 원하는 폴더(예: 문서)에 푼 다음,\n그 폴더의 PcBlackBox.exe 를 실행해 주세요.", false, 3);

        // 3) 한 번만 실행
        using var mutex = new Mutex(true, opt.MutexName, out bool first);
        if (!first)
        {
            if (opt.Headless) return Fail("같은 데이터 폴더로 PC 블랙박스가 이미 실행 중이라 자체 점검을 하지 않았어요. 트레이 앱을 끄거나 --data-dir 을 따로 주세요.", true, 3);
            if (!opt.Autostart)
                MessageBox.Show("PC 블랙박스가 이미 실행 중이에요.\n작업 표시줄 오른쪽(시계 옆) 아이콘을 눌러 보세요. 숨은 아이콘(^) 안에 있을 수 있어요.",
                    Texts.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Directory.CreateDirectory(opt.DataDir);
        var state = ParticipantState.Load(opt.DataDir);

        if (opt.SelfTest)
        {
            // 실제 수신 주소로 이 PC 의 기록을 보내므로, 이 PC 에서 한 번 동의한 뒤에만 허용한다(시험 수신기는 예외)
            if (!study.IsLoopbackEndpoint && !state.HasConsentFor(study))
                return Fail("자체 점검은 이 PC 에서 먼저 PcBlackBox.exe 를 실행해 동의한 뒤에 쓸 수 있어요.", true, 2);
            OpsLog.Install(teeToConsole: true);
            return SelfTest.Run(opt, study, state);
        }

        // 4) 연구 기간이 끝났으면 기록하지 않고 정리를 안내한다
        if (study.CollectionEnded(DateTime.Today))
        {
            try { StartupShortcut.Remove(opt.StartupDir); } catch { }
            if (!state.HasConsentFor(study)) return 0;
            var answer = MessageBox.Show($"연구 기간({study.CollectLastDay:yyyy-MM-dd})이 끝나 기록을 멈췄어요. 참여해 주셔서 정말 감사합니다.\n" +
                "자동 실행도 꺼 두었어요.\n\n이 PC에 남은 기록도 지울까요? (프로그램 폴더는 직접 지우시면 됩니다)",
                Texts.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) LocalData.DeleteAll(opt.DataDir);
            else { state.Autostart = false; state.Save(opt.DataDir); }
            return 0;
        }

        // 5) 동의 — 거절하면 아무것도 저장하지 않고 끝난다. 동의는 연구·받는 곳에 묶인다.
        if (!state.HasConsentFor(study))
        {
            if (opt.Autostart) return 0; // 동의가 철회된 뒤 남은 바로가기 — 조용히 끝낸다
            var pid = state.ParticipantIdFor(study);
            if (opt.TestConsent)
            {
                state.Accept(pid, "test", autostart: opt.StartupDir != null, study); // 시험: 임시 시작 폴더에서 바로가기 생성까지 확인
            }
            else
            {
                using var consent = new ConsentForm(study, pid);
                if (consent.ShowDialog() != DialogResult.OK) return 0;
                state.Accept(pid, consent.Nickname, consent.Autostart, study);
            }
            state.Save(opt.DataDir);
            if (state.Autostart)
            {
                try { StartupShortcut.Create(Installation.ExePath, opt.StartupDir); }
                catch (Exception ex)
                {
                    state.Autostart = false;
                    state.Save(opt.DataDir);
                    MessageBox.Show($"자동 실행을 켜지 못했어요({ex.Message}). 아이콘 메뉴에서 다시 켤 수 있어요.", Texts.AppTitle);
                }
            }
        }

        OpsLog.Install(teeToConsole: opt.HasTestFlags); // 시험 실행은 운영 기록을 터미널에도
        OpsLog.Write($"[app] PcBlackBox {TrayApp.Version} 시작 (참여 ID {state.ParticipantId}, {(opt.Autostart ? "자동 실행" : "직접 실행")})");
        using var app = new TrayApp(opt, study, state);
        Application.Run(app);
        return 0;
    }

    private static int Fail(string message, bool headless, int code)
    {
        if (headless) Console.Error.WriteLine(message);
        else MessageBox.Show(message, Texts.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return code;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
