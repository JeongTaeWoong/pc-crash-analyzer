using System.IO;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
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
    private bool _fixApplied;

    public MainWindow()
    {
        InitializeComponent();
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
            SetStatus($"엔진: {_engine.ExePath}");
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
            txtCollectorState.Text = $"수집기: 종료됨 (코드 {code})";
            btnCollectorStart.IsEnabled = true;
            btnCollectorStop.IsEnabled = false;
        });

        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        txtElevation.Text = elevated ? "관리자 (ETW 가능)" : "비관리자 — ETW·모듈 추적 제한";

        Closing += (_, _) => _collector?.Dispose();
    }

    // ─── 수집기 제어 ───

    private void OnCollectorStart(object sender, RoutedEventArgs e)
    {
        try
        {
            _collector!.Start();
            txtCollectorState.Text = "수집기: 동작 중 ●";
            btnCollectorStart.IsEnabled = false;
            btnCollectorStop.IsEnabled = true;
            AppendLog($"=== 수집기 시작 {DateTime.Now:HH:mm:ss} ===");
            SetStatus("수집기 가동 중 — 크래시가 감지되면 자동으로 덤프가 생깁니다");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "수집기 시작 실패"); }
    }

    private void OnCollectorStop(object sender, RoutedEventArgs e)
    {
        _collector!.Stop();
        txtCollectorState.Text = "수집기: 꺼짐";
        btnCollectorStart.IsEnabled = true;
        btnCollectorStop.IsEnabled = false;
        AppendLog($"=== 수집기 중지 {DateTime.Now:HH:mm:ss} ===");
    }

    private void OnNewDump(string path)
    {
        AppendLog($"[새 덤프] {path}");
        SetStatus($"새 덤프 감지: {Path.GetFileName(Path.GetDirectoryName(path))}");
        if (chkAutoDiagnose.IsChecked == true)
            LoadSnapshot(path);
    }

    // ─── 스냅샷 열기 → 진단 ───

    private void OnOpenLatestDump(object sender, RoutedEventArgs e)
    {
        if (_repoRoot is null) return;
        var path = DumpLocator.FindLatestEngineSnapshot(RepoPaths.ResolveDumpsRoot(_repoRoot));
        if (path is null)
        {
            MessageBox.Show(this, "collector 아래에서 engine-snapshot.json을 찾지 못했습니다.\n수집기를 켜고 마킹(크래시)이 한 번 발생해야 덤프가 생깁니다.", "덤프 없음");
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
                var dlgResult = SymptomDialog.Ask(this, snap?["symptom"]?["target_process"]?.GetValue<string>() ?? "");
                if (dlgResult is null) return;
                var sym = snap!["symptom"] as JsonObject ?? new JsonObject();
                sym["type"] = dlgResult.Type;
                sym["scope"] = dlgResult.Scope;
                sym["target_process"] = dlgResult.TargetProcess;
                snap["symptom"] = sym;
                usePath = Path.Combine(RepoPaths.SessionDir(),
                    $"patched_{DateTime.Now:HHmmss}_{Path.GetFileName(path)}");
                await File.WriteAllTextAsync(usePath, snap.ToJsonString());
            }

            SetStatus("진단 중...");
            var result = await Task.Run(() => _engine.CallTool("diagnose", new JsonObject { ["snapshot_path"] = usePath }));
            _currentSnapshotPath = usePath;
            RenderDiagnose(ViewMap.FromDiagnose(result), path);
            btnStartSession.IsEnabled = true;
            tabs.SelectedIndex = 0;
            SetStatus($"진단 완료: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus("진단 실패: " + ex.Message);
            MessageBox.Show(this, ex.Message, "진단 실패");
        }
    }

    private void RenderDiagnose(DiagnoseVm vm, string originalPath)
    {
        txtSnapInfo.Text = $"{vm.SnapshotId}  —  {originalPath}";
        txtSymptomLine.Text = vm.HeaderLine;
        txtLayerLine.Text = vm.LayerLine;
        icLayerBars.ItemsSource = vm.LayerBars;
        icLayerEvidence.ItemsSource = vm.LayerEvidence;
        icCandidates.ItemsSource = vm.Candidates;
        txtPlanSummary.Text = vm.PlanSummary;
        icSteps.ItemsSource = vm.Steps;
        icNotes.ItemsSource = vm.Notes;
    }

    // ─── 세션: 확인 → 해결 → 검증 ───

    private async void OnStartSession(object sender, RoutedEventArgs e)
    {
        if (_session is null || _currentSnapshotPath is null) return;
        await RunSession(() => _session.Start(_currentSnapshotPath), gotoTab: 1);
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
        _fixApplied = true;
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
            SetStatus("엔진 호출 중...");
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
        txtSessionStatus.Text = $"세션 상태: {vm.StatusText} ({vm.Status})";
        txtNextAction.Text = vm.NextAction;
        icBelief.ItemsSource = vm.Belief;
        icHistory.ItemsSource = vm.History;

        cardNextCheck.Visibility = vm.HasNextCheck ? Visibility.Visible : Visibility.Collapsed;
        _currentCheckId = vm.CheckId;
        if (vm.HasNextCheck)
        {
            txtCheckLabel.Text = vm.CheckLabel;
            txtCheckMeta.Text = $"이 단계에서 원인이 드러날 확률 {vm.CheckChancePct}% · {vm.CheckExecutionText}";
            txtCheckAdvice.Text = vm.CheckAdvice;
            icCheckSteps.ItemsSource = vm.CheckSteps.Select((s, i) => $"{i + 1}. {s}").ToList();
            txtCheckRollback.Text = vm.CheckRollback.Length > 0 ? "되돌리기: " + vm.CheckRollback : "";
        }

        txtConfirmed.Text = vm.ConfirmedLabel.Length > 0 ? vm.ConfirmedLabel : "아직 없음";
        icConfirmedEvidence.ItemsSource = vm.ConfirmedEvidence;
        icRemedies.ItemsSource = vm.Remedies;
        btnApplyFix.IsEnabled = vm.Status is "confirmed" or "fixing";
        txtLastVerify.Text = vm.LastVerifyText;
        txtRestoreNote.Text = vm.RestoreNote;

        if (vm.Status == "confirmed" && !_fixApplied) tabs.SelectedIndex = 2;
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
