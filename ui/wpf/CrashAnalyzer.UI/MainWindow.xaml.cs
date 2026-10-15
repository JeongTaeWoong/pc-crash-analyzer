using System.Collections.ObjectModel;
using System.IO;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using CrashAnalyzer.UI.Models;
using CrashAnalyzer.UI.Services;
using Microsoft.Win32;

namespace CrashAnalyzer.UI;

public partial class MainWindow : Window
{
    private readonly string? _repoRoot;
    private EngineClient? _engine;
    private CollectorHost? _collector;
    private SessionService? _session;
    private string? _currentSnapshotPath;
    private string _currentCheckId = "";
    private readonly ObservableCollection<string> _activity = new();
    private bool _diagnosedOnce;          // 리허설 모드: 자동 진단 완료 감지
    public bool SuppressDialogs { get; set; } // 리허설(오프스크린)에서 MessageBox 차단

    public MainWindow()
    {
        InitializeComponent();
        icActivity.ItemsSource = _activity;

        _repoRoot = RepoPaths.FindRepoRoot();
        if (_repoRoot is null)
        {
            SetStatus("저장소 루트(engine+collector 폴더)를 찾지 못했습니다 — 저장소 안에서 실행하세요");
            return;
        }
        try
        {
            _engine = new EngineClient(RepoPaths.ResolveEnginePath(_repoRoot));
            _session = new SessionService(_engine);
            SetStatus($"엔진 연결됨 · {_engine.ExePath}");
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

        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        txtElevation.Text = elevated ? "관리자 모드" : "비관리자 — ETW·모듈 추적 제한";

        Closing += (_, _) => _collector?.Dispose();
    }

    // ─── 수집기 레일 ───

    private void OnCollectorStart(object sender, RoutedEventArgs e)
    {
        try
        {
            _collector!.Start();
            SetCollectorState(true, "수집 중 — 크래시 대기");
            AppendLog($"=== 수집기 시작 {DateTime.Now:HH:mm:ss} ===");
            Activity("수집기 시작");
            SetStatus("수집기 가동 중 — 크래시가 감지되면 자동으로 덤프가 생깁니다");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "수집기 시작 실패"); }
    }

    private void OnCollectorStop(object sender, RoutedEventArgs e)
    {
        _collector!.Stop();
        SetCollectorState(false, "꺼짐");
        AppendLog($"=== 수집기 중지 {DateTime.Now:HH:mm:ss} ===");
        Activity("수집기 중지");
    }

    private void SetCollectorState(bool running, string text)
    {
        dotCollector.Fill = running ? Ui.Hex("#2F9E63") : Ui.Hex("#B9C2CF");
        txtCollectorState.Text = text;
        txtCollectorState.Foreground = running ? Ui.Hex("#1E6B42") : (Brush)FindResource("TextDim");
        btnCollectorStart.IsEnabled = !running;
        btnCollectorStop.IsEnabled = running;
    }

    private void OnNewDump(string path)
    {
        string name = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
        AppendLog($"[새 덤프] {path}");
        Activity($"새 덤프: {name}");
        SetStatus($"새 덤프 감지: {name}");
        if (chkAutoDiagnose.IsChecked == true)
            LoadSnapshot(path);
    }

    private void Activity(string text)
    {
        txtNoActivity.Visibility = Visibility.Collapsed;
        _activity.Insert(0, $"{DateTime.Now:HH:mm}  {text}");
        while (_activity.Count > 10) _activity.RemoveAt(_activity.Count - 1);
    }

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
            lines.Add("header=" + txtHeaderSnap.Text);
            lines.Add("activity=" + string.Join(" | ", _activity));

            _collector?.Stop();
            tabs.SelectedIndex = 0;
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
    /// --record-demo 인자용: 수집 시작 → 로그 탭 전환을 자동 수행하고,
    /// 크래시 자동 진단(진단 탭 자동 전환 포함)이 끝나면 marker 파일을 쓴다.
    /// </summary>
    public async void StartRecordDemo(string markerPath)
    {
        await Task.Delay(800);
        OnCollectorStart(this, new RoutedEventArgs());
        await Task.Delay(1800);
        tabs.SelectedIndex = 3; // 수집기 로그 탭 — [trigger]/[mark]/[dump]가 라이브로 보이도록

        var t0 = DateTime.UtcNow;
        while (!_diagnosedOnce && (DateTime.UtcNow - t0).TotalSeconds < 120)
            await Task.Delay(300);
        // 진단 성공 시 RenderDiagnose가 진단 탭으로 자동 전환해 둔 상태
        try { await File.WriteAllTextAsync(markerPath, _diagnosedOnce ? "ok" : "timeout"); } catch { /* 마커 실패는 치명 아님 */ }
    }

    /// <summary>--shot 인자용: 창 내용을 PNG로 렌더 (사용자 화면 캡처 없이 자가 검수).</summary>
    public void SaveWindowPng(string outPath)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 10 || size.Height < 10) return;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    // ─── 스냅샷 열기 → 진단 ───

    private void OnOpenLatestDump(object sender, RoutedEventArgs e)
    {
        if (_repoRoot is null) return;
        var path = DumpLocator.FindLatestEngineSnapshot(RepoPaths.ResolveDumpsRoot(_repoRoot));
        if (path is null)
        {
            MessageBox.Show(this,
                "collector 아래에서 engine-snapshot.json을 찾지 못했습니다.\n수집기를 켜 두면 크래시 때 덤프가 자동으로 생깁니다.",
                "덤프 없음");
            return;
        }
        LoadSnapshot(path);
    }

    private void OnOpenSnapshotFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "스냅샷 JSON|*.json",
            InitialDirectory = _repoRoot is null ? "" : Path.Combine(_repoRoot, "engine", "data", "samples"),
        };
        if (dlg.ShowDialog(this) == true)
            LoadSnapshot(dlg.FileName);
    }

    private async void LoadSnapshot(string path)
    {
        if (_engine is null) { SetStatus("엔진이 준비되지 않았습니다"); return; }
        try
        {
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
                usePath = Path.Combine(RepoPaths.SessionDir(),
                    $"patched_{DateTime.Now:HHmmss}_{Path.GetFileName(path)}");
                await File.WriteAllTextAsync(usePath, snap.ToJsonString());
            }

            SetStatus("진단 중…");
            var result = await Task.Run(() => _engine.CallTool("diagnose", new JsonObject { ["snapshot_path"] = usePath }));
            _currentSnapshotPath = usePath;
            RenderDiagnose(ViewMap.FromDiagnose(result));
            btnStartSession.IsEnabled = true;
            tabs.SelectedIndex = 0;
            Activity($"진단 완료: {Path.GetFileName(path)}");
            SetStatus($"진단 완료 · {path}");
        }
        catch (Exception ex)
        {
            SetStatus("진단 실패: " + ex.Message);
            if (!SuppressDialogs) MessageBox.Show(this, ex.Message, "진단 실패");
        }
    }

    private void RenderDiagnose(DiagnoseVm vm)
    {
        _diagnosedOnce = true;
        txtHeaderSnap.Text = $"{vm.SnapshotId}   ·   {vm.Symptom}" +
                             (vm.Target.Length > 0 ? $"   ·   {vm.Target}" : "");
        txtMetaLine.Text = vm.MetaLine;
        txtConfLine.Text = vm.ConfidenceLine;
        txtSummary.Text = vm.SummaryText;
        icLayerBars.ItemsSource = vm.LayerBars;
        lbCandidates.ItemsSource = vm.Candidates.Take(12).ToList();
        if (lbCandidates.Items.Count > 0) lbCandidates.SelectedIndex = 0;
        txtPlanSummary.Text = vm.PlanSummary;
        icSteps.ItemsSource = vm.Steps;
        icNotes.ItemsSource = vm.Notes;
    }

    private void OnCandidateSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (lbCandidates.SelectedItem is not CandidateVm c) return;
        txtSelCand.Text = c.Label;
        icSelEvidence.ItemsSource = c.Evidence.Count > 0
            ? c.Evidence
            : new List<EvidenceVm> { new() { Text = "이 후보에 기록된 개별 근거가 없습니다 (사전 확률 기반)." } };
    }

    // ─── 세션: 확인 → 해결 → 검증 ───

    private async void OnStartSession(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentSnapshotPath is null) return;
        await RunSession(() => _session.Start(_currentSnapshotPath), gotoTab: 1);
        Activity("원인 확인 세션 시작");
    }

    private async void OnReportSolved(object s, RoutedEventArgs e) => await Report("solved");
    private async void OnReportNotSolved(object s, RoutedEventArgs e) => await Report("not_solved");
    private async void OnReportSkipped(object s, RoutedEventArgs e) => await Report("skipped");

    private async Task Report(string result)
    {
        if (_session is null || _currentCheckId.Length == 0) return;
        string id = _currentCheckId;
        await RunSession(() => _session.Report(id, result));
    }

    private async void OnApplyFix(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        await RunSession(() => _session.ApplyFix(), gotoTab: 2);
        Activity("해결책 적용 기록");
    }

    private async void OnVerifyAfterSnapshot(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var dlg = new OpenFileDialog { Filter = "사후 스냅샷 JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        await RunSession(() => _session.VerifyAfter(dlg.FileName), gotoTab: 2);
    }

    private async void OnVerifyNoRecur(object s, RoutedEventArgs e) => await VerifyAnswer(false);
    private async void OnVerifyRecurred(object s, RoutedEventArgs e) => await VerifyAnswer(true);

    private async Task VerifyAnswer(bool recurred)
    {
        if (_session is null) return;
        int minutes = int.TryParse(txtObservedMinutes.Text, out var m) ? m : 60;
        await RunSession(() => _session.VerifyAnswer(recurred, minutes), gotoTab: 2);
    }

    private async Task RunSession(Func<JsonNode> call, int? gotoTab = null)
    {
        try
        {
            SetStatus("엔진 호출 중…");
            var view = await Task.Run(call);
            RenderSession(ViewMap.FromSessionView(view));
            if (gotoTab is { } t) tabs.SelectedIndex = t;
            SetStatus("완료");
        }
        catch (Exception ex)
        {
            SetStatus("세션 오류: " + ex.Message);
            MessageBox.Show(this, ex.Message, "세션 오류");
        }
    }

    private void RenderSession(SessionVm vm)
    {
        txtSessionStatus.Text = $"세션 상태: {vm.StatusText}";
        txtNextAction.Text = vm.NextAction;
        icBelief.ItemsSource = vm.Belief;
        icHistory.ItemsSource = vm.History;

        cardNextCheck.Visibility = vm.HasNextCheck ? Visibility.Visible : Visibility.Collapsed;
        _currentCheckId = vm.CheckId;
        if (vm.HasNextCheck)
        {
            var pill = Ui.ExecPill(vm.CheckExecution);
            txtCheckLabel.Text = vm.CheckLabel;
            txtCheckChance.Text = $"이 단계에서 원인이 드러날 확률 {vm.CheckChancePct}%";
            txtCheckAdvice.Text = vm.CheckAdvice;
            txtCheckExec.Text = pill.Text;
            txtCheckExec.Foreground = pill.Fg;
            bdCheckExec.Background = pill.Bg;
            icCheckSteps.ItemsSource = vm.CheckSteps.Select((s, i) => $"{i + 1}.  {s}").ToList();
            txtCheckRollback.Text = vm.CheckRollback.Length > 0 ? "되돌리기: " + vm.CheckRollback : "";
        }

        txtConfirmed.Text = vm.ConfirmedLabel.Length > 0 ? vm.ConfirmedLabel : "아직 없음";
        icConfirmedEvidence.ItemsSource = vm.ConfirmedEvidence;
        lbRemedies.ItemsSource = vm.Remedies;
        var current = vm.Remedies.FirstOrDefault(r => r.IsCurrent) ?? vm.Remedies.FirstOrDefault();
        if (current != null) lbRemedies.SelectedItem = current;
        btnApplyFix.IsEnabled = vm.Status is "confirmed" or "fixing";
        txtLastVerify.Text = vm.LastVerifyText;
        txtRestoreNote.Text = vm.RestoreNote;

        if (vm.Status == "confirmed") Activity($"원인 확정: {vm.ConfirmedLabel}");
        if (vm.Status is "resolved") Activity("검증 완료 — 해결됨");
    }

    private void OnRemedySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (lbRemedies.SelectedItem is RemedyVm r)
            icRemedyActions.ItemsSource = r.Actions;
    }

    // ─── 공용 ───

    private void AppendLog(string line)
    {
        txtLog.AppendText(line + Environment.NewLine);
        if (txtLog.Text.Length > 400_000) txtLog.Text = txtLog.Text[^200_000..];
        txtLog.ScrollToEnd();
    }

    private void SetStatus(string s) => txtStatus.Text = s;
}
