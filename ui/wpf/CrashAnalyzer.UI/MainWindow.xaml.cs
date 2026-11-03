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
using System.Windows.Media.Effects;
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
    private BlackBoxData? _bbView;          // _blackbox + 실행기 기록 (화면에 그리는 것, 2026-11-02)
    private readonly List<BlackBoxItem> _bbItems = new();

    // 상담 도우미
    private readonly ObservableCollection<ChatLine> _chatLines = new();   // 오른쪽 카드 안 상담 패널 (2026-11-02)
    private IChatAgent? _agent;
    private bool _chatBusy;
    private bool _chatGreetedEmpty;
    private string? _chatReason;          // 상담 창을 자동으로 연 이유 (같은 이유로 두 번 열지 않게)
    private BitmapSource? _chatImage;

    // PC블랙박스 연결 · 실행기는 2026-11-02 UI/UX 푸시에서 빼고 보관함 (README 「보관」 참고)
    private bool _autoPlay;                                       // 임시(시연용) 자동 진행 중   // Lv1 자동 실행을 이미 한 대상 (같은 점검을 두 번 자동 실행하지 않게)

    public MainWindow()
    {
        InitializeComponent();
        icActivity.ItemsSource = _activity;

        icChat.ItemsSource = _chatLines;
        _chatImage = LoadChatImage();
        if (_chatImage != null)
        {
            imgChat.Source = _chatImage;
            imgChatHead.Source = _chatImage;
        }
        else
        {
            imgChat.Visibility = Visibility.Collapsed;
            txtChatGlyph.Visibility = Visibility.Visible;
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

        UpdateCollectorHeader();

        Closing += (_, _) =>
        {
            _collector?.Dispose();
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
        btnCollectorStart.IsEnabled = !running;
        btnCollectorStop.IsEnabled = running;
        UpdateCollectorHeader();
    }

    /// <summary>오른쪽 위 수집기 표시: UI 에서 켠 수집기 상태</summary>
    private void UpdateCollectorHeader()
    {
        bool on = _collector?.IsRunning ?? false;
        dotCollectorHeader.Fill = on ? Ui.Hex("#2F9E63") : Ui.Hex("#B9C2CF");
        txtCollectorHeader.Text = on ? "블랙박스 수집 중" : "수집기 꺼짐";
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
        if (_autoPlay) StopAutoPlay("새 기록을 열어서 자동 진행을 멈췄어요");
        if (!_autoStarting) SetAutoBanner(false);
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
            _chatLines.Clear();
            if (ChatOpen) EnsureAgent();

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
        {
            AutoOpenChat();
        }
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
        {
            AutoOpenChat();
        }
    }

    // ═══════════════════════ 화면 갱신 ═══════════════════════

    private void RefreshScreen()
    {
        FillHeader();
        DrawRing();
        FillStates();
        FillRankNote();
        FillRank();
        FillProgress();
        FillCheck();
        FillEnd();
        FillFix();
        FillTodo();
        FillRightHeader();
        UpdateChatButton();
        FillBlackBox();
        UpdateStatusBar();
    }

    /// <summary>왼쪽 페이지 전환: main(검진 결과) / bb(블랙박스 기록) / progress(점검 진행). 오른쪽 카드는 그대로.</summary>
    private void ShowPage(string name)
    {
        _page = name;
        pageMain.Visibility = name == "main" ? Visibility.Visible : Visibility.Collapsed;
        pageBlackBox.Visibility = name == "bb" ? Visibility.Visible : Visibility.Collapsed;
        pageProgress.Visibility = name == "progress" ? Visibility.Visible : Visibility.Collapsed;
        rbNavMain.IsChecked = name == "main";
        rbNavBlackBox.IsChecked = name == "bb";
        rbNavProgress.IsChecked = name == "progress";
        FillHeader();
        if (name == "bb") FillBlackBox();
    }

    private void OnNavMain(object sender, RoutedEventArgs e) => ShowPage("main");
    private void OnNavBlackBox(object sender, RoutedEventArgs e) => ShowPage("bb");
    private void OnNavProgress(object sender, RoutedEventArgs e) => ShowPage("progress");

    /// <summary>기록 한 줄 요약 (확정안: "othergame.exe 튕김 · 10/14 21:29 · 블랙박스가 자동으로 기록했어요")</summary>
    private void FillHeader()
    {
        UpdateNavLabels();
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
        if (_page == "bb") parts.Add(Str(_diag["target_process"]).Length > 0 ? "문제가 나기 전 PC 에서 일어난 일" : "기록 앞뒤에 일어난 일");
        txtHeaderInfo.Text = string.Join(" · ", parts);
        txtHeaderInfo.Foreground = Ui.DimFg;
    }

    /// <summary>탭 이름 '점검 진행 3/4' — 지금 몇 번째 점검인지 / 엔진이 정한 점검 수</summary>
    private void UpdateNavLabels()
    {
        var (now, total) = CheckCounts();
        rbNavProgress.Content = total > 0 ? "점검 진행 " + now + "/" + total : "점검 진행";
    }

    /// <summary>(지금 하는 점검 번호 또는 끝낸 수, 전체 점검 수)</summary>
    private (int Now, int Total) CheckCounts()
    {
        if (_diag is null) return (0, 0);
        int done = 0;
        if (_view?["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
                if (Str(hist[i]?["type"]) == "check") done++;
        int planned = _diag["plan"]?["steps"] is JsonArray steps ? steps.Count : 0;
        bool checking = Str(_view?["status"]) == "checking";
        int total = Math.Max(planned, done + (checking ? 1 : 0));
        return (checking ? done + 1 : done, total);
    }

    /// <summary>맨 아래 작은 상태 글. 엔진·세션 경로(사용자 이름이 들어감)는 글에 넣지 않고 마우스를 올리면 보이게 했다.</summary>
    private void UpdateStatusBar()
    {
        var parts = new List<string> { _mode == "auto" ? "자동 점검 모드" : "안내 모드" };
        if (_lastStatusMessage.Length > 0) parts.Add(_lastStatusMessage);
        txtStatus.Text = string.Join(" · ", parts);
        var tip = new List<string>();
        if (_engine != null) tip.Add("엔진: " + _engine.ExePath);
        if (_session?.SessionPath != null) tip.Add("세션: " + _session.SessionPath);
        txtStatus.ToolTip = tip.Count > 0 ? string.Join("\n", tip) : null;
    }

    private void SetStatus(string s)
    {
        _lastStatusMessage = s;
        UpdateStatusBar();
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
        const double cx = 100, cy = 100;
        Brush track = Ui.Hex("#D9E4DE");
        AddCircle(c, cx, cy, 88, 18, track);
        AddCircle(c, cx, cy, 66, 6, track);
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
        if (p1 > 0.001) AddArc(c, cx, cy, 88, 18, p1, Ui.Accent);
        if (cands.Count > 1)
        {
            double p2 = Num(cands[1]?["probability"]);
            if (p2 > 0.001) AddArc(c, cx, cy, 66, 6, p2, Ui.Accent2);
        }
        bool confirmed = _view?["confirmed"] is JsonObject;
        txtRingCaption.Text = confirmed ? "확인된 원인" : "가장 유력한 원인";
        txtRingPct.Text = Ui.Pct(p1);
        txtRingPct.FontSize = 34;
        txtRingPct.Foreground = Ui.Accent;
        txtRingName.Text = Ui.Short(NameOf(cands[0]), 16);
        txtRingSub.Text = confirmed ? "원인 확인됨" : (Str(_view?["status"]) == "exhausted" ? "확정 못 함 · 상담으로" : "아직 확정 전");
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
            TextBlock val = k switch
            {
                "SW" => txtStateSW,
                "DRIVER" => txtStateDRIVER,
                _ => txtStateHW,
            };
            if (probs is null)
            {
                val.Text = "-";
                val.Foreground = Ui.FaintFg;
                val.ToolTip = null;
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
            val.ToolTip = "엔진 판정 " + Ui.Pct(p) + " (1위 층 = 주의, 20% 이상 = 관찰)";
        }
    }

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
            lines.Add("아님: " + string.Join("·", rejected.Take(3)) + more + " (점검 " + rejected.Count + "개 끝)");
        }
        txtRankNote.Text = string.Join("\n", lines);
        txtRankNote.ToolTip = _diag != null && cands.Count > 0
            ? "층 판정 신뢰도 " + Ui.Pct(Num(_diag["layer"]?["confidence"])) + " · 1순위 범위: " + Ui.StageKo((int)Num(cands[0]?["stage"]))
            : null;
    }

    // ─── 원인 후보 순위 ───

    private void FillRank()
    {
        var cands = CandidatesNow();
        txtRankEmpty.Visibility = cands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rows = new List<RankRow>();
        int n = Math.Min(3, cands.Count);
        for (int i = 0; i < n; i++)
            rows.Add(new RankRow
            {
                Index = i, Name = Ui.Short(NameOf(cands[i]), 20), Probability = Num(cands[i]?["probability"]),
                IsFirst = i == 0, IsSelected = i == _selectedCand, Tip = EvidenceText(cands[i]),
            });
        double rest = 0;
        for (int i = n; i < cands.Count; i++) rest += Num(cands[i]?["probability"]);
        if (rest > 0.005)
            rows.Add(new RankRow { Index = -1, Name = "기타", Probability = rest, Tip = "나머지 후보 " + (cands.Count - n) + "개" });
        icRank.ItemsSource = rows;
    }

    /// <summary>막대를 누르면 블랙박스 기록 탭에서 근거를 본다 (근거 요약은 막대에 마우스를 올리면 보임).</summary>
    private void OnRankRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int index && index >= 0)
        {
            _selectedCand = index;
            ShowPage("bb");
        }
    }

    /// <summary>후보의 근거 요약 (기여도 큰 순, 사전 비중·층 반영은 뺌)</summary>
    private string EvidenceText(JsonNode? cand)
    {
        var full = DiagCandidate(Str(cand?["id"]));
        if (full is null) return "";
        var ev = new List<JsonNode>();
        if (full["evidence"] is JsonArray arr)
            for (int i = 0; i < arr.Count; i++)
                if (arr[i] != null) ev.Add(arr[i]!);
        ev.Sort((a, b) => Num(b["contribution"]).CompareTo(Num(a["contribution"])));
        var lines = new List<string> { Str(full["label"]) + " 근거" };
        for (int i = 0; i < ev.Count; i++)
        {
            string kind = Str(ev[i]["kind"]);
            if (kind is "prior" or "layer") continue;
            lines.Add("· " + Str(ev[i]["detail"]));
            if (lines.Count >= 4) break;
        }
        if (lines.Count == 1) lines.Add("· 직접 근거는 없고, 증상별 사전 비중만 있어요.");
        lines.Add("(누르면 블랙박스 기록에서 볼 수 있어요)");
        return string.Join("\n", lines);
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

    /// <summary>"3이", "4가" — 숫자 뒤 조사</summary>
    private static string NumSubject(int n)
    {
        int last = n % 10;
        return n + (last is 2 or 4 or 5 or 9 ? "가" : "이");
    }

    /// <summary>대상 이름에서 괄호 안 설명을 뗀다: "Steam 오버레이 (GameOverlayRenderer64.dll)" → "Steam 오버레이"</summary>
    private static string TargetShort(string target)
    {
        int paren = target.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? target.Substring(0, paren) : target;
    }

    private static string CheckLine(string label, string target) =>
        target.Length > 0 ? label + " → " + TargetShort(target) : label;

    /// <summary>점검 목록 (끝난 것 → 지금 → 남은 것). Sub = 점검 진행 탭에 보일 자세한 글.</summary>
    private List<ProgressRow> BuildProgressRows()
    {
        var rows = new List<ProgressRow>();
        if (_diag is null) return rows;
        var doneIds = new HashSet<string>();
        if (_view?["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
            {
                var h = hist[i];
                if (Str(h?["type"]) != "check") continue;
                string reference = Str(h?["ref"]);
                var lab = CheckText(reference);
                doneIds.Add(reference);
                rows.Add(new ProgressRow
                {
                    Num = (rows.Count + 1).ToString(), Text = CheckLine(lab.Check, lab.Target), Sub = lab.Target,
                    State = Ui.ResultKo(Str(h?["result"])), Kind = "done",
                });
            }
        var nc = _view?["next_check"];
        bool checking = _view != null && Str(_view["status"]) == "checking";
        if (nc != null && checking)
        {
            doneIds.Add(Str(nc["check_id"]));
            rows.Add(new ProgressRow
            {
                Num = (rows.Count + 1).ToString(), Text = CheckLine(Str(nc["label"]), Str(nc["target"])),
                Sub = Str(nc["target"]) + " · 약 " + Ui.DurationText(Num(nc["cost_seconds"])) + " · 드러날 확률 " +
                      Ui.Pct(Num(nc["chance_to_reveal_cause"])),
                State = "지금 (오른쪽)", Kind = "now",
            });
        }
        if ((checking || _view is null) && _diag["plan"]?["steps"] is JsonArray steps)
            for (int i = 0; i < steps.Count; i++)
            {
                if (doneIds.Contains(Str(steps[i]?["check_id"]))) continue;
                int num = rows.Count + 1;
                rows.Add(new ProgressRow
                {
                    Num = num.ToString(), Text = CheckLine(Str(steps[i]?["label"]), Str(steps[i]?["target"])),
                    Sub = Str(steps[i]?["target"]) + " · 약 " + Ui.DurationText(Num(steps[i]?["cost_seconds"])),
                    State = num > 1 ? NumSubject(num - 1) + " 그대로면" : "", Kind = "later",
                });
            }
        return rows;
    }

    private void FillProgress()
    {
        var all = BuildProgressRows();
        txtProgressEmpty.Visibility = _diag is null ? Visibility.Visible : Visibility.Collapsed;
        txtProgressAllEmpty.Visibility = _diag is null ? Visibility.Visible : Visibility.Collapsed;
        if (_diag is null)
        {
            icProgress.ItemsSource = null;
            icProgressAll.ItemsSource = null;
            txtProgressCount.Text = "";
            txtPlanSummary.Text = "";
            return;
        }
        icProgressAll.ItemsSource = all;

        // 검진 결과 탭에는 6줄까지 (끝난 점검이 많으면 앞쪽을 한 줄로 줄인다)
        var shown = new List<ProgressRow>();
        int doneCount = 0;
        for (int i = 0; i < all.Count; i++) if (all[i].Kind == "done") doneCount++;
        int skipFrom = doneCount > 4 ? doneCount - 3 : 0;
        if (skipFrom > 0)
            shown.Add(new ProgressRow { Num = "…", Text = "앞의 점검 " + skipFrom + "개", State = "모두 끝남", Kind = "done" });
        for (int i = skipFrom; i < all.Count && shown.Count < 6; i++) shown.Add(all[i]);
        icProgress.ItemsSource = shown;

        var (now, total) = CheckCounts();
        string status = Str(_view?["status"]);
        txtProgressCount.Text = status switch
        {
            "checking" => now + " / 보통 " + total + "번 안에 끝나요",
            "exhausted" => doneCount + " / " + doneCount + " · 모두 그대로",
            _ => doneCount + "개 끝 · 원인 확인",
        };
        var plan = _diag["plan"];
        txtPlanSummary.Text = "원인을 찾을 확률 " + Ui.Pct(Num(plan?["resolved_probability"])) +
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
        spReport.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled)
        {
            wpCheckMeta.Visibility = Visibility.Collapsed;
            bdWhy.Visibility = Visibility.Collapsed;
            icNextSteps.ItemsSource = null;
            txtNextRollback.Text = "";
            if (v is null)
            {
                txtNextTitle.Text = "아직 검진 전이에요";
                txtNextAdvice.Text = "[최근 기록 열기]를 누르면 수집기가 남긴 가장 최근 기록으로 검진을 시작해요.\n" +
                                     "다른 기록이나 실험용 시나리오는 [파일 열기…]로 고를 수 있어요.";
            }
            else
            {
                txtNextTitle.Text = Str(v["status_text"]);
                txtNextAdvice.Text = Str(v["next_action"]);
            }
            return;
        }

        var act = nc!["action"];
        var (now, total) = CheckCounts();
        wpCheckMeta.Visibility = Visibility.Visible;
        txtCheckChip.Text = "점검 " + now + " / " + total;
        var meta = new List<string> { "약 " + Ui.DurationText(Num(nc["cost_seconds"])) };
        if (Bool(act?["requires_reboot"])) meta.Add("재부팅 필요");
        meta.Add("드러날 확률 " + Ui.Pct(Num(nc["chance_to_reveal_cause"])));
        txtNextMeta.Text = string.Join(" · ", meta);
        var pill = Ui.ExecPill(Str(act?["execution"]));
        txtNextExec.Text = pill.Text;
        txtNextExec.Foreground = pill.Fg;
        bdNextExec.Background = pill.Bg;

        txtNextTitle.Text = Str(nc["label"]) + " · " + TargetShort(Str(nc["target"]));
        txtNextAdvice.Text = Str(nc["advice"]);

        // 왜 이걸 먼저 하나요? — 이 점검이 가리는 후보의 근거 + 이미 아닌 것으로 나온 후보
        var why = new List<string>();
        string candidateId = nc["covers"] is JsonArray covers && covers.Count > 0 ? Str(covers[0]) : "";
        var full = DiagCandidate(candidateId);
        if (full?["evidence"] is JsonArray ev)
        {
            var list = new List<JsonNode>();
            for (int i = 0; i < ev.Count; i++)
                if (ev[i] != null && Str(ev[i]!["kind"]) is not ("prior" or "layer")) list.Add(ev[i]!);
            list.Sort((a, b) => Num(b["contribution"]).CompareTo(Num(a["contribution"])));
            for (int i = 0; i < list.Count && i < 2; i++) why.Add(Str(list[i]["detail"]));
        }
        var rejected = RejectedTargets();
        if (rejected.Count > 0) why.Add(string.Join("·", rejected.Take(3)) + "는 이미 원인이 아니었어요.");
        if (why.Count == 0) why.Add("걸리는 시간에 비해 원인을 가를 수 있는 정도가 가장 큰 점검이에요.");
        txtWhyBody.Text = string.Join(" ", why);
        bdWhy.Visibility = Visibility.Visible;

        var steps = new List<string>();
        if (act?["steps"] is JsonArray st)
            for (int i = 0; i < st.Count; i++) steps.Add((i + 1) + ".  " + Str(st[i]));
        steps.Add((steps.Count + 1) + ".  평소처럼 써 보고, 아래에서 결과를 골라 주세요.");
        icNextSteps.ItemsSource = steps;
        txtNextRollback.Text = Str(act?["rollback"]).Length > 0 ? "되돌리기: " + Str(act?["rollback"]) : "되돌리기 어려운 조치예요.";

    }

    /// <summary>준비된 점검을 다 했는데 그대로일 때 (확정안 2-4-B)</summary>
    private void FillEnd()
    {
        if (Str(_view?["status"]) != "exhausted") return;
        int done = 0;
        if (_view?["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
                if (Str(hist[i]?["type"]) == "check") done++;
        txtEndChip.Text = "점검 " + done + " / " + done + " 끝 · 아직 해결 안 됨";
        string sym = Str(_diag?["symptom"]);
        txtEndTitle.Text = "준비된 점검을 모두 해 봤지만 " + (sym switch
        {
            "app_crash" => "아직 튕겨요",
            "bsod" => "아직 블루스크린이 나요",
            _ => "문제가 아직 남아 있어요",
        });
        txtEndBody.Text = (done > 1 ? "점검 1~" + done : "점검 1") + "이 모두 \"그대로\"였어요. 자세한 결과는 왼쪽 점검 진행에 있어요.";
        var top = CandidatesNow();
        string name = top.Count > 0 ? NameOf(top[0]) : "-";
        txtKnown.Text = "가장 유력한 건 여전히 " + name + "지만 확정하지 못했어요. 이제부터는 상담 도우미가 몇 가지를 더 물어보며 같이 찾아요.";
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

    private void OnRemedySelected(object sender, SelectionChangedEventArgs e)
    {
        FillRemedyDetail();
    }

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

    /// <summary>오른쪽 카드 가운데: 지금 할 점검 / 점검 끝(그대로) / 해결·검증 중 하나만 보인다.</summary>
    private void FillTodo()
    {
        bool confirmed = _view?["confirmed"] is JsonObject;
        bool exhausted = !confirmed && Str(_view?["status"]) == "exhausted";
        panelFix.Visibility = confirmed ? Visibility.Visible : Visibility.Collapsed;
        panelEnd.Visibility = exhausted ? Visibility.Visible : Visibility.Collapsed;
        panelCheck.Visibility = !confirmed && !exhausted ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>오른쪽 카드 머리: 상태에 따라 '지금 할 점검' / '점검 결과' / '해결 안내' …</summary>
    private void FillRightHeader()
    {
        string status = Str(_view?["status"]);
        (string title, string sub) = status switch
        {
            "exhausted" => ("점검 결과", ChatOpen ? "오류가 남아 상담 도우미를 열었어요" : "오류가 남아 있어요"),
            "confirmed" => ("해결 안내", "원인이 확인됐어요. 해결책을 적용해요"),
            "fixing" => ("해결 확인", "적용한 해결책이 잘 듣는지 확인해요"),
            "resolved" => ("해결 완료", "재발 없이 해결됐어요"),
            "failed" => ("해결 결과", "준비된 해결책으로는 해결되지 않았어요"),
            _ => ("지금 할 점검", "한 번에 하나씩 안내해요"),
        };
        txtRightTitle.Text = title;
        txtRightSub.Text = sub;
        txtFixChip.Text = status switch
        {
            "fixing" => "해결책 적용함 · 확인 중",
            "resolved" => "해결됨",
            "failed" => "해결 안 됨",
            _ => "원인 확인",
        };
        bdFixChip.Background = status == "failed" ? Ui.Hex("#FDE7E9") : Ui.AccentSoft;
        txtFixChip.Foreground = status == "failed" ? Ui.Red : Ui.Accent;
    }

    // ═══════════════════════ 상담 도우미 (챗봇) ═══════════════════════
    // 확정안 2-4: 오른쪽 아래 둥근 버튼(평소엔 흐리게). 누르면 오른쪽 카드 안에서 상담 패널이 열리고,
    // 준비된 점검을 다 해도 문제가 남으면 저절로 열린다. 2026-11-02 에 따로 뜨던 챗봇 창을 이 패널로 바꿨다.

    private bool ChatOpen => bdChatPanel.Visibility == Visibility.Visible;

    private void ChatAdd(string who, string text)
    {
        _chatLines.Add(new ChatLine { Who = who, Text = text });
        // 새 줄이 그려진 뒤에 맨 아래로 내린다
        Dispatcher.BeginInvoke(new Action(svChat.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SetChatOpen(bool open)
    {
        bdChatPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateChatButton();
        FillRightHeader();
    }

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
        bool problem = ProblemRemains() != null;
        bool open = ChatOpen;
        // 평소에는 흐리게(꺼진 느낌), 문제가 남으면 진한 초록
        elChat.Fill = problem ? Ui.Accent : Ui.Hex("#C9DCD2");
        imgChat.Opacity = problem ? 1.0 : 0.55;
        btnChat.ToolTip = open ? "상담 도우미 닫기" : "상담 도우미 열기";
        if (problem)
        {
            txtChatHint.Text = "문제가 남았어요 · 상담 도우미와 같이 찾아요";
            txtChatHint.Foreground = Ui.Amber;
        }
        else
        {
            txtChatHint.Text = "점검으로 해결이 안 되면 상담 도우미가 열려요";
            txtChatHint.Foreground = Ui.DimFg;
        }
    }

    private void OnChatToggle(object sender, RoutedEventArgs e)
    {
        if (ChatOpen) SetChatOpen(false);
        else OpenChat(null);
    }

    private void OnChatClose(object sender, RoutedEventArgs e) => SetChatOpen(false);

    private void OnChatSendClick(object sender, RoutedEventArgs e) => SendChatInput();

    private void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendChatInput(); e.Handled = true; }
    }

    private void OnChatQuick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string text) OnChatSend(text);
    }

    private void SendChatInput()
    {
        string text = txtChatInput.Text.Trim();
        if (text.Length == 0) return;
        txtChatInput.Clear();
        OnChatSend(text);
    }

    private void OpenChat(string? reason)
    {
        if (!ChatOpen) SetChatOpen(true);
        if (_session?.SessionPath is null || _diag is null)
        {
            if (!_chatGreetedEmpty)
            {
                ChatAdd("bot", "먼저 [최근 기록 열기]로 검진을 시작해 주세요. 그다음 결과를 같이 살펴볼게요.");
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
                "exhausted" => "점검을 다 해 봤는데도 문제가 계속 나네요. 몇 가지만 더 물어볼게요.\n" +
                               "문제가 날 때 공통점이 있나요? (특정 장면에서만, 다른 프로그램에서도, 최근에 뭔가 설치함 등)",
                "failed" => "준비된 해결 방법을 모두 써 봤는데도 문제가 남았어요.\n어떤 상황에서 다시 나는지 알려 주시면 같이 찾아볼게요.",
                _ => "해결책을 적용했는데도 문제가 다시 났어요.\n언제, 어떤 프로그램에서 다시 났는지 알려 주시면 다음 방법을 같이 찾아볼게요.",
            };
            ChatAdd("bot", msg);
        }
        UpdateChatButton();
        txtChatInput.Focus();
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
        txtAgentMode.ToolTip = "연결: " + _agent.Name;
        if (ProblemRemains() is null)
        {
            var top = CandidatesNow();
            string name = top.Count > 0 ? NameOf(top[0]) : "-";
            ChatAdd("bot", "검진 결과를 쉬운 말로 설명해 드릴게요. 지금 가장 의심되는 원인은 '" + name + "'이에요.\n" +
                             "점검은 '지금 할 점검'에서 진행하고, 궁금한 점이나 점검 결과를 여기에 말로 적어도 돼요.");
        }
    }

    private async void OnChatSend(string text)
    {
        if (_agent is null)
        {
            ChatAdd("bot", "먼저 [최근 기록 열기]로 검진을 시작해 주세요.");
            return;
        }
        if (_chatBusy) return;
        _chatBusy = true;
        ChatAdd("user", text);
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
        if (called.Count > 0) ChatAdd("tool", "엔진 도구 호출: " + string.Join(" → ", called));
        ChatAdd("bot", answer);
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

    // ═══════════════════════ 임시(시연용) 자동 진행 — 2026-11-02 추가 ═══════════════════════
    // "지금 할 일"을 사람 대신 차례로 처리한다: 조치 실행(자동 점검 모드일 때) → 점검 결과 알리기 → 해결책 적용 → 검증.
    // 점검 결과는 실험용 시나리오의 정답(ground_truth)으로 정한다. 정답이 없는 실제 기록은 조치까지만 하고 결과에서 멈춘다.
    // 임시 기능이라 판단 로직은 넣지 않았다 — 결과를 아는 시나리오로 흐름을 보여 주는 용도.

    private bool _autoStarting;                                   // 자동 진행이 시연 기록을 여는 중
    private int _autoStepNo;
    private int _autoLogNo;
    private readonly ObservableCollection<string> _autoLog = new();
    private const int AutoStepMs = 3500;                          // 단계마다 기다리는 시간 (보는 사람이 따라올 수 있게)

    private async void OnAutoPlay(object sender, RoutedEventArgs e)
    {
        if (_autoPlay)
        {
            _autoPlay = false;   // 지금 단계가 끝나면 멈춘다
            btnAutoPlay.Content = "▶ 자동 진행 (시연용)";
            txtAutoHead.Text = "■ 멈추는 중…";
            SetStatus("자동 진행을 멈춰요");
            return;
        }
        if (_autoStarting) return;

        _autoStepNo = 0;
        _autoLogNo = 0;
        _autoLog.Clear();
        icAutoLog.ItemsSource = _autoLog;
        bdAutoPlay.Background = (Brush)FindResource("Accent");
        SetAutoBanner(true);
        btnAutoClose.Visibility = Visibility.Collapsed;
        pbAutoWait.Visibility = Visibility.Visible;
        OnNavMain(this, new RoutedEventArgs());

        // 항상 처음부터 보여 주도록 기록을 새로 연다(이미 해결된 세션이면 바로 끝나 버리므로).
        // 지금 기록에 정답이 있으면 그 기록을, 없으면 정답이 있는 실험용 시나리오(s1: 점검 3번 → 해결 → 확인)를 연다.
        string? sample = TrueCandidateId() != null ? (_sourcePath ?? _snapshotPath) : DemoSamplePath();
        if (sample is null)
        {
            ShowAutoBanner("시연용 시나리오 파일을 찾지 못했어요", "[파일 열기…]로 engine\\data\\samples 의 파일을 먼저 열어 주세요.");
            return;
        }
        ShowAutoBanner("기록을 처음부터 다시 열기", System.IO.Path.GetFileName(sample) + " — 실험용 시나리오라 정답(진짜 원인)을 알고 있어요.");
        _autoStarting = true;
        _diag = null;
        LoadSnapshot(sample);
        for (int i = 0; i < 600 && (_diag is null || _view is null); i++) await Task.Delay(150);   // 최대 90초 (증상 창이 뜨면 답할 시간)
        _autoStarting = false;
        if (_diag is null)
        {
            ShowAutoBanner("시나리오를 열지 못했어요", "아래 상태 표시줄의 오류를 확인해 주세요.");
            return;
        }
        AddAutoLog("기록 열기: " + System.IO.Path.GetFileName(sample));
        _autoPlay = true;
        btnAutoPlay.Content = "■ 자동 진행 멈추기";
        await AutoPlayLoop();
    }

    /// <summary>정답이 있는 실험용 시나리오 (지금 열린 것 → s1 순)</summary>
    private string? DemoSamplePath()
    {
        if (_repoRoot is null) return null;
        string dir = System.IO.Path.Combine(_repoRoot, "engine", "data", "samples");
        string first = System.IO.Path.Combine(dir, "s1_anticheat_crash.json");
        if (File.Exists(first)) return first;
        if (!Directory.Exists(dir)) return null;
        string[] files = Directory.GetFiles(dir, "s*.json");
        Array.Sort(files);
        return files.Length > 0 ? files[0] : null;
    }

    /// <summary>스냅샷의 정답(ground_truth)에 해당하는 후보 id. 정답이 없으면 null.</summary>
    private string? TrueCandidateId()
    {
        try
        {
            if (_snapshotPath is null) return null;
            var gt = JsonNode.Parse(File.ReadAllText(_snapshotPath))?["ground_truth"];
            if (gt is null) return null;
            string category = Str(gt["category"]);
            string subject = Str(gt["subject"]).ToLowerInvariant();
            if (_diag?["candidates"] is not JsonArray cands) return null;
            string? byCategory = null;
            for (int i = 0; i < cands.Count; i++)
            {
                if (Str(cands[i]?["category"]) != category) continue;
                string id = Str(cands[i]?["id"]);
                byCategory ??= id;   // 같은 범주에서 확률이 가장 높은 후보 (후보는 확률 순)
                string text = (id + " " + Str(cands[i]?["subject"]) + " " + Str(cands[i]?["product"]) + " " + Str(cands[i]?["label"])).ToLowerInvariant();
                if (subject.Length > 0 && text.Contains(subject)) return id;
            }
            return byCategory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>후보 id → 화면에 보일 이름</summary>
    private string CandidateName(string id)
    {
        if (_diag?["candidates"] is JsonArray cands)
            for (int i = 0; i < cands.Count; i++)
                if (Str(cands[i]?["id"]) == id)
                {
                    string label = Str(cands[i]?["label"]);
                    if (label.Length == 0) label = Str(cands[i]?["product"]);
                    if (label.Length == 0) label = Str(cands[i]?["subject"]);
                    return label.Length > 0 ? label : id;
                }
        return id;
    }

    /// <summary>안내 띠를 단계 표시 자리에 보이거나 숨긴다 (둘 중 하나만 보임).</summary>
    private void SetAutoBanner(bool show)
    {
        bdAutoPlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAutoClose(object sender, RoutedEventArgs e)
    {
        if (!_autoPlay) SetAutoBanner(false);
    }

    /// <summary>지금 세션 단계 이름 (단계 표시를 대신하므로 띠에 적어 준다)</summary>
    private string StageName() => Str(_view?["status"]) switch
    {
        "checking" => "원인 확인",
        "confirmed" => "해결 안내",
        "fixing" => "검증",
        "resolved" => "해결됨",
        _ => "진단",
    };

    private void ShowAutoBanner(string step, string why)
    {
        _autoStepNo++;
        SetAutoBanner(true);
        txtAutoHead.Text = "▶ 자동 진행 중 · " + _autoStepNo + "단계 · " + StageName() + "   (멈추기: 오른쪽 ■ 버튼)";
        txtAutoStep.Text = step;
        txtAutoWhy.Text = why;
        SetStatus("자동 진행 " + _autoStepNo + "단계: " + step);
    }

    private void AddAutoLog(string line)
    {
        _autoLogNo++;
        _autoLog.Add("✓ " + _autoLogNo + ". " + line);
        while (_autoLog.Count > 3) _autoLog.RemoveAt(0);   // 띠에는 최근 3개만 (전체는 블랙박스 기록의 활동 목록)
        Activity("자동 진행: " + line);
    }

    /// <summary>남은 시간 막대를 줄이면서 기다린다. 도중에 멈추면 false.</summary>
    private async Task<bool> AutoWait(int ms)
    {
        int steps = Math.Max(1, ms / 100);
        for (int i = 0; i <= steps; i++)
        {
            if (!_autoPlay) return false;
            pbAutoWait.Value = 1.0 - (double)i / steps;
            await Task.Delay(100);
        }
        return _autoPlay;
    }

    /// <summary>누를 버튼을 주황색으로 깜빡여서 보는 사람이 어디를 누르는지 알게 한다.</summary>
    private async Task AutoPress(Button? button)
    {
        if (button is null || !button.IsVisible) return;
        button.BringIntoView();
        var glow = new DropShadowEffect { Color = Color.FromRgb(0xF2, 0x9D, 0x1F), BlurRadius = 22, ShadowDepth = 0, Opacity = 1 };
        for (int i = 0; i < 3; i++)
        {
            button.Effect = glow;
            await Task.Delay(260);
            button.Effect = null;
            await Task.Delay(160);
        }
        button.Effect = glow;
        await Task.Delay(300);
        button.Effect = null;
    }

    private async Task AutoPlayLoop()
    {
        string? truth = TrueCandidateId();
        if (truth != null)
            ShowAutoBanner("자동 진행 시작", "이 시나리오의 진짜 원인: " + CandidateName(truth) + " — 점검 결과는 이 정답으로 정해요.");
        else
            ShowAutoBanner("자동 진행 시작", "정답 정보가 없는 기록이에요. 조치까지만 하고 결과는 직접 알려 주셔야 해요.");
        if (!await AutoWait(AutoStepMs)) { StopAutoPlay("자동 진행을 멈췄어요."); return; }

        int guard = 0;
        while (_autoPlay && guard++ < 40)
        {
            string status = Str(_view?["status"]);
            if (status == "checking" && _view?["next_check"] is JsonNode nc)
            {
                string label = Str(nc["label"]);
                string target = Str(nc["target"]);
                if (target.Length > 0) label += " → " + target;   // 같은 점검 이름(예: 오버레이 종료)도 대상으로 구분
                ShowAutoBanner("점검: " + label, "오른쪽 '지금 할 점검'의 점검을 해 봐요.");
                if (!await AutoWait(AutoStepMs)) break;
                if (truth is null)
                {
                    StopAutoPlay("정답 정보가 없는 기록이라 결과를 자동으로 정할 수 없어요. 해 본 결과를 버튼으로 알려 주세요.");
                    return;
                }
                bool covers = false;
                if (nc["covers"] is JsonArray cv)
                    for (int i = 0; i < cv.Count; i++)
                        if (Str(cv[i]) == truth) covers = true;
                string answer = covers ? "증상이 사라졌어요" : "그대로예요";
                ShowAutoBanner("결과 알리기 → '" + answer + "'",
                               covers ? "이 점검이 진짜 원인(" + CandidateName(truth) + ")을 건드려서 증상이 사라져요."
                                      : "이 점검은 진짜 원인(" + CandidateName(truth) + ")과 관계가 없어서 증상이 그대로예요.");
                await AutoPress(covers ? btnSolved : btnNotSolved);
                if (!_autoPlay) break;
                await Report(covers ? "solved" : "not_solved");
                AddAutoLog("점검 " + label + " → " + answer);
                if (!await AutoWait(AutoStepMs)) break;
            }
            else if (status == "confirmed")
            {
                ShowAutoBanner("원인 확인 → 해결책 적용", "원인이 확인됐어요. 추천 해결책을 적용하고 '적용했어요'를 눌러요.");
                if (!await AutoWait(AutoStepMs)) break;
                string remedyId = lvRemedies.SelectedItem is RemedyRow r ? r.RemedyId : "";
                string appliedAt = txtAppliedAt.Text.Trim();
                await AutoPress(btnApply);
                if (!_autoPlay) break;
                await RunSession(s => s.ApplyFix(remedyId, appliedAt));
                AddAutoLog("해결책 적용 기록" + (remedyId.Length > 0 ? " (" + remedyId + ")" : ""));
                if (!await AutoWait(AutoStepMs)) break;
            }
            else if (status == "fixing")
            {
                if (truth is null)
                {
                    StopAutoPlay("해결책을 적용했어요. 한동안 써 본 뒤 '재발 없음/다시 발생'으로 확인해 주세요.");
                    return;
                }
                ShowAutoBanner("해결 확인 → '재발 없음 · 120분'", "시나리오는 진짜 원인을 고쳤으니, 2시간 써 봐도 다시 생기지 않았다고 답해요.");
                rbNoRecur.IsChecked = true;
                txtObservedMinutes.Text = "120";
                if (!await AutoWait(AutoStepMs)) break;
                await AutoPress(btnVerifyAnswer);
                if (!_autoPlay) break;
                if (await RunSession(s => s.VerifyAnswer(false, 120)) != null)
                {
                            AddAutoLog("해결 확인: 재발 없음 · 120분");
                    AutoOpenChat();
                }
                if (!await AutoWait(AutoStepMs)) break;
            }
            else
            {
                string text = status switch
                {
                    "resolved" => "자동 진행 끝 — 해결이 확인됐어요.",
                    "exhausted" => "자동 진행 끝 — 점검을 다 했지만 원인을 못 찾았어요.",
                    "failed" => "자동 진행 끝 — 해결책이 모두 실패했어요.",
                    _ => "자동 진행 끝",
                };
                StopAutoPlay(text);
                return;
            }
        }
        StopAutoPlay(_autoPlay ? "자동 진행 끝 (단계가 너무 많아 멈췄어요)" : "자동 진행을 멈췄어요.");
    }

    private void StopAutoPlay(string message)
    {
        _autoPlay = false;
        btnAutoPlay.Content = "▶ 자동 진행 (시연용)";
        SetStatus(message);
        Activity(message);
        if (bdAutoPlay.Visibility == Visibility.Visible)
        {
            txtAutoHead.Text = "■ 자동 진행 끝 · " + _autoStepNo + "단계";
            txtAutoStep.Text = message;
            txtAutoWhy.Text = "다시 보려면 [▶ 자동 진행]을 누르세요. ✕ 를 누르면 단계 표시로 돌아가요.";
            pbAutoWait.Visibility = Visibility.Collapsed;
            btnAutoClose.Visibility = Visibility.Visible;
            if (message.Contains("해결이 확인")) bdAutoPlay.Background = (Brush)FindResource("Accent");
            else bdAutoPlay.Background = (Brush)FindResource("Ink");
        }
    }

    // ═══════════════════════ 블랙박스 기록 페이지 (확정안 2-4-C) ═══════════════════════
    // 보기 필터 · ①②③ 흐름 · 줄별 타임라인(이벤트 로그 / 대상 프로그램 / DLL / 후보 드라이버 / 온도) ·
    // 시간순 기록(출처·판단) · 선택한 항목. '판단' 칸은 엔진 결과(후보 순위, 점검 결과)를 옮겨 적기만 한다.

    private const double TimelineX0 = 112;
    private const double TimelineTop = 22;
    private const double RowH = 32;
    private string _bbFilter = "all";
    private readonly List<(string Key, string Name, bool Strong)> _lanes = new();
    private readonly List<(string File, string Display, string CandId, int Rank)> _bbDrivers = new();
    private BlackBoxItem? _flowStart, _flowModule;

    private void OnModulesToggle(object sender, RoutedEventArgs e) => FillBlackBox();

    private void OnBbFilter(object sender, RoutedEventArgs e)
    {
        _bbFilter = rbFilterProc.IsChecked == true ? "proc"
            : rbFilterDriver.IsChecked == true ? "driver"
            : rbFilterEvent.IsChecked == true ? "event"
            : rbFilterTemp.IsChecked == true ? "temp"
            : "all";
        FillBlackBox();
    }

    /// <summary>후보의 파일 이름(subject), 소문자. 없으면 id 의 ':' 뒤.</summary>
    private string CandSubject(string id)
    {
        string subject = Str(DiagCandidate(id)?["subject"]);
        if (subject.Length == 0)
        {
            int c = id.IndexOf(':');
            subject = c >= 0 ? id.Substring(c + 1) : id;
        }
        return subject.ToLowerInvariant();
    }

    /// <summary>점검 결과가 '그대로'였던 후보 id</summary>
    private HashSet<string> RejectedCandIds()
    {
        var set = new HashSet<string>();
        if (_view?["history"] is JsonArray hist)
            for (int i = 0; i < hist.Count; i++)
            {
                string reference = Str(hist[i]?["ref"]);
                int at = reference.IndexOf('@');
                if (Str(hist[i]?["type"]) == "check" && Str(hist[i]?["result"]) == "not_solved" && at >= 0)
                    set.Add(reference.Substring(at + 1));
            }
        return set;
    }

    /// <summary>후보 상위 중 스냅샷 드라이버 목록에 있는 것 (타임라인 드라이버 줄, 최대 2개)</summary>
    private void FindCandidateDrivers()
    {
        _bbDrivers.Clear();
        var drivers = BlackBoxData.Drivers(_snapshotPath);
        var cands = CandidatesNow();
        for (int i = 0; i < cands.Count && _bbDrivers.Count < 2; i++)
        {
            string id = Str(cands[i]?["id"]);
            string subject = CandSubject(id);
            for (int k = 0; k < drivers.Count; k++)
                if (drivers[k].Name.ToLowerInvariant() == subject)
                {
                    _bbDrivers.Add((drivers[k].Name, drivers[k].Display, id, i));
                    break;
                }
        }
    }

    private string CrashWord() => Str(_diag?["symptom"]) switch
    {
        "app_crash" => "튕김",
        "bsod" => "블루스크린",
        _ => "문제 순간",
    };

    private void FillBlackBox()
    {
        if (_page != "bb") return;
        var bb = _blackbox;
        _bbView = bb;
        _bbItems.Clear();
        lvBlackBox.ItemsSource = null;
        icFlow.ItemsSource = null;
        ShowBbSelection(null);
        FindCandidateDrivers();
        BuildLanes(bb);
        if (bb is null || bb.Source == "none")
        {
            txtBbSpan.Text = "";
            DrawTimeline();
            return;
        }
        if (bb.Mark is DateTime mk0 && bb.From is DateTime f0)
        {
            var span = mk0 - f0;
            txtBbSpan.Text = CrashWord() + " 전 " + (span.TotalHours >= 1
                ? (int)span.TotalHours + "시간 " + span.Minutes + "분"
                : span.Minutes + "분 " + span.Seconds + "초") +
                (bb.Source == "snapshot" ? " · 실험용 시나리오" : "");
        }

        // 판단 칸 · 줄 색 (엔진 결과를 옮겨 적기만 함)
        var rejected = RejectedCandIds();
        var cands = CandidatesNow();
        string topId = cands.Count > 0 ? Str(cands[0]?["id"]) : "";
        var items = new List<BlackBoxItem>(bb.Items);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            it.Verdict = "";
            it.VerdictFg = Ui.DimFg;
            it.RowBg = Brushes.Transparent;
            it.CandId = "";
            switch (it.Lane)
            {
                case "event":
                    it.Source = "이벤트 로그";
                    if (bb.Mark is DateTime mk && Math.Abs((it.Time - mk).TotalSeconds) <= 90)
                    {
                        it.Verdict = CrashWord();
                        it.VerdictFg = Ui.Red;
                        it.RowBg = Ui.Hex("#FDECEE");
                    }
                    break;
                case "proc":
                    it.Source = it.Kind.StartsWith("설치", StringComparison.Ordinal) ? "설치" : "프로세스";
                    break;
                case "module":
                {
                    it.Source = "모듈";
                    string file = (it.Title.Length > 0 ? it.Title : it.Text.Substring(it.Text.LastIndexOf(' ') + 1)).ToLowerInvariant();
                    foreach (string rid in rejected)
                        if (CandSubject(rid) == file)
                        {
                            it.Verdict = "점검: 원인 아님";
                            it.CandId = rid;
                        }
                    if (it.CandId.Length == 0)
                        for (int k = 0; k < cands.Count && k < 4; k++)
                            if (CandSubject(Str(cands[k]?["id"])) == file)
                            {
                                it.CandId = Str(cands[k]?["id"]);
                                it.Verdict = (k + 1) + "순위 후보";
                                it.VerdictFg = k == 0 ? Ui.Amber : Ui.DimFg;
                                if (k == 0) it.RowBg = Ui.Hex("#FFF6E2");
                                break;
                            }
                    break;
                }
                case "action":
                    it.Source = "조치";
                    break;
            }
        }
        // 후보 드라이버: 문제 순간에 실행 중이었다는 줄
        if (bb.Mark is DateTime mark)
        {
            for (int k = 0; k < _bbDrivers.Count; k++)
            {
                var d = _bbDrivers[k];
                bool top = d.CandId == topId;
                items.Add(new BlackBoxItem
                {
                    Time = mark, Lane = "driver", Kind = "드라이버", Source = "드라이버", Title = d.File,
                    Text = d.File + " 실행 중 (" + d.Display + ")",
                    Detail = d.Display + " 드라이버예요. 기록 내내, 그리고 " + CrashWord() + " 순간에도 실행 중이었어요.\n\n" + EvidenceText(DiagCandidate(d.CandId)).Replace("\n(누르면 블랙박스 기록에서 볼 수 있어요)", ""),
                    CandId = d.CandId, Verdict = rejected.Contains(d.CandId) ? "점검: 원인 아님" : (d.Rank + 1) + "순위 후보",
                    VerdictFg = top ? Ui.Amber : Ui.DimFg, RowBg = top ? Ui.Hex("#FFF6E2") : Brushes.Transparent,
                });
            }
            string temps = TempAt(bb.GpuTemp, mark, "GPU") + (bb.CpuTemp.Count > 0 ? " · " + TempAt(bb.CpuTemp, mark, "CPU") : "");
            if (bb.GpuTemp.Count > 0 || bb.CpuTemp.Count > 0)
                items.Add(new BlackBoxItem
                {
                    Time = mark, Lane = "sensor", Kind = "센서", Source = "센서", Title = "온도",
                    Text = temps.Trim(' ', '·'), Detail = "문제 순간에 가장 가까운 온도 기록이에요.",
                });
        }
        items.Sort((x, y) => x.Time.CompareTo(y.Time));

        // ① ② ③ 흐름
        _flowStart = null;
        _flowModule = null;
        string target = bb.TargetProcess.ToLowerInvariant();
        for (int i = 0; i < items.Count && _flowStart is null; i++)
            if (items[i].Lane == "proc" && items[i].Kind.Contains("시작") &&
                (target.Length == 0 || items[i].Text.ToLowerInvariant().StartsWith(target, StringComparison.Ordinal)))
                _flowStart = items[i];
        for (int i = 0; i < items.Count && _flowModule is null; i++)
            if (items[i].Lane == "module" && (_flowStart is null || items[i].Time >= _flowStart.Time) &&
                (items[i].Verdict.Length > 0 || bb.Source == "snapshot"))
                _flowModule = items[i];
        var flow = new List<FlowRow>();
        flow.Add(_flowStart != null
            ? new FlowRow { Head = "① " + _flowStart.Time.ToString("HH:mm", CultureInfo.InvariantCulture), Title = "프로그램 시작", Body = bb.TargetProcess.Length > 0 ? bb.TargetProcess : _flowStart.Text }
            : new FlowRow { Head = "①", Title = "프로그램 시작", Body = "기록 구간 안에 시작 기록이 없어요" });
        if (_flowModule != null)
        {
            var names = new List<string>();
            for (int i = 0; i < items.Count && names.Count < 3; i++)
                if (items[i].Lane == "module" && (items[i].Verdict.Length > 0 || bb.Source == "snapshot") && Math.Abs((items[i].Time - _flowModule.Time).TotalMinutes) < 30)
                {
                    string cn = items[i].CandId.Length > 0 ? TargetShort(Str(DiagCandidate(items[i].CandId)?["label"])) : items[i].Title;
                    if (cn.Length > 0 && !names.Contains(cn)) names.Add(cn);
                }
            flow.Add(new FlowRow
            {
                Head = "② " + _flowModule.Time.ToString("HH:mm", CultureInfo.InvariantCulture), Title = "DLL 로드",
                Body = string.Join("·", names) + (_flowModule.Verdict.Length > 0 ? " · " + _flowModule.Verdict : ""),
            });
        }
        else
            flow.Add(new FlowRow { Head = "②", Title = "DLL 로드", Body = "후보와 관계있는 DLL 기록이 없어요" });
        if (bb.Mark is DateTime m3)
        {
            string body = _bbDrivers.Count > 0 && _bbDrivers[0].Rank == 0 ? "이때도 " + _bbDrivers[0].File + " 실행 중" : "";
            if (body.Length == 0)
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Lane == "event" && items[i].Verdict.Length > 0) body = Ui.Short(items[i].Text, 40);
            flow.Add(new FlowRow
            {
                Head = "③ " + m3.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Title = Ui.SymptomKo(Str(_diag?["symptom"])),
                Body = body, Crash = true,
            });
        }
        icFlow.ItemsSource = flow;

        // 시간순 기록 (보기 필터 · 모듈은 관계있는 것만, 체크하면 전부)
        bool showModules = chkModules.IsChecked == true || bb.Source == "snapshot";
        bool longSpan = bb.From is DateTime f && bb.To is DateTime t && (t - f).TotalSeconds > 86400;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Lane == "module" && !showModules && it.Verdict.Length == 0) continue;
            bool pass = _bbFilter switch
            {
                "proc" => it.Lane is "proc" or "module",
                "driver" => it.Lane == "driver",
                "event" => it.Lane == "event",
                "temp" => it.Lane == "sensor",
                _ => true,
            };
            if (!pass) continue;
            it.TimeText = it.Time.ToString(longSpan || (bb.From is DateTime ff && it.Time.Date != ff.Date) ? "MM/dd HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
            _bbItems.Add(it);
        }
        lvBlackBox.ItemsSource = _bbItems.ToList();
        DrawTimeline();

        // 먼저 보여 줄 항목: 순위에서 누른 후보 → 1순위 후보 드라이버 → 문제 순간과 가장 가까운 오류
        string wantCand = _selectedCand < cands.Count ? Str(cands[_selectedCand]?["id"]) : topId;
        int pick = -1;
        for (int i = 0; i < _bbItems.Count && pick < 0; i++)
            if (_bbItems[i].CandId == wantCand && _bbItems[i].Lane is "driver" or "module") pick = i;
        double best = double.MaxValue;
        if (pick < 0)
            for (int i = 0; i < _bbItems.Count; i++)
            {
                if (_bbItems[i].Lane != "event") continue;
                double d = bb.Mark is DateTime mk ? Math.Abs((_bbItems[i].Time - mk).TotalSeconds) : 0;
                if (d < best) { best = d; pick = i; }
            }
        if (pick >= 0) SelectBlackBoxRow(pick);
    }

    private static string TempAt(List<(DateTime T, double V)> pts, DateTime at, string name)
    {
        if (pts.Count == 0) return "";
        int best = 0;
        for (int i = 1; i < pts.Count; i++)
            if (Math.Abs((pts[i].T - at).TotalSeconds) < Math.Abs((pts[best].T - at).TotalSeconds)) best = i;
        return name + " " + (int)Math.Round(pts[best].V) + "°C";
    }

    private void SelectBlackBoxRow(int index)
    {
        if (index < 0 || index >= lvBlackBox.Items.Count) return;
        lvBlackBox.SelectedIndex = index;
        lvBlackBox.ScrollIntoView(lvBlackBox.Items[index]);
    }

    private void OnBlackBoxSelected(object sender, SelectionChangedEventArgs e)
    {
        ShowBbSelection(lvBlackBox.SelectedItem as BlackBoxItem);
    }

    private static Brush LaneColor(string lane) => lane switch
    {
        "event" => Ui.Red,
        "proc" => Ui.Accent,
        "module" => Ui.Hex("#8C7AE6"),
        "driver" => Ui.Hex("#E3A33B"),
        "sensor" => Ui.Hex("#3A78C2"),
        _ => Ui.Accent2,
    };

    /// <summary>오른쪽 '선택한 항목' 카드</summary>
    private void ShowBbSelection(BlackBoxItem? it)
    {
        if (it is null)
        {
            txtBbSelTitle.Text = "-";
            txtBbDetail.Text = "타임라인이나 목록에서 항목을 누르면 설명이 나와요.";
            bdBbSelFoot.Visibility = Visibility.Collapsed;
            bdBbSel.BorderBrush = Ui.Hex("#DCE6E0");
            rcBbSel.Fill = Ui.Hex("#DCE6E0");
            return;
        }
        Brush color = LaneColor(it.Lane);
        rcBbSel.Fill = color;
        bdBbSel.BorderBrush = it.Lane is "driver" || it.CandId.Length > 0 ? Ui.Hex("#E3B54F") : (it.Lane == "event" ? Ui.Hex("#E8A3A0") : Ui.Hex("#DCE6E0"));
        txtBbSelTitle.Text = it.Title.Length > 0 ? it.Title : it.Kind;
        string body = it.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · " + it.Kind + "\n" + it.Text;
        if (it.Detail.Length > 0 && it.Detail != it.Text) body += "\n\n" + it.Detail;
        txtBbDetail.Text = body;

        if (it.CandId.Length == 0)
        {
            bdBbSelFoot.Visibility = Visibility.Collapsed;
            return;
        }
        var cands = CandidatesNow();
        int rank = -1;
        double prob = 0;
        for (int i = 0; i < cands.Count; i++)
            if (Str(cands[i]?["id"]) == it.CandId) { rank = i; prob = Num(cands[i]?["probability"]); }
        string state;
        if (RejectedCandIds().Contains(it.CandId)) state = "점검에서 원인 아님";
        else if (Str(_view?["confirmed"]?["id"]) == it.CandId || Str(_view?["confirmed"]?["candidate_id"]) == it.CandId) state = "원인으로 확인됨";
        else if (_view?["next_check"]?["covers"] is JsonArray cov && cov.Any(c => Str(c) == it.CandId) && Str(_view?["status"]) == "checking")
            state = "지금 점검 " + CheckCounts().Now + "에서 확인 중";
        else state = "아직 점검 전";
        txtBbSelFoot.Text = (rank >= 0 ? (rank + 1) + "순위 후보 · " + Ui.Pct(prob) + " · " : "") + state;
        bdBbSelFoot.Visibility = Visibility.Visible;
    }

    private double TimeX(DateTime t, double x0, double x1)
    {
        var bb = (_bbView ?? _blackbox)!;
        double span = (bb.To!.Value - bb.From!.Value).TotalSeconds;
        if (span <= 0) return x0;
        return x0 + (x1 - x0) * (t - bb.From.Value).TotalSeconds / span;
    }

    private void OnTimelineSize(object sender, SizeChangedEventArgs e) => DrawTimeline();

    /// <summary>타임라인 줄: 이벤트 로그 · 대상 프로그램 · DLL · 후보 드라이버(최대 2) · 온도 · (조치)</summary>
    private void BuildLanes(BlackBoxData? bb)
    {
        _lanes.Clear();
        _lanes.Add(("event", "이벤트 로그", false));
        _lanes.Add(("proc", bb != null && bb.TargetProcess.Length > 0 ? bb.TargetProcess : "프로그램", false));
        _lanes.Add(("module", "DLL 로드", false));
        string topId = CandidatesNow().Count > 0 ? Str(CandidatesNow()[0]?["id"]) : "";
        for (int k = 0; k < _bbDrivers.Count; k++)
            _lanes.Add(("drv" + k, _bbDrivers[k].File, _bbDrivers[k].CandId == topId));
        if (bb != null && bb.GpuTemp.Count > 0) _lanes.Add(("gpu_temp", "GPU 온도", false));
        if (bb != null && bb.CpuTemp.Count > 0) _lanes.Add(("cpu_temp", "CPU 온도", false));
        if (bb != null && bb.GpuLoad.Count > 0 && _lanes.Count < 7) _lanes.Add(("gpu_load", "GPU 사용률", false));
        bool hasAction = false;
        if (bb != null)
            for (int i = 0; i < bb.Items.Count; i++)
                if (bb.Items[i].Lane == "action") hasAction = true;
        if (hasAction) _lanes.Add(("action", "조치 실행·되돌림", false));
        cvTimeline.Height = TimelineTop + _lanes.Count * RowH + 6;
    }

    private int LaneIndex(string key)
    {
        for (int i = 0; i < _lanes.Count; i++) if (_lanes[i].Key == key) return i;
        return -1;
    }

    private void DrawTimeline()
    {
        var c = cvTimeline;
        c.Children.Clear();
        var bb = _bbView ?? _blackbox;
        double width = Math.Max(c.ActualWidth, 500);
        if (bb is null || bb.From is null || bb.To is null)
        {
            AddText(c, "표시할 기록이 없어요. [최근 기록 열기]로 기록을 열어 주세요.", 10, 40, 13, Ui.DimFg);
            return;
        }
        double x0 = TimelineX0, x1 = width - 12;
        double bottom = TimelineTop + _lanes.Count * RowH;
        Brush rowBg = Ui.Hex("#F2F6F4");
        for (int i = 0; i < _lanes.Count; i++)
        {
            double y = TimelineTop + i * RowH;
            var r = new Rectangle { Width = x1 - x0, Height = RowH - 10, RadiusX = 5, RadiusY = 5, Fill = rowBg };
            Canvas.SetLeft(r, x0);
            Canvas.SetTop(r, y + 5);
            c.Children.Add(r);
            var lbl = new TextBlock
            {
                Text = Ui.Short(_lanes[i].Name, 15), FontSize = 12,
                Foreground = _lanes[i].Strong ? Ui.Hex("#8A5300") : Ui.StrongFg,
                FontWeight = _lanes[i].Strong ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Canvas.SetLeft(lbl, 2);
            Canvas.SetTop(lbl, y + RowH / 2 - 9);
            c.Children.Add(lbl);
        }

        // 시간 눈금 (위쪽)
        double span = (bb.To.Value - bb.From.Value).TotalSeconds;
        int[] stepChoices = { 60, 300, 600, 900, 1800, 3600, 6 * 3600, 86400, 7 * 86400 };
        int step = stepChoices[stepChoices.Length - 1];
        for (int i = 0; i < stepChoices.Length; i++)
            if (span / stepChoices[i] <= 7) { step = stepChoices[i]; break; }
        string fmt = step < 86400 ? "HH:mm" : "MM/dd";
        DateTime from = bb.From.Value;
        DateTime first = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0);
        if (step >= 3600) first = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0);
        if (step >= 86400) first = new DateTime(from.Year, from.Month, from.Day);
        Brush tick = Ui.Hex("#E6ECE8");
        for (DateTime t = first.AddSeconds(step); t < bb.To.Value; t = t.AddSeconds(step))
        {
            if (step < 3600 && t.Second != 0) continue;
            double x = TimeX(t, x0, x1);
            c.Children.Add(new Line { X1 = x, Y1 = TimelineTop, X2 = x, Y2 = bottom, Stroke = tick, StrokeThickness = 1 });
            AddCenteredText(c, t.ToString(fmt, CultureInfo.InvariantCulture), x, 2, 11, FontWeights.Normal, Ui.FaintFg);
        }

        double Y(int lane) => TimelineTop + lane * RowH + RowH / 2;
        bool InRange(DateTime t) => t >= bb.From.Value && t <= bb.To.Value;

        // 대상 프로그램 줄: 시작부터 문제 순간까지 막대 + ①
        int pi = LaneIndex("proc");
        if (_flowStart != null && InRange(_flowStart.Time) && pi >= 0)
        {
            double xs = TimeX(_flowStart.Time, x0, x1);
            double xe = TimeX(bb.Mark ?? bb.To.Value, x0, x1);
            var bar = new Rectangle { Width = Math.Max(2, xe - xs), Height = 12, RadiusX = 3, RadiusY = 3, Fill = Ui.Hex("#BCD9CA") };
            Canvas.SetLeft(bar, xs);
            Canvas.SetTop(bar, Y(pi) - 6);
            c.Children.Add(bar);
            AddBadge(c, "①", xs, Y(pi), Ui.Hex("#E3A33B"));
        }

        // 기록 점·선
        for (int i = 0; i < bb.Items.Count; i++)
        {
            var it = bb.Items[i];
            if (!InRange(it.Time)) continue;
            double x = TimeX(it.Time, x0, x1);
            int li = it.Lane switch
            {
                "proc" => pi,
                "module" => LaneIndex("module"),
                "action" => LaneIndex("action"),
                _ => LaneIndex("event"),
            };
            if (li < 0) continue;
            double y = Y(li);
            if (it.Lane == "event")
            {
                var dot = new Ellipse { Width = 9, Height = 9, Fill = Ui.Red };
                Canvas.SetLeft(dot, x - 4.5);
                Canvas.SetTop(dot, y - 4.5);
                c.Children.Add(dot);
            }
            else if (it.Lane == "proc")
            {
                if (it == _flowStart) continue;
                c.Children.Add(new Line { X1 = x, Y1 = y - 7, X2 = x, Y2 = y + 7, Stroke = Ui.Accent, StrokeThickness = 2 });
            }
            else if (it.Lane == "action")
            {
                Brush fill = it.ActionKind switch
                {
                    "apply" => Ui.Accent,
                    "rollback" => Ui.Hex("#C97A2B"),
                    _ => Brushes.White,
                };
                var dotAction = new Ellipse
                {
                    Width = 11, Height = 11, Fill = fill,
                    Stroke = it.ActionKind == "error" ? Ui.Red : Brushes.White, StrokeThickness = it.ActionKind == "error" ? 2 : 1,
                };
                Canvas.SetLeft(dotAction, x - 5.5);
                Canvas.SetTop(dotAction, y - 5.5);
                c.Children.Add(dotAction);
            }
            else
            {
                c.Children.Add(new Rectangle { Width = 3, Height = 14, RadiusX = 1, RadiusY = 1, Fill = Ui.Hex("#8C7AE6") });
                Canvas.SetLeft(c.Children[c.Children.Count - 1], x - 1.5);
                Canvas.SetTop(c.Children[c.Children.Count - 1], y - 7);
            }
        }
        // ② DLL 로드 + 점검 결과
        int mi = LaneIndex("module");
        if (_flowModule != null && InRange(_flowModule.Time) && mi >= 0)
        {
            double x = TimeX(_flowModule.Time, x0, x1);
            AddBadge(c, "②", x + 10, Y(mi), Ui.Hex("#8C7AE6"));
            if (_flowModule.Verdict.Length > 0) AddText(c, _flowModule.Verdict, x + 22, Y(mi) - 8, 11.5, Ui.DimFg);
        }

        // 후보 드라이버 줄: 기록 내내 실행 중이면 줄 전체에 선
        for (int k = 0; k < _bbDrivers.Count; k++)
        {
            int di = LaneIndex("drv" + k);
            if (di < 0) continue;
            bool strong = _lanes[di].Strong;
            Brush col = strong ? Ui.Hex("#E3A33B") : Ui.Hex("#B5BFC8");
            c.Children.Add(new Line { X1 = x0 + 4, Y1 = Y(di), X2 = x1 - 4, Y2 = Y(di), Stroke = col, StrokeThickness = strong ? 4 : 2 });
            if (strong && bb.Mark != null)
            {
                var note = new TextBlock
                {
                    Text = " " + CrashWord() + " 순간에도 실행 중 ", FontSize = 11.5, Foreground = Ui.Hex("#8A5300"),
                    Background = Ui.Hex("#FFF6E2"),
                };
                note.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(note, x1 - note.DesiredSize.Width - 6);
                Canvas.SetTop(note, Y(di) - 9);
                c.Children.Add(note);
            }
        }

        // 온도 · 사용률 꺾은선
        DrawSeries(c, bb.GpuTemp, LaneIndex("gpu_temp"), Ui.Hex("#2F9E63"), "°C", 30, 100, x0, x1);
        DrawSeries(c, bb.CpuTemp, LaneIndex("cpu_temp"), Ui.Hex("#3A78C2"), "°C", 30, 100, x0, x1);
        DrawSeries(c, bb.GpuLoad, LaneIndex("gpu_load"), Ui.Accent2, "%", 0, 100, x0, x1);

        // ③ 문제 순간
        if (bb.Mark is DateTime mark)
        {
            double x = TimeX(mark, x0, x1);
            c.Children.Add(new Line { X1 = x, Y1 = TimelineTop - 4, X2 = x, Y2 = bottom, Stroke = Ui.Red, StrokeThickness = 2 });
            var label = new TextBlock
            {
                Text = "③ " + mark.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + CrashWord(),
                FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Red, Background = Brushes.White,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool nearRight = x > x1 - label.DesiredSize.Width - 8;
            Canvas.SetLeft(label, nearRight ? x - 4 - label.DesiredSize.Width : x + 4);
            Canvas.SetTop(label, 1);
            c.Children.Add(label);

            // 문제 순간과 가장 가까운 오류 이벤트 이름
            int ei = LaneIndex("event");
            BlackBoxItem? near = null;
            for (int i = 0; i < bb.Items.Count; i++)
                if (bb.Items[i].Lane == "event" && (near is null || Math.Abs((bb.Items[i].Time - mark).TotalSeconds) < Math.Abs((near.Time - mark).TotalSeconds)))
                    near = bb.Items[i];
            if (near != null && ei >= 0 && Math.Abs((near.Time - mark).TotalSeconds) <= 90)
            {
                var ev = new TextBlock { Text = Ui.Short(near.Text, 30), FontSize = 11, Foreground = Ui.Red };
                ev.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(ev, nearRight ? x - 6 - ev.DesiredSize.Width : x + 6);
                Canvas.SetTop(ev, Y(ei) - 8);
                c.Children.Add(ev);
            }
        }
    }

    private static void AddBadge(Canvas c, string text, double x, double y, Brush fill)
    {
        var e = new Ellipse { Width = 16, Height = 16, Fill = fill };
        Canvas.SetLeft(e, x - 8);
        Canvas.SetTop(e, y - 8);
        c.Children.Add(e);
        var tb = new TextBlock { Text = text.Length > 0 ? text.Substring(0, 1) : "", FontSize = 10.5, Foreground = Brushes.White, Width = 16, TextAlignment = TextAlignment.Center };
        Canvas.SetLeft(tb, x - 8);
        Canvas.SetTop(tb, y - 7.5);
        c.Children.Add(tb);
    }

    private void DrawSeries(Canvas c, List<(DateTime T, double V)> pts, int laneIndex, Brush color, string unit,
        double lo, double hi, double x0, double x1)
    {
        if (pts.Count == 0 || laneIndex < 0) return;
        var bb = (_bbView ?? _blackbox)!;
        double top = TimelineTop + laneIndex * RowH + 7, h = RowH - 14;
        var line = new Polyline { Stroke = color, StrokeThickness = 1.6 };
        double max = double.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].T < bb.From!.Value || pts[i].T > bb.To!.Value) continue;
            double v = Math.Max(lo, Math.Min(hi, pts[i].V));
            line.Points.Add(new Point(TimeX(pts[i].T, x0, x1), top + h - h * (v - lo) / (hi - lo)));
            if (pts[i].V > max) max = pts[i].V;
        }
        if (line.Points.Count >= 2) c.Children.Add(line);
        if (max > double.MinValue)
        {
            var label = new TextBlock { Text = "최고 " + (int)Math.Round(max) + unit, FontSize = 11, Foreground = color, Background = Ui.Hex("#F2F6F4") };
            Canvas.SetLeft(label, x0 + 6);
            Canvas.SetTop(label, top - 1);
            c.Children.Add(label);
        }
    }

    private static void AddText(Canvas c, string text, double left, double top, double size, Brush fg)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        c.Children.Add(tb);
    }

    /// <summary>타임라인을 누르면 그 시각·줄에 가장 가까운 기록을 목록에서 고른다.</summary>
    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        var bb = _bbView ?? _blackbox;
        if (bb?.From is null || bb.To is null || _bbItems.Count == 0) return;
        double width = Math.Max(cvTimeline.ActualWidth, 500);
        var pos = e.GetPosition(cvTimeline);
        int lane = (int)((pos.Y - TimelineTop) / RowH);
        string key = lane >= 0 && lane < _lanes.Count ? _lanes[lane].Key : "";
        string want = key.StartsWith("drv", StringComparison.Ordinal) ? "driver"
            : key is "gpu_temp" or "cpu_temp" or "gpu_load" ? "sensor" : key;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _bbItems.Count; i++)
        {
            double d = Math.Abs(TimeX(_bbItems[i].Time, TimelineX0, width - 12) - pos.X);
            if (want.Length > 0 && _bbItems[i].Lane != want) d += 10000;
            if (key.StartsWith("drv", StringComparison.Ordinal) && _bbItems[i].Lane == "driver" &&
                lane < _lanes.Count && _bbItems[i].Title != _lanes[lane].Name) d += 5000;
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
