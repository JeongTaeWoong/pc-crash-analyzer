using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CrashAnalyzer.UI.Models;
using CrashAnalyzer.UI.Services;
using Microsoft.Win32;

namespace CrashAnalyzer.UI;

/// <summary>
/// 메인 창 — 2026-11-02 확정 시안 2-4 구성 (2026-10-30 Python 시안과 같은 동작. Python 시안은 2026-11-02 에 지움).
/// UI 는 판단하지 않는다. 층·후보 확률·점검 순서·실행 방식은 모두 엔진(crash_engine tool ...)이 돌려준 값을 그대로 보여 준다.
/// 층별 '주의/관찰/양호'는 엔진의 층 확률을 말로 바꾼 표시일 뿐이다 (1위 층 = 주의, 20% 이상 = 관찰, 그 밖 = 양호).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] StepNames = { "진단", "원인 확인", "해결 안내", "검증" };
    private static readonly string[] LayerKeys = { "SW", "DRIVER", "HW" };

    private readonly string? _repoRoot;
    private EngineClient? _engine;
    private CollectorHost? _collector;
    private SessionService? _session;
    private readonly ObservableCollection<string> _activity = new();
    private bool _diagnosedOnce;          // 리허설·촬영 모드: 자동 진단 완료 감지
    public bool SuppressDialogs { get; set; } // 리허설(오프스크린)에서 MessageBox 차단

    // 지금 열린 기록
    private JsonNode? _diag;              // diagnose 결과
    private JsonNode? _view;              // 세션 상태 (session_status)
    private string? _sourcePath;          // 사용자가 연 원본 (수집기 덤프면 같은 폴더에 events.json · meta.json)
    private string? _snapshotPath;        // 엔진에 넘긴 스냅샷 (증상을 채운 사본일 수 있음)
    private string _mode = "guide";
    private int _selectedCand;
    private string _lastStatusMessage = "";

    // 블랙박스 기록 페이지
    private string _page = "main";
    private BlackBoxData? _blackbox;
    private readonly List<BlackBoxItem> _bbItems = new();

    // 상담 도우미
    private readonly ChatWindow _chat;
    private IChatAgent? _agent;
    private bool _chatBusy;
    private bool _chatGreetedEmpty;
    private string? _chatReason;          // 상담 창을 자동으로 연 이유 (같은 이유로 두 번 열지 않게)
    private BitmapSource? _chatImage;

    public MainWindow()
    {
        InitializeComponent();
        icActivity.ItemsSource = _activity;

        _chat = new ChatWindow();
        _chat.SendRequested += OnChatSend;
        _chat.VisibilityToggled += UpdateChatButton;
        _chatImage = LoadChatImage();
        _chat.SetHeadImage(_chatImage);
        if (_chatImage != null)
        {
            imgChat.Source = _chatImage;
        }
        else
        {
            imgChat.Visibility = Visibility.Collapsed;
            txtChatLabel.Visibility = Visibility.Collapsed;
            bdChatFallback.Visibility = Visibility.Visible;
        }

        _repoRoot = RepoPaths.FindRepoRoot();
        if (_repoRoot is null)
        {
            SetStatus("저장소 루트(engine+collector 폴더)를 찾지 못했습니다 — 저장소 안에서 실행하세요");
        }
        else
        {
            try
            {
                _engine = new EngineClient(RepoPaths.ResolveEnginePath(_repoRoot));
                _session = new SessionService(_engine);
                SetStatus("엔진 연결됨");
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }

            _collector = new CollectorHost(_repoRoot);
            _collector.OutputLine += line => Dispatcher.Invoke(() => AppendLog(line));
            _collector.NewDump += path => Dispatcher.Invoke(() => OnNewDump(path));
            _collector.Exited += code => Dispatcher.Invoke(() =>
            {
                SetCollectorState(false, $"종료됨 (코드 {code})");
                Activity($"수집기 종료 (코드 {code})");
            });
        }

        using (var identity = WindowsIdentity.GetCurrent())
        {
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            txtElevation.Text = elevated ? "관리자 모드" : "비관리자 — ETW·모듈 추적 제한 (관리자로 실행하면 더 많이 기록해요)";
        }

        Closing += (_, _) =>
        {
            _collector?.Dispose();
            _chat.ReallyClose = true;
            _chat.Close();
        };

        ShowPage("main");
        RefreshScreen();
    }

    // ═══════════════════════ 수집기 ═══════════════════════

    private void OnCollectorStart(object sender, RoutedEventArgs e)
    {
        if (_collector is null) { SetStatus("저장소 루트를 찾지 못해 수집기를 시작할 수 없습니다"); return; }
        try
        {
            _collector.Start();
            SetCollectorState(true, "수집 중 — 크래시 대기");
            AppendLog($"=== 수집기 시작 {DateTime.Now:HH:mm:ss} ===");
            Activity("수집기 시작");
            SetStatus("수집기 가동 중 — 크래시가 감지되면 자동으로 기록이 생깁니다");
        }
        catch (Exception ex)
        {
            if (!SuppressDialogs) MessageBox.Show(this, ex.Message, "수집기 시작 실패");
            SetStatus("수집기 시작 실패: " + ex.Message);
        }
    }

    private void OnCollectorStop(object sender, RoutedEventArgs e)
    {
        if (_collector is null) return;
        _collector.Stop();
        SetCollectorState(false, "꺼짐");
        AppendLog($"=== 수집기 중지 {DateTime.Now:HH:mm:ss} ===");
        Activity("수집기 중지");
    }

    private void SetCollectorState(bool running, string text)
    {
        Brush dot = running ? Ui.Hex("#2F9E63") : Ui.Hex("#B9C2CF");
        dotCollector.Fill = dot;
        dotCollectorHeader.Fill = dot;
        txtCollectorState.Text = text;
        txtCollectorState.Foreground = running ? Ui.Hex("#1E6B42") : (Brush)FindResource("TextDim");
        txtCollectorHeader.Text = running ? "수집 중" : "수집기 꺼짐";
        btnCollectorStart.IsEnabled = !running;
        btnCollectorStop.IsEnabled = running;
    }

    private void OnNewDump(string path)
    {
        string name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? path;
        AppendLog($"[새 덤프] {path}");
        Activity($"새 기록: {name}");
        SetStatus($"새 기록 감지: {name}");
        if (chkAutoDiagnose.IsChecked == true)
            LoadSnapshot(path);
    }

    private void Activity(string text)
    {
        txtNoActivity.Visibility = Visibility.Collapsed;
        _activity.Insert(0, $"{DateTime.Now:HH:mm}  {text}");
        while (_activity.Count > 6) _activity.RemoveAt(_activity.Count - 1);
    }

    // ═══════════════════════ 시연·검수용 실행 모드 (v1 그대로) ═══════════════════════

    /// <summary>--open 인자용: 창이 뜬 직후 스냅샷 자동 진단.</summary>
    public void OpenSnapshotOnStartup(string path) => LoadSnapshot(path);

    /// <summary>
    /// --rehearse 인자용: 수집기 기동 → (외부에서 크래시 유발) → 새 덤프 자동 진단까지의
    /// 녹화 시나리오 전체를 오프스크린으로 검증하고 결과를 파일로 남긴다.
    /// </summary>
    public async void StartRehearse(string outPng, string outTxt, int timeoutSec = 90)
    {
        var lines = new List<string>();
        try
        {
            OnCollectorStart(this, new RoutedEventArgs());
            lines.Add("collector_started=" + (_collector?.IsRunning ?? false));

            var t0 = DateTime.UtcNow;
            while (!_diagnosedOnce && (DateTime.UtcNow - t0).TotalSeconds < timeoutSec)
                await Task.Delay(500);

            lines.Add("auto_diagnosed=" + _diagnosedOnce);
            lines.Add("trigger_in_log=" + txtLog.Text.Contains("[trigger]"));
            lines.Add("mark_in_log=" + txtLog.Text.Contains("[mark]"));
            lines.Add("dump_in_log=" + txtLog.Text.Contains("[dump]"));
            lines.Add("header=" + txtHeaderInfo.Text);
            lines.Add("activity=" + string.Join(" | ", _activity));

            _collector?.Stop();
            ShowPage("main");
            await Task.Delay(600);
            SaveWindowPng(outPng);
        }
        catch (Exception ex)
        {
            lines.Add("error=" + ex.Message);
        }
        File.WriteAllLines(outTxt, lines);
        Application.Current.Shutdown(_diagnosedOnce ? 0 : 1);
    }

    /// <summary>
    /// --record-demo 인자용: 수집 시작 → 블랙박스 기록 페이지의 수집기 로그 탭을 자동으로 띄우고,
    /// 크래시 자동 진단(검진 결과 페이지 자동 전환 포함)이 끝나면 marker 파일을 쓴다.
    /// </summary>
    public async void StartRecordDemo(string markerPath)
    {
        await Task.Delay(800);
        OnCollectorStart(this, new RoutedEventArgs());
        await Task.Delay(1800);
        ShowPage("bb");
        tabsBlackBox.SelectedIndex = 1; // 수집기 로그 탭 — [trigger]/[mark]/[dump]가 라이브로 보이도록

        var t0 = DateTime.UtcNow;
        while (!_diagnosedOnce && (DateTime.UtcNow - t0).TotalSeconds < 120)
            await Task.Delay(300);
        // 진단 성공 시 LoadSnapshot 이 검진 결과 페이지로 전환해 둔 상태
        try { await File.WriteAllTextAsync(markerPath, _diagnosedOnce ? "ok" : "timeout"); } catch { /* 마커 실패는 치명 아님 */ }
    }

    /// <summary>--shot 인자용: 창 내용을 PNG로 렌더 (사용자 화면 캡처 없이 자가 검수).</summary>
    public void SaveWindowPng(string outPath)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 10 || size.Height < 10) return;
        var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    // ═══════════════════════ 기록 열기 → 검진 ═══════════════════════

    private void OnOpenLatestDump(object sender, RoutedEventArgs e)
    {
        if (_repoRoot is null) return;
        var path = DumpLocator.FindLatestEngineSnapshot(RepoPaths.ResolveDumpsRoot(_repoRoot));
        if (path is null)
        {
            MessageBox.Show(this,
                "수집기 기록(engine-snapshot.json)을 찾지 못했어요.\n" +
                "수집기를 켜 두고 Ctrl+Shift+F12 로 기록하거나, [파일 열기…]로 직접 고르세요.\n" +
                "(기록 폴더가 다른 곳이면 CE_DUMPS 환경 변수로 알려 주세요)",
                "최근 기록 없음");
            return;
        }
        LoadSnapshot(path);
    }

    private void OnOpenSnapshotFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "기록 파일 열기",
            Filter = "스냅샷 JSON|*.json|모든 파일|*.*",
            InitialDirectory = _repoRoot is null ? "" : System.IO.Path.Combine(_repoRoot, "engine", "data", "samples"),
        };
        if (dlg.ShowDialog(this) == true)
            LoadSnapshot(dlg.FileName);
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        string wanted = rbModeAuto.IsChecked == true ? "auto" : "guide";
        if (wanted == _mode) return;
        if (_sourcePath != null)
        {
            var answer = MessageBox.Show(this, "실행 방식을 바꾸면 처음부터 다시 검진해요. 계속할까요?", "실행 방식 변경",
                MessageBoxButton.YesNo);
            if (answer != MessageBoxResult.Yes)
            {
                rbModeGuide.IsChecked = _mode == "guide";
                rbModeAuto.IsChecked = _mode == "auto";
                return;
            }
            _mode = wanted;
            LoadSnapshot(_sourcePath);
            return;
        }
        _mode = wanted;
        UpdateStatusBar();
    }

    private async void LoadSnapshot(string path)
    {
        if (_engine is null || _session is null) { SetStatus("엔진이 준비되지 않았습니다"); return; }
        try
        {
            // 엔진은 자기 폴더(engine\build\Release)에서 실행되므로, 상대 경로(--open engine\data\...)는
            // UI 를 실행한 폴더 기준의 전체 경로로 바꿔서 넘긴다. (2026-11-02 수정)
            path = System.IO.Path.GetFullPath(path);

            // 수동 마킹 덤프는 증상이 unknown — UI가 묻고, 원본은 두고 채운 사본으로 진단 (스키마 명세)
            string usePath = path;
            var snap = JsonNode.Parse(await File.ReadAllTextAsync(path));
            string symType = snap?["symptom"]?["type"]?.GetValue<string>() ?? "";
            if (symType is "" or "unknown")
            {
                var answer = SymptomDialog.Ask(this, snap?["symptom"]?["target_process"]?.GetValue<string>() ?? "");
                if (answer is null) return;
                var sym = snap!["symptom"] as JsonObject ?? new JsonObject();
                sym["type"] = answer.Type;
                sym["scope"] = answer.Scope;
                sym["target_process"] = answer.TargetProcess;
                snap["symptom"] = sym;
                usePath = System.IO.Path.Combine(RepoPaths.SessionDir(),
                    $"patched_{DateTime.Now:HHmmss}_{System.IO.Path.GetFileName(path)}");
                await File.WriteAllTextAsync(usePath, snap.ToJsonString());
            }

            SetStatus("검진 중…");
            string mode = _mode;
            var engine = _engine;
            var session = _session;
            var diag = await Task.Run(() => engine.CallTool("diagnose",
                new JsonObject { ["snapshot_path"] = usePath, ["mode"] = mode }));
            var view = await Task.Run(() => session.Start(usePath, mode));

            _diag = diag;
            _view = view;
            _sourcePath = path;
            _snapshotPath = usePath;
            _selectedCand = 0;
            _diagnosedOnce = true;
            txtAppliedAt.Text = "";

            // 새 기록이면 세션이 바뀌므로 상담도 새로 시작
            _agent = null;
            _chatReason = null;
            _chat.ClearLines();
            if (_chat.IsVisible) EnsureAgent();

            _blackbox = BlackBoxData.Load(_sourcePath, _snapshotPath);
            RefreshScreen();
            ShowPage("main");
            Activity($"검진 완료: {System.IO.Path.GetFileName(path)}");
            SetStatus($"검진 완료 · {path}");
        }
        catch (Exception ex)
        {
            SetStatus("검진 실패: " + ex.Message);
            if (!SuppressDialogs) MessageBox.Show(this, ex.Message, "검진 실패");
        }
    }

    // ═══════════════════════ 세션: 점검 → 해결 → 검증 ═══════════════════════

    /// <summary>엔진 세션 도구를 작업 스레드에서 부르고, 끝나면 최신 상태로 화면을 다시 그린다.</summary>
    private async Task<JsonNode?> RunSession(Func<SessionService, JsonNode> call)
    {
        if (_session?.SessionPath is null) return null;
        var session = _session;
        try
        {
            SetStatus("엔진 호출 중…");
            var result = await Task.Run(() => call(session));
            _view = await Task.Run(() => session.Status());
            RefreshScreen();
            SetStatus("완료");
            return result;
        }
        catch (Exception ex)
        {
            SetStatus("엔진 오류: " + ex.Message);
            if (!SuppressDialogs) MessageBox.Show(this, ex.Message, "엔진 오류");
            return null;
        }
    }

    private async void OnReportSolved(object s, RoutedEventArgs e) => await Report("solved");
    private async void OnReportNotSolved(object s, RoutedEventArgs e) => await Report("not_solved");
    private async void OnReportSkipped(object s, RoutedEventArgs e) => await Report("skipped");

    private async Task Report(string result)
    {
        var nc = _view?["next_check"];
        if (nc is null) return;
        string checkId = Str(nc["check_id"]);
        if (await RunSession(s => s.Report(checkId, result)) != null)
            AutoOpenChat();
    }

    /// <summary>"왜 이게 먼저예요?" — 엔진의 explain 결과를 그대로 보여 준다.</summary>
    private async void OnWhy(object sender, RoutedEventArgs e)
    {
        var nc = _view?["next_check"];
        if (nc is null || _session is null) return;
        string candidateId = nc["covers"] is JsonArray covers && covers.Count > 0 ? Str(covers[0]) : "";
        string reason = "이 점검은 약 " + Ui.DurationText(Num(nc["cost_seconds"])) + " 걸리고, 원인이 드러날 확률이 " +
                        Ui.Pct(Num(nc["chance_to_reveal_cause"])) + "예요.\n" +
                        "엔진은 '걸리는 시간 대비 원인을 가를 수 있는 정도'가 큰 점검부터 고릅니다(비용 가중 순서).";
        string text = "";
        try
        {
            var session = _session;
            var ex = await Task.Run(() => session.Explain(candidateId));
            var lines = new List<string> { Str(ex["label"]) + " 을(를) 의심하는 근거" };
            if (ex["reasons"] is JsonArray reasons)
                for (int i = 0; i < reasons.Count && i < 5; i++)
                    lines.Add("  · " + Str(reasons[i]?["sentence"]));
            text = string.Join("\n", lines);
        }
        catch (Exception)
        {
            text = "";
        }
        MessageBox.Show(this, reason + (text.Length > 0 ? "\n\n" + text : ""), "왜 이게 먼저예요?");
    }

    private async void OnApplyFix(object sender, RoutedEventArgs e)
    {
        if (Str(_view?["status"]) != "confirmed")
        {
            MessageBox.Show(this, "원인이 확인된 뒤에 해결책을 적용할 수 있어요.", "안내");
            return;
        }
        string remedyId = lvRemedies.SelectedItem is RemedyRow r ? r.RemedyId : "";
        string appliedAt = txtAppliedAt.Text.Trim();
        await RunSession(s => s.ApplyFix(remedyId, appliedAt));
        Activity("해결책 적용 기록");
    }

    private async void OnVerifyAfterSnapshot(object sender, RoutedEventArgs e)
    {
        if (Str(_view?["status"]) != "fixing")
        {
            MessageBox.Show(this, "해결책을 적용한 뒤에 확인할 수 있어요.", "안내");
            return;
        }
        var dlg = new OpenFileDialog
        {
            Title = "적용 후 기록 열기",
            Filter = "스냅샷 JSON|*.json",
            InitialDirectory = System.IO.Path.GetDirectoryName(_snapshotPath) ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;
        string after = dlg.FileName;
        if (await RunSession(s => s.VerifyAfter(after)) != null)
            AutoOpenChat();
    }

    private async void OnVerifyAnswer(object sender, RoutedEventArgs e)
    {
        if (Str(_view?["status"]) != "fixing")
        {
            MessageBox.Show(this, "해결책을 적용한 뒤에 확인할 수 있어요.", "안내");
            return;
        }
        double minutes = double.TryParse(txtObservedMinutes.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var m) ? m : 0;
        bool recurred = rbRecur.IsChecked == true;
        int whole = (int)Math.Round(minutes);
        if (await RunSession(s => s.VerifyAnswer(recurred, whole)) != null)
            AutoOpenChat();
    }

    // ═══════════════════════ 화면 갱신 ═══════════════════════

    private void RefreshScreen()
    {
        FillHeader();
        DrawStepper();
        DrawRing();
        FillStates();
        FillRankNote();
        FillRank();
        FillEvidence();
        FillProgress();
        FillCheck();
        FillFix();
        FillTodo();
        UpdateChatButton();
        FillBlackBox();
        UpdateStatusBar();
    }

    private void ShowPage(string name)
    {
        _page = name;
        bool onMain = name == "main";
        pageMain.Visibility = onMain ? Visibility.Visible : Visibility.Collapsed;
        pageBlackBox.Visibility = onMain ? Visibility.Collapsed : Visibility.Visible;
        rbNavMain.IsChecked = onMain;
        rbNavBlackBox.IsChecked = !onMain;
        if (!onMain) FillBlackBox();
    }

    private void OnNavMain(object sender, RoutedEventArgs e) => ShowPage("main");
    private void OnNavBlackBox(object sender, RoutedEventArgs e) => ShowPage("bb");

    private void FillHeader()
    {
        if (_diag is null) return;
        string sym = Ui.SymptomKo(Str(_diag["symptom"]));
        string target = Str(_diag["target_process"]);
        if (target.Length == 0) target = "PC 전체";
        var parts = new List<string> { target + " " + sym };
        var bb = _blackbox;
        if (bb?.Mark is DateTime mark) parts.Add(mark.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture));
        if (bb?.Source == "collector")
            parts.Add(bb.MetaKind == "manual" || bb.ViaHotkey ? "단축키로 남긴 기록이에요" : "블랙박스가 자동으로 기록했어요");
        else if (bb?.Source == "snapshot")
            parts.Add("실험용 시나리오 " + Str(_diag["snapshot_id"]));
        if (_view != null) parts.Add("상태: " + Str(_view["status_text"]));
        txtHeaderInfo.Text = string.Join("   ·   ", parts);
        txtHeaderInfo.Foreground = Ui.StrongFg;
        txtCollectLabel.Text = bb?.Source == "collector" ? "● 블랙박스 기록 있음" : "";
    }

    private void UpdateStatusBar()
    {
        var parts = new List<string>();
        if (_engine != null) parts.Add("엔진: " + _engine.ExePath);
        if (_session?.SessionPath != null) parts.Add("세션: " + _session.SessionPath);
        parts.Add("모드: " + (_mode == "auto" ? "자동 점검" : "안내"));
        if (_lastStatusMessage.Length > 0) parts.Add(_lastStatusMessage);
        txtStatus.Text = string.Join("   ·   ", parts);
    }

    private void SetStatus(string s)
    {
        _lastStatusMessage = s;
        UpdateStatusBar();
    }

    // ─── 단계 표시 ① 진단 ② 원인 확인 ③ 해결 안내 ④ 검증 ───

    private static int StatusStep(string status) => status switch
    {
        "checking" => 1,
        "exhausted" => 1,
        "confirmed" => 2,
        "fixing" => 3,
        "failed" => 3,
        "resolved" => 4,
        _ => 1,
    };

    private void OnStepperSize(object sender, SizeChangedEventArgs e) => DrawStepper();

    private void DrawStepper()
    {
        var c = cvStepper;
        c.Children.Clear();
        double width = c.ActualWidth;
        if (width < 50) return;
        int current = _view != null ? StatusStep(Str(_view["status"])) : 0;
        Brush gray = Ui.Hex("#C3CEC8");
        int n = StepNames.Length;
        double seg = (width - 40) / n;
        for (int i = 0; i < n; i++)
        {
            double x = 20 + seg * i + seg / 2;
            bool done = _diag != null && (i < current || (i == 3 && current == 4));
            bool active = _diag != null && i == current && current < 4;
            Brush color = done || active ? Ui.Accent : gray;
            if (i > 0)
            {
                double px = 20 + seg * (i - 1) + seg / 2;
                c.Children.Add(new Line { X1 = px + 15, Y1 = 16, X2 = x - 15, Y2 = 16, Stroke = color, StrokeThickness = 2 });
            }
            var dot = new Ellipse
            {
                Width = 24, Height = 24, StrokeThickness = 2, Stroke = color,
                Fill = done ? Ui.Accent : (active ? Ui.AccentSoft : Brushes.White),
            };
            Canvas.SetLeft(dot, x - 12);
            Canvas.SetTop(dot, 4);
            c.Children.Add(dot);
            AddCenteredText(c, done ? "✓" : (i + 1).ToString(), x, 7, 13, FontWeights.Bold, done ? Brushes.White : color);
            AddCenteredText(c, StepNames[i], x, 31, active ? 13 : 12, active ? FontWeights.SemiBold : FontWeights.Normal,
                done || active ? Ui.StrongFg : Ui.DimFg);
        }
    }

    private static void AddCenteredText(Canvas c, string text, double cx, double top, double size, FontWeight weight, Brush fg)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = fg,
            Width = 160, TextAlignment = TextAlignment.Center,
        };
        Canvas.SetLeft(tb, cx - 80);
        Canvas.SetTop(tb, top);
        c.Children.Add(tb);
    }

    // ─── 가장 유력한 원인 (원형 그래프) ───

    /// <summary>지금 보여 줄 후보: 세션이 있으면 점검 결과가 반영된 확률(belief), 없으면 진단 결과.</summary>
    private JsonArray CandidatesNow()
    {
        if (_view?["belief"] is JsonArray belief && belief.Count > 0) return belief;
        if (_diag?["candidates"] is JsonArray cands) return cands;
        return new JsonArray();
    }

    /// <summary>진단 결과에서 id 가 같은 후보(근거·제품 이름이 들어 있음).</summary>
    private JsonNode? DiagCandidate(string id)
    {
        if (_diag?["candidates"] is JsonArray dc)
            for (int i = 0; i < dc.Count; i++)
                if (Str(dc[i]?["id"]) == id) return dc[i];
        return null;
    }

    /// <summary>후보 표시 이름: 진단 결과의 제품 이름(product)이 있으면 그것, 없으면 표시명.</summary>
    private string NameOf(JsonNode? cand)
    {
        var full = DiagCandidate(Str(cand?["id"]));
        if (Str(full?["product"]).Length > 0) return Str(full?["product"]);
        if (Str(cand?["product"]).Length > 0) return Str(cand?["product"]);
        return Str(cand?["label"]);
    }

    private void DrawRing()
    {
        var c = cvRing;
        c.Children.Clear();
        const double cx = 98, cy = 98;
        Brush track = Ui.Hex("#D9E4DE");
        AddCircle(c, cx, cy, 86, 16, track);
        AddCircle(c, cx, cy, 64, 6, track);
        var cands = CandidatesNow();
        if (cands.Count == 0)
        {
            txtRingCaption.Text = "";
            txtRingPct.Text = "검진 전";
            txtRingPct.FontSize = 17;
            txtRingPct.Foreground = Ui.FaintFg;
            txtRingName.Text = "";
            txtRingSub.Text = "";
            return;
        }
        double p1 = Num(cands[0]?["probability"]);
        if (p1 > 0.001) AddArc(c, cx, cy, 86, 16, p1, Ui.Accent);
        if (cands.Count > 1)
        {
            double p2 = Num(cands[1]?["probability"]);
            if (p2 > 0.001) AddArc(c, cx, cy, 64, 6, p2, Ui.Accent2);
        }
        bool confirmed = _view?["confirmed"] is JsonObject;
        txtRingCaption.Text = confirmed ? "확인된 원인" : "가장 유력한 원인";
        txtRingPct.Text = Ui.Pct(p1);
        txtRingPct.FontSize = 30;
        txtRingPct.Foreground = Ui.Accent;
        txtRingName.Text = Ui.Short(NameOf(cands[0]), 16);
        txtRingSub.Text = confirmed ? "원인 확인됨" : "아직 확정 전";
    }

    private static void AddCircle(Canvas c, double cx, double cy, double r, double thick, Brush stroke)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Stroke = stroke, StrokeThickness = thick };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        c.Children.Add(e);
    }

    /// <summary>12시 방향에서 시계 방향으로 p(0~1)만큼 호를 그린다.</summary>
    private static void AddArc(Canvas c, double cx, double cy, double r, double thick, double p, Brush stroke)
    {
        double sweep = 359.9 * Math.Min(1.0, p);
        double rad = sweep * Math.PI / 180.0;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        c.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = stroke, StrokeThickness = thick });
    }

    // ─── 층별 상태 (주의 / 관찰 / 양호) ───

    private void FillStates()
    {
        var probs = _diag?["layer"]?["probability"] as JsonObject;
        string top = Str(_diag?["layer"]?["top"]);
        for (int i = 0; i < LayerKeys.Length; i++)
        {
            string k = LayerKeys[i];
            var (val, prob) = StateLabels(k);
            if (probs is null)
            {
                val.Text = "-";
                val.Foreground = Ui.FaintFg;
                prob.Text = "";
                continue;
            }
            double p = Num(probs[k]);
            string word = k == top ? "주의" : (p >= 0.2 ? "관찰" : "양호");
            val.Text = word;
            val.Foreground = word switch
            {
                "주의" => Ui.Hex("#A23B1E"),
                "관찰" => Ui.Hex("#8A5E00"),
                _ => Ui.Accent,
            };
            prob.Text = "엔진 판정 " + Ui.Pct(p);
            prob.Foreground = Ui.DimFg;
        }
    }

    private (TextBlock Val, TextBlock Prob) StateLabels(string layer) => layer switch
    {
        "SW" => (txtStateSW, txtProbSW),
        "DRIVER" => (txtStateDRIVER, txtProbDRIVER),
        _ => (txtStateHW, txtProbHW),
    };

    private void FillRankNote()
    {
        var cands = CandidatesNow();
        var lines = new List<string>();
        if (cands.Count > 1)
        {
            var parts = new List<string>();
            for (int i = 1; i < Math.Min(3, cands.Count); i++)
                parts.Add((i + 1) + "순위 " + Ui.Short(NameOf(cands[i]), 18) + " " + Ui.Pct(Num(cands[i]?["probability"])));
            lines.Add(string.Join(" · ", parts));
        }
        var rejected = RejectedTargets();
        if (rejected.Count > 0)
        {
            string more = rejected.Count <= 3 ? "" : " 외 " + (rejected.Count - 3) + "개";
            lines.Add("아님: " + string.Join(", ", rejected.Take(3)) + more + " (점검해 보니 그대로였어요)");
        }
        if (_diag != null && cands.Count > 0)
        {
            int stage = (int)Num(cands[0]?["stage"]);
            lines.Add("층 판정 신뢰도 " + Ui.Pct(Num(_diag["layer"]?["confidence"])) + " · 1순위 범위: " + Ui.StageKo(stage));
        }
        txtRankNote.Text = string.Join("\n", lines);
    }

    // ─── 원인 후보 순위 ───

    private void FillRank()
    {
        var cands = CandidatesNow();
        txtRankEmpty.Visibility = cands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = new List<RankRow>();
        int n = Math.Min(4, cands.Count);
        for (int i = 0; i < n; i++)
            rows.Add(new RankRow
            {
                Index = i, Name = Ui.Short(NameOf(cands[i]), 22), Probability = Num(cands[i]?["probability"]),
                IsFirst = i == 0, IsSelected = i == _selectedCand,
            });
        double rest = 0;
        for (int i = n; i < cands.Count; i++) rest += Num(cands[i]?["probability"]);
        if (rest > 0.005)
            rows.Add(new RankRow { Index = -1, Name = "기타 " + (cands.Count - n) + "개", Probability = rest });
        icRank.ItemsSource = rows;
    }

    private void OnRankRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int index && index >= 0)
        {
            _selectedCand = index;
            FillRank();
            FillEvidence();
        }
    }

    private void FillEvidence()
    {
        var cands = CandidatesNow();
        if (cands.Count == 0 || _selectedCand >= cands.Count) { txtEvidence.Text = ""; return; }
        var full = DiagCandidate(Str(cands[_selectedCand]?["id"]));
        if (full is null) { txtEvidence.Text = ""; return; }

        var ev = new List<JsonNode>();
        if (full["evidence"] is JsonArray arr)
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] != null) ev.Add(arr[i]!);
        // 기여도(contribution) 큰 순으로
        ev.Sort((a, b) => Num(b["contribution"]).CompareTo(Num(a["contribution"])));

        var lines = new List<string> { "▸ " + Str(full["label"]) + " 근거" };
        for (int i = 0; i < ev.Count; i++)
        {
            string kind = Str(ev[i]["kind"]);
            if (kind is "prior" or "layer") continue;
            lines.Add("  · " + Str(ev[i]["detail"]));
            if (lines.Count >= 4) break;
        }
        if (lines.Count == 1) lines.Add("  · 직접 근거는 없고, 증상별 사전 비중만 있어요.");
        lines.Add("  (막대를 누르면 다른 후보의 근거를 볼 수 있어요)");
        txtEvidence.Text = string.Join("\n", lines);
    }

    // ─── 점검 진행 ───

    /// <summary>점검 기록(ref = '점검id@후보id')을 사람이 읽을 글로: (점검 이름, 대상 이름)</summary>
    private (string Check, string Target) CheckText(string reference)
    {
        var steps = _diag?["plan"]?["steps"] as JsonArray;
        if (steps != null)
            for (int i = 0; i < steps.Count; i++)
                if (Str(steps[i]?["check_id"]) == reference)
                    return (Str(steps[i]?["label"]), Str(steps[i]?["target"]));

        int at = reference.IndexOf('@');
        string check = at >= 0 ? reference.Substring(0, at) : reference;
        string target = at >= 0 ? reference.Substring(at + 1) : "";
        if (steps != null)
            for (int i = 0; i < steps.Count; i++)
            {
                string sid = Str(steps[i]?["check_id"]);
                int a2 = sid.IndexOf('@');
                if ((a2 >= 0 ? sid.Substring(0, a2) : sid) == check)
                {
                    check = Str(steps[i]?["label"]);
                    break;
                }
            }
        var cand = DiagCandidate(target);
        if (cand != null) target = Str(cand["label"]);
        return (check, target);
    }

    /// <summary>'그대로'로 끝난 점검의 대상 = 원인이 아닌 후보 (대상이 하나인 점검만)</summary>
    private List<string> RejectedTargets()
    {
        var output = new List<string>();
        if (_view?["history"] is not JsonArray hist) return output;
        for (int i = 0; i < hist.Count; i++)
        {
            var h = hist[i];
            string reference = Str(h?["ref"]);
            if (Str(h?["type"]) == "check" && Str(h?["result"]) == "not_solved" && reference.Contains('@'))
            {
                string name = CheckText(reference).Target;
                int paren = name.IndexOf(" (", StringComparison.Ordinal);
                if (paren >= 0) name = name.Substring(0, paren);
                name = Ui.Short(name, 16);
                if (name.Length > 0 && !output.Contains(name)) output.Add(name);
            }
        }
        return output;
    }

    private void FillProgress()
    {
        if (_diag is null)
        {
            txtProgressEmpty.Visibility = Visibility.Visible;
            icProgress.ItemsSource = null;
            txtProgressCount.Text = "";
            return;
        }
        txtProgressEmpty.Visibility = Visibility.Collapsed;

        var doneRows = new List<(string Text, string State, string Kind)>();
        var laterRows = new List<(string Text, string State, string Kind)>();
        var nowRows = new List<(string Text, string State, string Kind)>();
        var doneIds = new HashSet<string>();
        if (_view?["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
            {
                var h = hist[i];
                if (Str(h?["type"]) != "check") continue;
                string reference = Str(h?["ref"]);
                var lab = CheckText(reference);
                doneIds.Add(reference);
                doneRows.Add((lab.Target.Length > 0 ? lab.Check + " → " + lab.Target : lab.Check,
                    Ui.ResultKo(Str(h?["result"])), "done"));
            }
        int doneCount = doneRows.Count;

        var nc = _view?["next_check"];
        bool checking = _view != null && Str(_view["status"]) == "checking";
        if (nc != null && checking)
        {
            doneIds.Add(Str(nc["check_id"]));
            nowRows.Add((Str(nc["label"]) + " → " + Str(nc["target"]), "지금 (오른쪽)", "now"));
        }
        if ((checking || _view is null) && _diag["plan"]?["steps"] is JsonArray steps)
            for (int i = 0; i < steps.Count; i++)
            {
                if (doneIds.Contains(Str(steps[i]?["check_id"]))) continue;
                laterRows.Add((Str(steps[i]?["label"]) + " → " + Str(steps[i]?["target"]), "앞 점검이 그대로면", "later"));
            }

        // 카드 높이 안에 6줄까지: 끝난 점검이 많으면 앞쪽을 한 줄로 줄이고, 남은 점검은 자른다
        if (doneRows.Count > 3)
        {
            int hidden = doneRows.Count - 2;
            var shortened = new List<(string Text, string State, string Kind)> { ("앞의 점검 " + hidden + "개", "모두 끝남", "done") };
            for (int i = hidden; i < doneRows.Count; i++) shortened.Add(doneRows[i]);
            doneRows = shortened;
        }
        int room = Math.Max(0, 6 - doneRows.Count - nowRows.Count);
        var all = new List<(string Text, string State, string Kind)>();
        all.AddRange(doneRows);
        all.AddRange(nowRows);
        for (int i = 0; i < laterRows.Count && i < room; i++) all.Add(laterRows[i]);

        var rows = new List<ProgressRow>();
        for (int i = 0; i < all.Count; i++)
            rows.Add(new ProgressRow { Num = (i + 1).ToString(), Text = Ui.Short(all[i].Text, 54), State = all[i].State, Kind = all[i].Kind });
        icProgress.ItemsSource = rows;

        var plan = _diag["plan"];
        txtProgressCount.Text = doneCount + "개 끝 · 원인을 찾을 확률 " + Ui.Pct(Num(plan?["resolved_probability"])) +
                                " · 기대 시간 " + Ui.DurationText(Num(plan?["expected_cost_seconds"]));
    }

    // ─── 오른쪽 1: 지금 할 점검 ───

    private void FillCheck()
    {
        var v = _view;
        var nc = v?["next_check"];
        bool enabled = v != null && Str(v["status"]) == "checking" && nc != null;
        btnSolved.IsEnabled = enabled;
        btnNotSolved.IsEnabled = enabled;
        btnSkipped.IsEnabled = enabled;
        btnWhy.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (v is null)
        {
            txtNextTitle.Text = "아직 검진 전이에요";
            txtNextTarget.Text = "";
            txtNextAdvice.Text = "[최근 기록 열기]를 누르면 수집기가 남긴 가장 최근 기록으로 검진을 시작해요.\n" +
                                 "다른 기록이나 실험용 시나리오는 [파일 열기…]로 고를 수 있어요.";
            wpNextMeta.Visibility = Visibility.Collapsed;
            bdNextSteps.Visibility = Visibility.Collapsed;
            txtNextRollback.Text = "";
            txtNextReason.Text = "";
            return;
        }
        if (!enabled)
        {
            txtNextTitle.Text = Str(v["status"]) == "exhausted" ? "남은 점검이 없어요" : "원인 확인이 끝났어요";
            txtNextTarget.Text = Str(v["status_text"]);
            txtNextAdvice.Text = Str(v["next_action"]);
            wpNextMeta.Visibility = Visibility.Collapsed;
            bdNextSteps.Visibility = Visibility.Collapsed;
            txtNextRollback.Text = "";
            txtNextReason.Text = "";
            return;
        }

        var act = nc!["action"];
        int done = 0;
        if (v["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
                if (Str(hist[i]?["type"]) == "check") done++;
        txtNextTitle.Text = "점검 " + (done + 1) + " · " + Str(nc["label"]);
        txtNextTarget.Text = "대상: " + Str(nc["target"]);

        wpNextMeta.Visibility = Visibility.Visible;
        txtNextCost.Text = "약 " + Ui.DurationText(Num(nc["cost_seconds"]));
        txtNextChance.Text = "원인이 드러날 확률 " + Ui.Pct(Num(nc["chance_to_reveal_cause"]));
        var pill = Ui.ExecPill(Str(act?["execution"]));
        txtNextExec.Text = pill.Text;
        txtNextExec.Foreground = pill.Fg;
        bdNextExec.Background = pill.Bg;
        txtNextReboot.Visibility = Bool(act?["requires_reboot"]) ? Visibility.Visible : Visibility.Collapsed;

        txtNextAdvice.Text = Str(nc["advice"]);
        var steps = new List<string>();
        if (act?["steps"] is JsonArray st)
            for (int i = 0; i < st.Count; i++) steps.Add((i + 1) + ".  " + Str(st[i]));
        icNextSteps.ItemsSource = steps;
        bdNextSteps.Visibility = steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        txtNextRollback.Text = Str(act?["rollback"]).Length > 0 ? "↩ 되돌리기: " + Str(act?["rollback"]) : "↩ 되돌리기 어려운 조치예요.";
        txtNextReason.Text = Str(act?["reason"]).Length > 0 ? "실행 방식: " + Str(act?["reason"]) : "";
    }

    // ─── 오른쪽 2: 해결 방법 · 검증 (원인이 확인된 뒤) ───

    private void FillFix()
    {
        var v = _view;
        lvRemedies.ItemsSource = null;
        txtRemedyDetail.Inlines.Clear();
        if (v?["confirmed"] is not JsonObject c)
        {
            ShowVerify(null);
            return;
        }
        string status = Str(v["status"]);
        txtCauseTitle.Text = "원인: " + Str(c["label"]);
        var lines = new List<string> { Ui.StageKo((int)Num(c["stage"])) + " 범위" };
        if (c["evidence"] is JsonArray ev)
            for (int i = 0; i < ev.Count && lines.Count < 4; i++)
            {
                string kind = Str(ev[i]?["kind"]);
                if (kind is "prior" or "layer") continue;
                lines.Add("· " + Str(ev[i]?["detail"]));
            }
        if (Str(v["restore_after_check"]).Length > 0 && status == "confirmed")
            lines.Add("※ " + Str(v["restore_after_check"]));
        txtCauseDetail.Text = string.Join("\n", lines);

        string cur = Str(v["current_remedy"]);
        string applied = Str(v["applied_remedy"]);
        int curIndex = (int)Num(v["current_remedy_index"]);
        var rows = new List<RemedyRow>();
        RemedyRow? select = null;
        if (v["remedies"] is JsonArray rem)
            for (int i = 0; i < rem.Count; i++)
            {
                string id = Str(rem[i]?["remedy_id"]);
                string state;
                if (id == applied && (status == "fixing" || status == "resolved"))
                    state = status == "fixing" ? "적용함" : "해결됨";
                else if (id == cur) state = "추천";
                else if (i < curIndex) state = "재발";
                else state = "";
                var row = new RemedyRow
                {
                    Index = i, RemedyId = id, Label = Str(rem[i]?["label"]), Success = Num(rem[i]?["expected_success"]), State = state,
                };
                rows.Add(row);
                if (id == cur) select = row;
            }
        lvRemedies.ItemsSource = rows;
        if (select != null) lvRemedies.SelectedItem = select;
        else FillRemedyDetail();

        btnApply.IsEnabled = status == "confirmed";
        if (txtAppliedAt.Text.Trim().Length == 0)
            txtAppliedAt.Text = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        ShowVerify(v["last_verify"] as JsonObject);
    }

    private void OnRemedySelected(object sender, SelectionChangedEventArgs e) => FillRemedyDetail();

    private void FillRemedyDetail()
    {
        txtRemedyDetail.Inlines.Clear();
        if (lvRemedies.SelectedItem is not RemedyRow row) return;
        if (_view?["remedies"] is not JsonArray rem || row.Index >= rem.Count) return;
        var r = rem[row.Index];
        Brush sub = Ui.DimFg;
        txtRemedyDetail.Inlines.Add(new Run(Str(r?["label"]) + "\n") { FontWeight = FontWeights.SemiBold });
        if (Str(r?["description"]).Length > 0)
            txtRemedyDetail.Inlines.Add(new Run(Str(r?["description"]) + "\n") { Foreground = sub, FontSize = 12 });
        if (r?["actions"] is JsonArray acts)
            for (int k = 0; k < acts.Count; k++)
            {
                var a = acts[k];
                string exe = Ui.ExecPill(Str(a?["execution"])).Text;
                txtRemedyDetail.Inlines.Add(new Run("\n▸ " + Str(a?["label"]) + "  [" + exe + "]\n") { FontWeight = FontWeights.SemiBold });
                if (a?["steps"] is JsonArray st)
                    for (int s = 0; s < st.Count; s++)
                        txtRemedyDetail.Inlines.Add(new Run("   " + (s + 1) + ") " + Str(st[s]) + "\n"));
                if (Str(a?["rollback"]).Length > 0)
                    txtRemedyDetail.Inlines.Add(new Run("   되돌리기: " + Str(a?["rollback"]) + "\n") { Foreground = sub, FontSize = 12 });
                else if (a?["reversible"] is JsonValue rv && rv.TryGetValue<bool>(out bool reversible) && !reversible)
                    txtRemedyDetail.Inlines.Add(new Run("   되돌리기 어려운 조치예요. 실행 전에 한 번 더 확인하세요.\n") { Foreground = sub, FontSize = 12 });
            }
    }

    private void ShowVerify(JsonObject? ver)
    {
        bool fixing = Str(_view?["status"]) == "fixing";
        btnVerifySnap.IsEnabled = fixing;
        btnVerifyAnswer.IsEnabled = fixing;
        if (ver is null)
        {
            txtVerifyResult.Text = "해결책을 적용하고 한동안 쓴 뒤 확인해요. 적용 이후의 재발 신호만 세고, " +
                                   "60분 이상 써 봐야 '해결됨'이 돼요.";
            txtVerifyResult.Foreground = Ui.DimFg;
            return;
        }
        var style = Ui.OutcomeStyle(Str(ver["outcome"]));
        var lines = new List<string>
        {
            "결과: " + style.Text + " (관찰 " + (int)Num(ver["observed_minutes"]) + "분 · 재발 " + (int)Num(ver["recurrences"]) + "건)",
        };
        if (ver["details"] is JsonArray det)
            for (int i = 0; i < det.Count; i++) lines.Add("· " + Str(det[i]));
        if (Str(ver["feedback"]).Length > 0) lines.Add("· " + Str(ver["feedback"]));
        txtVerifyResult.Text = string.Join("\n", lines);
        txtVerifyResult.Foreground = style.Fg;
    }

    /// <summary>지금 할 일 안에서 점검 / 해결·검증 중 하나만 보인다.</summary>
    private void FillTodo()
    {
        bool confirmed = _view?["confirmed"] is JsonObject;
        panelFix.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        panelCheck.Visibility = confirmed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ═══════════════════════ 상담 도우미 (챗봇 창) ═══════════════════════
    // 오른쪽 아래 버튼을 누르면 언제든 열리고, 점검을 다 해도 문제가 남으면 저절로 열린다.

    /// <summary>
    /// 상담 도우미 그림. 이 파일만 같은 이름으로 바꾸면 버튼·창의 그림이 바뀐다 (PNG, 정사각형 128×128 권장).
    /// 찾는 순서: CE_CHATBOT_IMAGE 환경 변수 → 실행 파일 옆 assets\chatbot.png → 저장소의 ui\wpf\CrashAnalyzer.UI\assets\chatbot.png
    /// 없거나 못 읽으면 null (글자 버튼으로 대신한다).
    /// </summary>
    private BitmapSource? LoadChatImage()
    {
        var candidates = new List<string>();
        string? env = Environment.GetEnvironmentVariable("CE_CHATBOT_IMAGE");
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
        candidates.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "chatbot.png"));
        string? root = RepoPaths.FindRepoRoot();
        if (root != null) candidates.Add(System.IO.Path.Combine(root, "ui", "wpf", "CrashAnalyzer.UI", "assets", "chatbot.png"));
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!File.Exists(candidates[i])) continue;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;   // 파일을 잠그지 않게 (실행 중에도 그림을 바꿔 넣을 수 있음)
                img.UriSource = new Uri(candidates[i], UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception)
            {
                // 못 읽는 그림이면 다음 후보로
            }
        }
        return null;
    }

    /// <summary>점검을 다 해도 원인을 못 찾았거나(exhausted), 해결책이 모두 실패했거나(failed), 검증에서 재발했을 때</summary>
    private string? ProblemRemains()
    {
        if (_view is null) return null;
        string status = Str(_view["status"]);
        if (status is "exhausted" or "failed") return status;
        if (Str(_view["last_verify"]?["outcome"]) == "recurred")
        {
            int n = _view["history"] is JsonArray h ? h.Count : 0;
            return "recurred:" + n;
        }
        return null;
    }

    private void UpdateChatButton()
    {
        bool openNow = _chat.IsVisible;
        bool problem = ProblemRemains() != null;
        if (_chatImage is null)
        {
            txtChatFallback.Text = openNow ? "상담 도우미 닫기  ▼" : (problem ? "문제가 남았어요 · 상담하기  ▲" : "상담 도우미  ▲");
            bdChatFallback.Background = problem && !openNow ? Ui.Accent : (Brush)FindResource("Ink");
        }
        else if (openNow)
        {
            txtChatLabel.Text = "상담 도우미 닫기";
            txtChatLabel.Foreground = Ui.DimFg;
        }
        else if (problem)
        {
            txtChatLabel.Text = "문제가 남았어요 · 상담하기";
            txtChatLabel.Foreground = Ui.Accent;
        }
        else
        {
            txtChatLabel.Text = "상담 도우미";
            txtChatLabel.Foreground = Ui.StrongFg;
        }
        if (problem)
        {
            txtChatHint.Text = "점검을 다 해도 문제가 남았어요. 상담 도우미와 같이 살펴보세요.";
            txtChatHint.Foreground = Ui.Amber;
        }
        else
        {
            txtChatHint.Text = "궁금한 점은 상담 도우미에게 물어보세요";
            txtChatHint.Foreground = Ui.DimFg;
        }
    }

    private void OnChatToggle(object sender, RoutedEventArgs e)
    {
        if (_chat.IsVisible) _chat.Hide();
        else OpenChat(null);
    }

    /// <summary>메인 창 오른쪽 아래, 버튼 바로 위에 띄운다.</summary>
    private void PlaceChatWindow()
    {
        double w = 420;
        double h = Math.Min(600, Math.Max(420, ActualHeight - 120));
        _chat.Width = w;
        _chat.Height = h;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;
        // 화면 좌표(픽셀) → WPF 단위 (화면 배율 반영)
        var bottomRight = source.CompositionTarget.TransformFromDevice.Transform(PointToScreen(new Point(ActualWidth, ActualHeight)));
        _chat.Left = Math.Max(0, bottomRight.X - w - 24);
        _chat.Top = Math.Max(0, bottomRight.Y - h - 80);
    }

    private void OpenChat(string? reason)
    {
        if (!_chat.IsVisible)
        {
            if (_chat.Owner is null && IsLoaded) _chat.Owner = this;
            PlaceChatWindow();
            _chat.Show();
        }
        _chat.Activate();
        if (_session?.SessionPath is null || _diag is null)
        {
            if (!_chatGreetedEmpty)
            {
                _chat.Add("bot", "먼저 [최근 기록 열기]로 검진을 시작해 주세요. 그다음 결과를 같이 살펴볼게요.");
                _chatGreetedEmpty = true;
            }
        }
        else
        {
            EnsureAgent();
        }
        if (reason != null)
        {
            string msg = reason switch
            {
                "exhausted" => "엔진이 정한 점검을 모두 해 봤지만 원인이 드러나지 않았어요.\n" +
                               "어떤 상황에서 문제가 나는지 말로 알려 주시면, 지금까지의 점검 결과를 바탕으로 같이 살펴볼게요.",
                "failed" => "준비된 해결 방법을 모두 써 봤는데도 문제가 남았어요.\n어떤 상황에서 다시 나는지 알려 주시면 같이 찾아볼게요.",
                _ => "해결책을 적용했는데도 문제가 다시 났어요.\n언제, 어떤 프로그램에서 다시 났는지 알려 주시면 다음 방법을 같이 찾아볼게요.",
            };
            _chat.Add("bot", msg);
        }
        UpdateChatButton();
        _chat.FocusInput();
    }

    /// <summary>점검 후에도 문제가 남았으면 상담 창을 저절로 연다 (같은 이유로는 한 번만).</summary>
    private void AutoOpenChat()
    {
        string? reason = ProblemRemains();
        if (reason != null && reason != _chatReason && !SuppressDialogs)
        {
            _chatReason = reason;
            int colon = reason.IndexOf(':');
            OpenChat(colon >= 0 ? reason.Substring(0, colon) : reason);
        }
    }

    /// <summary>기록을 연 뒤 처음 상담을 열 때 에이전트를 연결하고 인사한다.</summary>
    private void EnsureAgent()
    {
        if (_agent != null || _session?.SessionPath is null) return;
        _agent = ChatAgentFactory.Create(_session);
        _chat.SetAgentMode("연결: " + _agent.Name);
        if (ProblemRemains() is null)
        {
            var top = CandidatesNow();
            string name = top.Count > 0 ? NameOf(top[0]) : "-";
            _chat.Add("bot", "검진 결과를 쉬운 말로 설명해 드릴게요. 지금 가장 의심되는 원인은 '" + name + "'이에요.\n" +
                             "점검은 '지금 할 일'에서 진행하고, 궁금한 점이나 점검 결과를 여기에 말로 적어도 돼요.");
        }
    }

    private async void OnChatSend(string text)
    {
        if (_agent is null)
        {
            _chat.Add("bot", "먼저 [최근 기록 열기]로 검진을 시작해 주세요.");
            return;
        }
        if (_chatBusy) return;
        _chatBusy = true;
        _chat.Add("user", text);
        var agent = _agent;
        var called = new List<string>();
        string answer;
        try
        {
            answer = await Task.Run(() => agent.Ask(text, called));
        }
        catch (Exception ex)
        {
            answer = "오류: " + ex.Message;   // 네트워크·엔진 오류도 대화창에 보여 준다
        }
        if (called.Count > 0) _chat.Add("tool", "엔진 도구 호출: " + string.Join(" → ", called));
        _chat.Add("bot", answer);
        _chatBusy = false;

        // 대화로 점검 결과를 알렸을 수 있으니 화면을 최신 상태로
        if (_session?.SessionPath != null)
        {
            try
            {
                var session = _session;
                _view = await Task.Run(() => session.Status());
            }
            catch (Exception ex)
            {
                SetStatus("엔진 오류: " + ex.Message);
            }
            RefreshScreen();
        }
    }

    // ═══════════════════════ 블랙박스 기록 페이지 ═══════════════════════

    private const double TimelineX0 = 150;
    private const double LaneHeight = 40;

    private void OnModulesToggle(object sender, RoutedEventArgs e) => FillBlackBox();

    private void FillBlackBox()
    {
        if (_page != "bb") return;
        var bb = _blackbox;
        _bbItems.Clear();
        lvBlackBox.ItemsSource = null;
        txtBbDetail.Text = "";
        FillMoment();
        if (bb is null || bb.Source == "none")
        {
            txtBbSub.Text = "기록을 열면 문제 순간 앞뒤가 여기에 나와요.";
            DrawTimeline();
            return;
        }
        if (bb.Source == "collector")
        {
            string how = bb.MetaKind == "manual" || bb.ViaHotkey
                ? "단축키로 직접 남긴 기록"
                : "수집기가 자동으로 남긴 기록 (" + bb.MetaKind + ")";
            string when = bb.Mark is DateTime m ? m.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture) : "";
            txtBbSub.Text = when + " · " + how + " · 앞 5분 ~ 뒤 30초";
        }
        else
        {
            txtBbSub.Text = "실험용 시나리오라 수집기 원본 기록이 없어요. 스냅샷의 이벤트·설치 이력만 보여 줘요.";
        }

        var items = new List<BlackBoxItem>(bb.Items);
        items.Sort((a, b) => a.Time.CompareTo(b.Time));
        bool showModules = chkModules.IsChecked == true;
        bool longSpan = bb.From is DateTime f && bb.To is DateTime t && (t - f).TotalSeconds > 86400;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsModule && !showModules) continue;
            items[i].TimeText = items[i].Time.ToString(longSpan ? "MM/dd HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
            _bbItems.Add(items[i]);
        }
        lvBlackBox.ItemsSource = _bbItems.ToList();
        DrawTimeline();

        // 문제 순간과 가장 가까운 오류 이벤트를 먼저 선택해 둔다
        int pick = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _bbItems.Count; i++)
        {
            if (_bbItems[i].Lane != "event") continue;
            double d = bb.Mark is DateTime mk ? Math.Abs((_bbItems[i].Time - mk).TotalSeconds) : 0;
            if (d < best) { best = d; pick = i; }
        }
        if (pick >= 0) SelectBlackBoxRow(pick);
    }

    private void SelectBlackBoxRow(int index)
    {
        if (index < 0 || index >= lvBlackBox.Items.Count) return;
        lvBlackBox.SelectedIndex = index;
        lvBlackBox.ScrollIntoView(lvBlackBox.Items[index]);
    }

    private void OnBlackBoxSelected(object sender, SelectionChangedEventArgs e)
    {
        if (lvBlackBox.SelectedItem is not BlackBoxItem it) return;
        txtBbDetail.Text = it.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + it.Kind + "\n" +
                           it.Text + "\n\n" + it.Detail;
    }

    /// <summary>엔진 후보 상위 4개가 문제 순간 기록(스냅샷의 드라이버·모듈·보안 제품)에 있었는지.</summary>
    private void FillMoment()
    {
        var cands = CandidatesNow();
        if (cands.Count == 0)
        {
            txtMomentEmpty.Visibility = Visibility.Visible;
            icMoment.ItemsSource = null;
            return;
        }
        txtMomentEmpty.Visibility = Visibility.Collapsed;
        var names = BlackBoxData.RunningNames(_snapshotPath);
        var rows = new List<MomentRow>();
        for (int i = 0; i < Math.Min(4, cands.Count); i++)
        {
            string id = Str(cands[i]?["id"]);
            int colon = id.IndexOf(':');
            string subject = (colon >= 0 ? id.Substring(colon + 1) : id).Replace('_', ' ');
            int dot = subject.IndexOf('.');
            string key = dot >= 0 ? subject.Substring(0, dot) : subject;
            bool present = false;
            for (int k = 0; k < names.Count; k++)
                if (key.Length > 0 && names[k].Contains(key)) present = true;
            rows.Add(new MomentRow
            {
                Name = Ui.Short(NameOf(cands[i]), 26), Present = present, Probability = Num(cands[i]?["probability"]), IsFirst = i == 0,
            });
        }
        icMoment.ItemsSource = rows;
    }

    private double TimeX(DateTime t, double x0, double x1)
    {
        var bb = _blackbox!;
        double span = (bb.To!.Value - bb.From!.Value).TotalSeconds;
        if (span <= 0) return x0;
        return x0 + (x1 - x0) * (t - bb.From.Value).TotalSeconds / span;
    }

    private void OnTimelineSize(object sender, SizeChangedEventArgs e) => DrawTimeline();

    private void DrawTimeline()
    {
        var c = cvTimeline;
        c.Children.Clear();
        var bb = _blackbox;
        double width = Math.Max(c.ActualWidth, 500);
        if (bb is null || bb.From is null || bb.To is null)
        {
            AddText(c, "표시할 기록이 없어요.", 10, 92, 13, Ui.DimFg);
            return;
        }
        double x0 = TimelineX0, x1 = width - 16;
        var lanes = BlackBoxData.Lanes;
        double bottom = 10 + lanes.Length * LaneHeight;
        Brush grid = Ui.Hex("#EEF2F0");
        for (int i = 0; i < lanes.Length; i++)
        {
            double y = 10 + i * LaneHeight;
            AddText(c, lanes[i].Name, 4, y + LaneHeight / 2 - 8, 12, Ui.DimFg);
            c.Children.Add(new Line { X1 = x0, Y1 = y + LaneHeight, X2 = x1, Y2 = y + LaneHeight, Stroke = grid, StrokeThickness = 1 });
        }

        // 시간 눈금: 전체 구간에 눈금이 10개 안팎이 되도록 간격을 고른다 (수집기 덤프는 5분 30초라 1분 간격)
        double span = (bb.To.Value - bb.From.Value).TotalSeconds;
        int[] stepChoices = { 60, 300, 900, 3600, 6 * 3600, 86400, 7 * 86400 };
        int step = stepChoices[stepChoices.Length - 1];
        for (int i = 0; i < stepChoices.Length; i++)
            if (span / stepChoices[i] <= 10) { step = stepChoices[i]; break; }
        string fmt = step < 86400 ? "HH:mm" : "MM/dd";
        DateTime from = bb.From.Value;
        DateTime first = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0);
        if (step >= 3600) first = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0);
        if (step >= 86400) first = new DateTime(from.Year, from.Month, from.Day);
        Brush tick = Ui.Hex("#F0F3F1");
        for (DateTime t = first.AddSeconds(step); t < bb.To.Value; t = t.AddSeconds(step))
        {
            double x = TimeX(t, x0, x1);
            c.Children.Add(new Line { X1 = x, Y1 = 6, X2 = x, Y2 = bottom, Stroke = tick, StrokeThickness = 1 });
            AddCenteredText(c, t.ToString(fmt, CultureInfo.InvariantCulture), x, bottom + 2, 11, FontWeights.Normal, Ui.FaintFg);
        }

        // 기록 점·선
        for (int i = 0; i < bb.Items.Count; i++)
        {
            var it = bb.Items[i];
            int li = it.Lane == "proc" ? 1 : (it.Lane == "module" ? 2 : 0);
            double x = TimeX(it.Time, x0, x1);
            double y = 10 + li * LaneHeight + LaneHeight / 2;
            if (it.Lane == "event")
            {
                var dot = new Ellipse { Width = 10, Height = 10, Fill = Ui.Red };
                Canvas.SetLeft(dot, x - 5);
                Canvas.SetTop(dot, y - 5);
                c.Children.Add(dot);
            }
            else if (it.Lane == "proc")
            {
                c.Children.Add(new Line { X1 = x, Y1 = y - 8, X2 = x, Y2 = y + 8, Stroke = Ui.Accent, StrokeThickness = 2 });
            }
            else
            {
                c.Children.Add(new Line { X1 = x, Y1 = y - 5, X2 = x, Y2 = y + 5, Stroke = Ui.Accent2, StrokeThickness = 1 });
            }
        }

        // GPU 사용률(%) · 온도(℃) 꺾은선 — 둘 다 0~100 기준
        DrawSeries(c, bb.GpuLoad, 3, Ui.Accent, "%", x0, x1);
        DrawSeries(c, bb.GpuTemp, 4, Ui.Hex("#C4581B"), "℃", x0, x1);

        if (bb.Mark is DateTime mark)
        {
            double x = TimeX(mark, x0, x1);
            c.Children.Add(new Line { X1 = x, Y1 = 4, X2 = x, Y2 = bottom, Stroke = Ui.Red, StrokeThickness = 2 });
            bool nearRight = x > x1 - 140;
            var label = new TextBlock
            {
                Text = "문제 순간 " + mark.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                FontSize = 11.5, Foreground = Ui.Red,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, nearRight ? x - 4 - label.DesiredSize.Width : x + 4);
            Canvas.SetTop(label, 0);
            c.Children.Add(label);
        }
    }

    private void DrawSeries(Canvas c, List<(DateTime T, double V)> pts, int laneIndex, Brush color, string unit, double x0, double x1)
    {
        if (pts.Count == 0) return;
        double yBase = 10 + laneIndex * LaneHeight + LaneHeight - 4;
        var line = new Polyline { Stroke = color, StrokeThickness = 1.5 };
        double hi = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            line.Points.Add(new Point(TimeX(pts[i].T, x0, x1), yBase - (LaneHeight - 10) * Math.Min(1.0, pts[i].V / 100.0)));
            if (pts[i].V > hi) hi = pts[i].V;
        }
        if (line.Points.Count >= 2) c.Children.Add(line);
        var label = new TextBlock { Text = "최고 " + (int)hi + unit, FontSize = 11, Foreground = Ui.DimFg };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, x1 - label.DesiredSize.Width);
        Canvas.SetTop(label, yBase - LaneHeight + 4);
        c.Children.Add(label);
    }

    private static void AddText(Canvas c, string text, double left, double top, double size, Brush fg)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        c.Children.Add(tb);
    }

    /// <summary>타임라인을 누르면 그 시각에 가장 가까운 기록을 목록에서 고른다.</summary>
    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        var bb = _blackbox;
        if (bb?.From is null || bb.To is null || _bbItems.Count == 0) return;
        double width = Math.Max(cvTimeline.ActualWidth, 500);
        double clickX = e.GetPosition(cvTimeline).X;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _bbItems.Count; i++)
        {
            double d = Math.Abs(TimeX(_bbItems[i].Time, TimelineX0, width - 16) - clickX);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best >= 0)
        {
            tabsBlackBox.SelectedIndex = 0;
            SelectBlackBoxRow(best);
        }
    }

    // ═══════════════════════ 공용 ═══════════════════════

    private void AppendLog(string line)
    {
        txtLog.AppendText(line + Environment.NewLine);
        if (txtLog.Text.Length > 400_000) txtLog.Text = txtLog.Text[^200_000..];
        txtLog.ScrollToEnd();
    }

    /// <summary>JSON 값 → 글자 (없거나 글자가 아니면 빈 글자)</summary>
    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>JSON 값 → 숫자 (없으면 0)</summary>
    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
