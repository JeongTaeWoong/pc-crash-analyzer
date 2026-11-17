using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CrashAnalyzer.Ai;
using CrashAnalyzer.UI.Models;

namespace CrashAnalyzer.UI;

/// <summary>
/// AI 설명 창 (2026-11-16). 화면은 판단하지 않는다 — 묶음·호출·권고 검사는 CrashAnalyzer.Ai 가 하고 여기서는 보여 주기만 한다.
///  - 키는 제품에 없다. 사용자가 등록한 자기 키로만 보낸다(KeyStore, 사용자 계정으로 암호화).
///  - [AI 에게 묻기]를 누르기 전에는 요금이 드는 요청을 보내지 않는다. 창을 열면 무엇을 보낼지와 예상 요금만 보여 준다
///    (입력 토큰 수 세기는 무료 호출이고, 키가 있을 때만 한다).
///  - 권고 검사에 걸린 조치는 지우지 않고 경고를 붙여 보여 준다.
/// </summary>
public partial class AiWindow : Window
{
    private readonly string _snapshotPath;
    private readonly string _userText;
    private readonly int _itemCount;
    private readonly IReadOnlySet<string> _ids;
    private CancellationTokenSource? _cts;

    public AiWindow(string snapshotPath)
    {
        InitializeComponent();
        _snapshotPath = snapshotPath;
        using var doc = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        var bundle = EvidenceBundle.Build(doc.RootElement);
        _userText = EvidenceBundle.UserMessage(bundle.Text);
        _itemCount = bundle.Ids.Count;
        _ids = bundle.Ids;
        txtBundle.Text = _userText;
        txtSend.Text = $"이 기록에서 정리한 증거 {_itemCount}개 항목(약 {_userText.Length:N0}자)만 보냅니다. 원본 기록, 파일 내용, 키 입력, 화면은 보내지 않습니다.";

        foreach (var m in Pricing.Models) cboModel.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id, ToolTip = m.Note });
        cboModel.SelectedIndex = 0;
        Loaded += async (_, _) => { RefreshKey(); await RefreshCostAsync(); };
        Closed += (_, _) => _cts?.Cancel();
    }

    private string Model => (cboModel.SelectedItem as ComboBoxItem)?.Tag as string ?? Pricing.Models[0].Id;

    private void RefreshKey()
    {
        string? key = KeyStore.Load();
        bool has = !string.IsNullOrEmpty(key);
        txtKey.Text = has
            ? $"등록된 키: {KeyStore.Masked(key)} — 이 PC 의 이 Windows 계정으로만 풀 수 있게 보관합니다. 요금은 이 키의 계정에 청구됩니다."
            : "이 프로그램에는 API 키가 들어 있지 않습니다. 자기 키를 등록하면 그 키로 AI 에 묻습니다. (키가 없어도 검진 결과 화면의 엔진 진단은 그대로 쓸 수 있습니다.)";
        btnKey.Content = has ? "키 바꾸기…" : "키 등록…";
        btnKeyDelete.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        btnAsk.IsEnabled = has;
    }

    private async Task RefreshCostAsync()
    {
        string model = Model;
        string? key = KeyStore.Load();
        if (string.IsNullOrEmpty(key)) { txtCost.Text = "키를 등록하면 보내기 전에 입력 크기와 예상 요금을 보여 줍니다."; return; }
        txtCost.Text = "입력 크기를 세는 중…";
        var (status, tokens, _) = await ClaudeDiagnosis.CountAsync(key, model, _userText);
        if (model != Model) return;                                   // 그사이 모델을 바꿨다
        txtCost.Text = status == "ok"
            ? $"입력 {tokens:N0} 토큰 · 예상 요금 약 {Pricing.EstimateUsd(model, tokens):0.000} 달러 ({Pricing.Of(model).Note})"
            : ClaudeDiagnosis.StatusText(status);
        btnAsk.IsEnabled = status == "ok";
    }

    private async void OnModelChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshCostAsync(); }

    private async void OnKeySet(object sender, RoutedEventArgs e)
    {
        var dlg = new KeyDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.ApiKey is { Length: > 0 } k) KeyStore.Save(k);
        RefreshKey();
        await RefreshCostAsync();
    }

    private async void OnKeyDelete(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "등록된 API 키를 이 PC 에서 지울까요?", "키 지우기", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        KeyStore.Delete();
        RefreshKey();
        await RefreshCostAsync();
    }

    private async void OnAsk(object sender, RoutedEventArgs e)
    {
        btnAsk.IsEnabled = false;
        pnlAnswer.Visibility = Visibility.Collapsed;
        txtStatus.Visibility = Visibility.Visible;
        txtStatus.Text = "AI 에 묻는 중입니다… (보통 10~40초)";
        txtUsage.Text = "";
        _cts = new CancellationTokenSource();
        AiResult r;
        try { r = await ClaudeDiagnosis.RunAsync(KeyStore.Load(), Model, _userText, _cts.Token); }
        finally { btnAsk.IsEnabled = KeyStore.Exists(); }
        Show(r);
    }

    /// <summary>받은 결과를 화면에 옮긴다 (--shot-ai 자가 검수에서도 쓴다).</summary>
    public void Show(AiResult r)
    {
        txtUsage.Text = r.InputTokens > 0
            ? $"실제 사용: 입력 {r.InputTokens:N0} · 출력 {r.OutputTokens:N0} 토큰 · {r.CostUsd:0.0000} 달러 · {Pricing.Of(r.Model).Label}" + (r.Attempts > 1 ? $" · {r.Attempts}번 물음(첫 답이 잘림)" : "")
              + $" · 지시문 {AiPrompt.PromptVersion} · 묶음 {AiPrompt.BundleVersion}"
            : "";
        if (r.Answer is null)
        {
            txtStatus.Visibility = Visibility.Visible;
            txtStatus.Text = ClaudeDiagnosis.StatusText(r.Status);
            pnlAnswer.Visibility = Visibility.Collapsed;
            return;
        }
        var a = r.Answer;
        txtStatus.Visibility = Visibility.Collapsed;
        pnlAnswer.Visibility = Visibility.Visible;
        txtVerdict.Text = a.VerdictText;
        txtExplain.Text = a.Explanation;

        icCands.ItemsSource = a.Candidates.Select((c, i) => new CandRow(
            $"{i + 1}. {c.Name}",
            "확신 " + c.Confidence switch { "high" => "높음", "medium" => "중간", "low" => "낮음", var x => x },
            c.Reason,
            c.EvidenceIds.Count == 0 ? "" : "근거 항목: " + string.Join(", ", c.EvidenceIds) + (c.EvidenceIds.Any(id => !_ids.Contains(id)) ? "  (보낸 자료에 없는 번호가 있습니다)" : ""))).ToList();
        txtNoCand.Visibility = a.Candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        icActs.ItemsSource = a.Actions.Select((x, i) =>
        {
            var warns = r.Findings.Where(f => f.ActionIndex == i).Select(f => "권고 검사: " + f.Why).ToList();
            var (bg, fg, text) = LevelStyle(x.Level);
            return new ActRow(text, bg, fg, x.Action, "대상: " + x.Target, x.Why, string.Join("\n", warns), warns.Count > 0 ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
        txtNoAct.Visibility = a.Actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        txtCheck.Text = r.Findings.Count == 0
            ? "권고 검사: 걸린 조치가 없습니다(백신 끄기, Windows 재설치, 원인 후보에 없는 드라이버·런타임·하드웨어를 건드리는 조치를 코드로 확인)."
            : $"권고 검사: 조치 {r.Findings.Select(f => f.ActionIndex).Distinct().Count()}개에 주의 표시를 붙였습니다. 지우지는 않았습니다.";
    }

    /// <summary>창의 내용을 PNG 로 저장한다 (화면 밖에서 그린 창 — 사용자 화면을 찍지 않는다).</summary>
    public void SavePng(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static (Brush Bg, Brush Fg, string Text) LevelStyle(string level) => level switch
    {
        "Lv0" => (Ui.GuideBg, Ui.GuideFg, "Lv0 관찰"),
        "Lv1" => (Ui.AutoBg, Ui.AutoFg, "Lv1 일시 조치"),
        "Lv2" => (Ui.ConfirmBg, Ui.ConfirmFg, "Lv2 설정 변경"),
        "Lv3" => (Ui.ManualBg, Ui.ManualFg, "Lv3 시스템 변경"),
        "Lv4" => (Ui.GuideBg, Ui.GuideFg, "Lv4 직접 하기"),
        _ => (Ui.GuideBg, Ui.GuideFg, level),
    };

    public sealed record CandRow(string Title, string Confidence, string Reason, string Evidence);
    public sealed record ActRow(string LevelText, Brush LevelBg, Brush LevelFg, string Action, string Target, string Why, string Warn, Visibility WarnVisibility);
}
