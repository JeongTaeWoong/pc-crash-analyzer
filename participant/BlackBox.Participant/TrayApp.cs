using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using BlackBox.Core;
using BlackBox.Hosting;
using BlackBox.Marking;
using BlackBox.Participant.Ui;
using Microsoft.Win32;
using StudyCore;

namespace BlackBox.Participant;

/// <summary>
/// 작업 표시줄 알림 영역 아이콘 하나로 동작하는 참여자판 본체.
/// 수집(CollectorHost) → 덤프 → 알림·메모 → 암호화 전송(UploadService). 조치(프로그램 종료·설정 변경)는 하지 않는다.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan BalloonGap = TimeSpan.FromMinutes(3);
    private const int ReportAutoCloseSeconds = 120;
    /// <summary>대화상자 답을 기다리는 최대 시간(마킹 기준). 대화상자 자동 닫힘보다 조금 길게.</summary>
    private static readonly TimeSpan ReportMaxWait = TimeSpan.FromSeconds(ReportAutoCloseSeconds + 20);

    private readonly AppOptions _opt;
    private readonly StudyConfig _study;
    private readonly ParticipantState _state;
    private readonly string _dumpRoot;
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem, _pauseItem, _autostartItem;
    private readonly CollectorHost _host;
    private readonly HotkeyListener _hotkey = new();
    private readonly UploadService _uploads;
    private readonly System.Windows.Forms.Timer _fgTimer = new() { Interval = 1500 };
    private int _lastExternalPid;
    private ReportForm? _openReport;
    private RecordsForm? _openRecords;
    private string? _balloonDumpDir;
    private DateTimeOffset _lastBalloon = DateTimeOffset.MinValue;
    private int _dumpsTotal, _dumpsAuto, _dumpsManual, _dumpsHidden;
    private volatile bool _collectorReady, _exiting, _withdrawing;
    /// <summary>수동 기록 연타 방지 — 같은 순간(덤프 뒤 구간 30초) 안의 반복은 무시(10/29 검토: 전체 화면에서 알림이 안 보여 연타).</summary>
    private DateTimeOffset _lastManualMark = DateTimeOffset.MinValue;
    private static readonly TimeSpan ManualDebounce = TimeSpan.FromSeconds(30);
    private SourceStatus? _hotkeyStatus;

    public static string Version => typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0.7.1";

    public TrayApp(AppOptions opt, StudyConfig study, ParticipantState state)
    {
        _opt = opt;
        _study = study;
        _state = state;
        _dumpRoot = Path.Combine(opt.DataDir, "dumps");
        _ = _ui.Handle; // UI 스레드 마샬링용 숨은 창 핸들

        var menu = new ContextMenuStrip { Font = UiKit.Body };
        menu.Items.Add(new ToolStripMenuItem($"지금 문제 기록하기   ({Texts.HotkeyText})", null, (_, _) => BeginReportFromMenu()) { Font = new System.Drawing.Font(UiKit.Body, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        _statusItem = new ToolStripMenuItem("시작하는 중…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripMenuItem("최근 기록 보기", null, (_, _) => ShowRecords()));
        _pauseItem = new ToolStripMenuItem("전송 일시중지", null, (_, _) => TogglePause()) { CheckOnClick = false };
        menu.Items.Add(_pauseItem);
        _autostartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (_, _) => ToggleAutostart());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripMenuItem("참여 정보", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("참여 그만두기…", null, async (_, _) => await Withdraw()));
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => ExitFromMenu()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { Icon = AppIcons.Recording, Text = "PC 블랙박스 · 시작하는 중…", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowRecords();
        _tray.BalloonTipClicked += (_, _) => { if (_balloonDumpDir != null) Annotate(_balloonDumpDir); };

        _host = new CollectorHost(new CollectorHostOptions
        {
            DumpRoot = _dumpRoot,
            Edition = "participant",
            StripCommandLineArgs = true,
        });
        _host.Marks.DumpWritten += OnDumpWritten;

        var transport = new HttpUploadTransport(study.EndpointUri!, $"PcBlackBox/{Version} (+research; Windows)");
        _uploads = new UploadService(study, () => _state, opt.DataDir, transport, HeartbeatPayload, Version);
        _uploads.Changed += () => PostUi(UpdateTrayText);

        // 자동 실행 바로가기: 폴더를 옮겼으면 경로만 고친다. 참여자가 지운 바로가기는 되살리지 않는다.
        try
        {
            bool exists = StartupShortcut.RepairIfMoved(Installation.ExePath, opt.StartupDir, opt.ShortcutDataDir);
            if (_state.Autostart != exists) { _state.Autostart = exists; SaveState(); }
        }
        catch (Exception ex) { OpsLog.Write($"[autostart] 확인 실패: {ex.Message}"); }

        _fgTimer.Tick += (_, _) => { int pid = Desktop.CurrentPid(); if (pid > 0) _lastExternalPid = pid; };
        _fgTimer.Start();

        // 로그오프·종료가 "실제로" 일어날 때만(SessionEnded). SessionEnding 은 취소될 수 있다 — 취소 뒤 아이콘 없이 도는 상태를 막는다(10/29 검토 지적)
        SystemEvents.SessionEnded += OnSessionEnded;
        Task.Run(StartCollector);
        _uploads.Start(firstHeartbeatDelay: opt.HasTestFlags ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(2));

        if (!opt.Autostart)
            ShowBalloon("PC 블랙박스가 켜졌어요", $"문제가 생기면 {Texts.HotkeyText} 를 눌러 주세요. 프로그램 강제 종료 같은 문제는 저절로 기록돼요.", null);

        if (opt.TestMarkAfterSec > 0) After(opt.TestMarkAfterSec, () => OnHotkey());
        if (opt.TestExitAfterSec > 0) After(opt.TestExitAfterSec, () => Shutdown(confirm: false));
        if (opt.TestWithdrawAfterSec > 0) After(opt.TestWithdrawAfterSec, () => _ = Withdraw(auto: true));
    }

    // ───────── 수집기 ─────────

    private void StartCollector()
    {
        try
        {
            _host.Start(OpsLog.Write);
            _hotkeyStatus = _hotkey.Start(OnHotkey);
            _host.Statuses.Add(_hotkeyStatus);
            OpsLog.Write($"[source] {Json.Serialize(_hotkeyStatus)}");
            _collectorReady = true;
            PruneLocal();
            PostUi(() =>
            {
                UpdateTrayText();
                if (_hotkeyStatus.Status != "ok")
                    ShowBalloon("단축키를 쓸 수 없어요", $"다른 프로그램이 {Texts.HotkeyText} 를 쓰고 있어요. 아이콘 메뉴의 \"지금 문제 기록하기\"를 써 주세요.", null);
            });
        }
        catch (Exception ex)
        {
            OpsLog.Write($"[start-error] {ex.GetType().Name}: {ex.Message}");
            PostUi(() => _tray.Text = "PC 블랙박스 · 시작 오류 (참여 정보 참고)");
        }
    }

    /// <summary>단축키 스레드에서 불린다. 대화상자가 초점을 가져가기 전에 전경 프로그램을 먼저 잡는다.</summary>
    private void OnHotkey()
    {
        var fg = Desktop.Current();
        PostUi(() => BeginReport("hotkey", fg ?? LastExternal(), fromHotkey: fg != null));
    }

    /// <summary>트레이 메뉴를 누르면 전경이 작업 표시줄로 가므로, 그 직전에 쓰던 프로그램을 쓴다.</summary>
    private Desktop.Foreground? LastExternal()
    {
        int pid = _lastExternalPid;
        return pid > 0 && Desktop.ProcessName(pid) is { } name ? new Desktop.Foreground(pid, name, false) : null;
    }

    private void BeginReportFromMenu() => BeginReport("tray-menu", LastExternal(), fromHotkey: false);

    private void BeginReport(string via, Desktop.Foreground? fg, bool fromHotkey)
    {
        if (!_collectorReady) { ShowBalloon("아직 준비 중이에요", "몇 초 뒤에 다시 눌러 주세요.", null); return; }
        if (_openReport != null) { _openReport.Activate(); return; } // 이미 묻는 중 — 같은 순간을 두 번 기록하지 않는다
        if (DateTimeOffset.Now - _lastManualMark < ManualDebounce)
        {
            if (fg?.Fullscreen != true) ShowBalloon("방금 기록했어요", "30초 안에 다시 누른 것은 같은 순간으로 봐요.", null);
            return;
        }
        _lastManualMark = DateTimeOffset.Now;

        var answer = new TaskCompletionSource<ManualMark?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var markTime = _host.Marks.MarkManualDeferred(via, fg?.Pid, answer.Task, ReportMaxWait);
        _dumpsManual++;

        // 전체 화면 게임·영상 중에는 창을 띄우지 않는다(게임이 최소화되거나 멈출 수 있다) — 나중에 "최근 기록"에서 메모 가능
        if (fromHotkey && fg?.Fullscreen == true)
        {
            answer.TrySetResult(new ManualMark(null, fg.ProcessName));
            ShowBalloon("기록했어요", "전체 화면이라 질문 창은 띄우지 않았어요. 나중에 아이콘 → 최근 기록에서 메모를 남길 수 있어요.", null);
            return;
        }

        List<(string, string)> apps;
        try { apps = Desktop.WindowedApps(); } catch { apps = new(); }
        var form = new ReportForm(ReportMode.NewMark, markTime, null, null, fg?.ProcessName, apps, ReportAutoCloseSeconds,
            testAutoAnswer: _opt.TestAutoAnswer);
        form.FormClosed += (_, _) =>
        {
            answer.TrySetResult(form.Answer);
            _openReport = null;
            form.Dispose();
        };
        _openReport = form;
        form.Show();
    }

    private void OnDumpWritten(DumpRecord r)
    {
        Interlocked.Increment(ref _dumpsTotal);
        if (r.Auto) Interlocked.Increment(ref _dumpsAuto);
        if (r.Tier == "hidden") Interlocked.Increment(ref _dumpsHidden);
        if (_withdrawing) return; // 참여 철회 중 — 정리 도중 끝난 덤프는 보내지 않는다(지운다)
        try { _uploads.EnqueueDump(r.Dir); }
        catch (Exception ex) { OpsLog.Write($"[upload-error] 봉투 만들기 실패 {Path.GetFileName(r.Dir)}: {ex.GetType().Name}: {ex.Message}"); }
        PruneLocal();

        if (!r.Auto) return; // 수동 기록은 대화상자에서 이미 알았다
        // 유의성 낮음(숨김 등급) — 백그라운드 도구 크래시 등은 조용히 기록만
        if (r.Tier == "hidden") return;
        PostUi(() => ShowBalloon($"{Texts.KindLabel(r.Kind)} 감지", Texts.AutoDetectedMessage(r.Kind, r.ProcessName), r.Dir));
    }

    // ───────── 메모(나중에) ─────────

    private void Annotate(string dumpDir)
    {
        try
        {
            var name = Path.GetFileName(dumpDir);
            var row = RecordsForm.LoadRows(_dumpRoot, UploadState).FirstOrDefault(x => x.Name == name);
            if (row == null) return;
            var form = new ReportForm(ReportMode.Annotate, row.MarkTime, Texts.SymptomForKind(row.Kind), row.Process, null,
                Array.Empty<(string, string)>(), ReportAutoCloseSeconds * 2, Texts.KindLabel(row.Kind));
            form.FormClosed += (_, _) =>
            {
                var a = form.Answer;
                form.Dispose();
                if (a == null) return;
                var m = MarkService.NormalizeManual(a);
                var report = Json.Serialize(new { symptom = m.Symptom, target = m.Target, memo = m.Memo, answeredAt = DateTimeOffset.Now, via = "annotate" }, indented: true);
                try
                {
                    File.WriteAllText(Path.Combine(dumpDir, "report.json"), Sanitizer.SanitizeJson(report));
                    _uploads.EnqueueAnnotation(name, Sanitizer.SanitizeJson(report));
                    ShowBalloon("메모를 남겼어요", "고맙습니다! 연구팀에 함께 전송돼요.", null);
                }
                catch (Exception ex) { OpsLog.Write($"[annotate-error] {ex.Message}"); }
            };
            form.Show();
        }
        catch (Exception ex) { OpsLog.Write($"[annotate-error] {ex.Message}"); }
    }

    // ───────── 메뉴 ─────────

    private string UploadState(string dumpName)
    {
        var ob = _uploads.Outbox;
        if (ob.IsSent("dump", dumpName)) return "전송됨";
        if (ob.IsFailed("dump", dumpName)) return "전송 안 됨";
        if (ob.IsPending("dump", dumpName)) return _state.UploadsPaused ? "일시중지" : "대기 중";
        return "-";
    }

    private void ShowRecords()
    {
        if (_openRecords != null) { _openRecords.Activate(); return; }
        var form = new RecordsForm(RecordsForm.LoadRows(_dumpRoot, UploadState), _dumpRoot);
        form.AnnotateRequested += row => Annotate(row.Dir);
        form.FormClosed += (_, _) => { _openRecords = null; form.Dispose(); };
        _openRecords = form;
        form.Show();
    }

    private void TogglePause()
    {
        _state.UploadsPaused = !_state.UploadsPaused;
        SaveState();
        OpsLog.Write($"[upload] 전송 {(_state.UploadsPaused ? "일시중지" : "재개")}");
        if (!_state.UploadsPaused) _uploads.Kick();
        UpdateTrayText();
    }

    private void ToggleAutostart()
    {
        try
        {
            if (StartupShortcut.Exists(_opt.StartupDir)) StartupShortcut.Remove(_opt.StartupDir);
            else StartupShortcut.Create(Installation.ExePath, _opt.StartupDir, _opt.ShortcutDataDir);
            _state.Autostart = StartupShortcut.Exists(_opt.StartupDir);
            SaveState();
        }
        catch (Exception ex) { MessageBox.Show($"자동 실행 설정을 바꾸지 못했어요: {ex.Message}", Texts.AppTitle); }
    }

    private void ShowAbout()
    {
        var hk = _hotkeyStatus?.Status == "ok" ? Texts.HotkeyText : "사용 불가(다른 프로그램이 사용 중)";
        var msg = $"PC 블랙박스 {Version} (연구용 시험판)\n\n" +
                  $"참여 ID: {_state.ParticipantId}\n" +
                  $"별명: {(_state.Nickname.Length > 0 ? _state.Nickname : "(없음)")}\n" +
                  $"기록 단축키: {hk}\n" +
                  $"받는 곳: {Texts.EndpointHost(_study)} (암호화 전송)\n" +
                  $"이 PC의 기록 폴더: {_opt.DataDir}\n" +
                  $"보관 기한: {_study.RetentionUntil}\n" +
                  $"문의: {_study.Contact}\n\n" +
                  "키보드 입력·화면·창 제목·파일 내용·인터넷 사용 기록은 수집하지 않아요.\n" +
                  "자세한 안내는 프로그램 폴더의 \"읽어주세요.txt\" 를 보세요.";
        MessageBox.Show(msg, $"{Texts.AppTitle} — 참여 정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <param name="auto">시험 전용(루프백 설정에서만): 창 없이 두 선택지 모두 켠 것으로 진행.</param>
    private async Task Withdraw(bool auto = false)
    {
        using var form = new WithdrawForm(_state.ParticipantId);
        if (!auto && form.ShowDialog() != DialogResult.OK) return;
        // 순서(10/29 검토 지적): 수집·전송을 먼저 멈춘다 → 그다음 삭제 요청 → 그다음 로컬 삭제.
        // 그래야 삭제 요청 "뒤에" 덤프가 올라가 서버 폴더가 다시 생기거나, 로컬 삭제 뒤 덤프 폴더가 다시 생기지 않는다.
        _withdrawing = true;
        Cursor.Current = Cursors.WaitCursor;
        await StopAllAsync(flush: false);
        string requestNote = "";
        if (form.RequestServerDelete)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var r = await _uploads.SendWithdrawAsync(deleteServerData: true, cts.Token);
                requestNote = r.Outcome == SendOutcome.Ok
                    ? "연구팀에 삭제 요청을 보냈어요."
                    : $"삭제 요청을 보내지 못했어요({r.Error}). 연구팀({_study.Contact})에 참여 ID {_state.ParticipantId} 를 알려 주세요.";
            }
            catch (Exception ex) { requestNote = $"삭제 요청을 보내지 못했어요({ex.Message}). 연구팀에 참여 ID {_state.ParticipantId} 를 알려 주세요."; }
        }
        try { StartupShortcut.Remove(_opt.StartupDir); } catch { }
        var pid = _state.ParticipantId;
        if (form.DeleteLocal) LocalData.DeleteAll(_opt.DataDir);
        else { _state.ConsentVersion = 0; _state.Autostart = false; SaveState(); }
        _uploads.Dispose();
        OpsLog.Write($"[withdraw] 완료 — {requestNote}");
        if (auto) Console.Error.WriteLine($"[withdraw] {requestNote}");
        else MessageBox.Show($"참여를 그만뒀어요. 자동 실행도 꺼졌어요.\n{requestNote}\n\n프로그램 폴더는 지금 지우셔도 됩니다.\n참여 ID: {pid}",
            Texts.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        ExitThread();
    }

    private void ExitFromMenu() => Shutdown(confirm: true);

    private async void Shutdown(bool confirm)
    {
        if (confirm)
        {
            var auto = StartupShortcut.Exists(_opt.StartupDir) ? "다음에 Windows에 로그인하면 다시 켜져요." : "다시 켜려면 PcBlackBox.exe 를 실행해 주세요.";
            if (MessageBox.Show($"끄면 그동안은 문제 상황이 기록되지 않아요.\n{auto}\n\n끌까요?", Texts.AppTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        if (await StopAllAsync(flush: true)) _uploads.Dispose();
        ExitThread();
    }

    /// <summary>
    /// 수집·전송 정지. UI 스레드를 막지 않는다(await). 순서: 질문 창 닫기 → 아이콘 숨김 → 단축키 해제 → 전송 반복 정지
    /// → 진행 중인 덤프 마무리(최대 40초) → (flush) 방금 생긴 것 한 번 보내기. 반환: 이번 호출이 정지를 수행했는가.
    /// </summary>
    private async Task<bool> StopAllAsync(bool flush)
    {
        if (_exiting) return false;
        _exiting = true;
        // 열린 질문 창을 먼저 닫아 기다리던 마킹이 바로 덤프되게 한다(고른 내용은 그대로)
        try { _openReport?.Close(); } catch { }
        _tray.Visible = false;
        _fgTimer.Stop();
        SystemEvents.SessionEnded -= OnSessionEnded;
        try { _hotkey.Dispose(); } catch { }
        await _uploads.StopLoopAsync();
        var stop = Task.Run(async () =>
        {
            _host.Stop(TimeSpan.FromSeconds(40));
            if (flush) await _uploads.FlushAsync(TimeSpan.FromSeconds(15));
        });
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(60)));
        return true;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // 로그오프·종료가 확정됨: 기다리지 않는다(대기열 파일은 원자적으로 써서 다음 실행에 이어 보낸다)
        _exiting = true;
        try { _tray.Visible = false; } catch { }
    }

    // ───────── 표시 ─────────

    private void RefreshMenu()
    {
        UpdateTrayText();
        _pauseItem.Checked = _state.UploadsPaused;
        _pauseItem.Text = _state.UploadsPaused ? "전송 일시중지됨 (눌러서 재개)" : "전송 일시중지";
        _autostartItem.Checked = StartupShortcut.Exists(_opt.StartupDir);
    }

    private void UpdateTrayText()
    {
        if (_exiting) return;
        var ob = _uploads.Outbox;
        int pending = ob.PendingCount, sent = ob.SentCount;
        string state = !_collectorReady ? "시작하는 중" : _state.UploadsPaused ? "기록 중 · 전송 일시중지" : "기록 중";
        var status = $"{state} · 기록 {_dumpsTotal} · 보냄 {sent}" + (pending > 0 ? $" · 대기 {pending}" : "");
        _statusItem.Text = status;
        var tip = $"PC 블랙박스 · {status}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _tray.Icon = _state.UploadsPaused ? AppIcons.Paused : AppIcons.Recording;
    }

    private void ShowBalloon(string title, string text, string? dumpDir)
    {
        if (_exiting) return;
        // 알림 폭주 방지 — 자동 감지 알림은 3분에 한 번
        if (dumpDir != null && DateTimeOffset.Now - _lastBalloon < BalloonGap) return;
        if (dumpDir != null) _lastBalloon = DateTimeOffset.Now;
        _balloonDumpDir = dumpDir;
        _tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
    }

    private void PruneLocal()
    {
        try
        {
            int n = LocalData.Prune(_dumpRoot, name => _uploads.Outbox.IsSent("dump", name), DateTimeOffset.Now);
            if (n > 0) OpsLog.Write($"[local] 오래된 기록 {n}건 정리(용량 상한)");
            int q = _uploads.TrimOutbox();
            if (q > 0) OpsLog.Write($"[local] 보내지 못한 오래된 봉투 {q}건 정리(대기열 상한)");
        }
        catch (Exception ex) { OpsLog.Write($"[local] 정리 실패: {ex.Message}"); }
    }

    private void SaveState()
    {
        try { _state.Save(_opt.DataDir); } catch (Exception ex) { OpsLog.Write($"[state] 저장 실패: {ex.Message}"); }
    }

    private void PostUi(Action a)
    {
        try { if (!_exiting && _ui.IsHandleCreated) _ui.BeginInvoke(a); } catch { /* 종료 중 */ }
    }

    private void After(int seconds, Action a)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); a(); };
        t.Start();
    }

    // ───────── 하트비트 ─────────

    private object HeartbeatPayload()
    {
        var me = Process.GetCurrentProcess();
        double uptimeSec = Math.Max(1, (DateTime.Now - me.StartTime).TotalSeconds);
        bool elevated;
        using (var id = WindowsIdentity.GetCurrent()) elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        var (bufCount, bufBytes) = _host.Buffer.Stats;
        return new
        {
            kind = "heartbeat",
            app = Version,
            at = DateTimeOffset.Now,
            os = Environment.OSVersion.VersionString,
            cpuCount = Environment.ProcessorCount,
            isElevated = elevated,
            launchedBy = _opt.Autostart ? "autostart" : "manual",
            startedAt = _host.StartedAt,
            uptimeMin = Math.Round(uptimeSec / 60, 1),
            collectorReady = _collectorReady,
            sources = _collectorReady ? _host.Statuses.ToList() : null, // 시작 중에는 다른 스레드가 목록에 추가하는 중
            replayedSinceBoot = _host.ReplayedSinceBoot,
            dumps = new { total = _dumpsTotal, auto = _dumpsAuto, manual = _dumpsManual, hidden = _dumpsHidden, onDisk = LocalData.DumpDirs(_dumpRoot).Count },
            outbox = new { pending = _uploads.Outbox.PendingCount, sent = _uploads.Outbox.SentCount, failed = _uploads.Outbox.FailedCount, lastError = _uploads.LastError },
            self = new
            {
                cpuPctOfMachine = Math.Round(me.TotalProcessorTime.TotalSeconds / uptimeSec / Environment.ProcessorCount * 100, 3),
                privateMB = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1),
                workingSetMB = Math.Round(me.WorkingSet64 / 1048576.0, 1),
                threads = me.Threads.Count,
                handles = me.HandleCount,
            },
            buffer = new { events = bufCount, approxKB = Math.Round(bufBytes / 1024.0, 1), rollupMinutes = _host.Rollup.Count },
            nickname = _state.Nickname,
            consentVersion = _state.ConsentVersion,
            autostart = _state.Autostart,
            uploadsPaused = _state.UploadsPaused,
            log = OpsLog.Tail(80),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _fgTimer.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
