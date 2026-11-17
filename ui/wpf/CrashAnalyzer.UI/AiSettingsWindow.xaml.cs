using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using CrashAnalyzer.Ai;
using CrashAnalyzer.UI.Services;

namespace CrashAnalyzer.UI;

// AI 설정 창 — 2026-11-16 추가, 2026-11-17 추가: 키는 KeyStore 에만 저장 (AI 설명 창과 같은 키)
//   키 입력(가림/보기) · 연결 확인(요금 없는 모델 목록 조회) · 저장 · 저장된 키 지우기
//   입력한 키는 이 창 안에서만 쓰고, 화면에는 KeyStore.Masked(끝 네 글자)로만 보인다.
public partial class AiSettingsWindow : Window
{
    private bool _syncing;

    /// <summary>저장 또는 지우기를 했는가 (메인 창의 'AI 설정' 버튼 글자를 새로 고칠 때 씀)</summary>
    public bool Changed { get; private set; }

    public AiSettingsWindow()
    {
        InitializeComponent();
        RefreshState();
        Loaded += (_, _) => pwKey.Focus();
    }

    private void RefreshState()
    {
        string? key = AiSettings.LoadKey();
        if (!string.IsNullOrEmpty(key))
        {
            txtState.Text = "등록된 키를 쓰고 있어요: " + KeyStore.Masked(key);
            btnDelete.IsEnabled = true;
        }
        else
        {
            txtState.Text = "키 없음 — AI 설명 없이 검진만 동작해요.";
            btnDelete.IsEnabled = KeyStore.Exists();   // 풀 수 없는 키 파일이 남아 있으면 지울 수 있게
        }
    }

    /// <summary>입력 칸의 키 (앞뒤 공백·줄바꿈 제거)</summary>
    private string InputKey()
    {
        string raw = chkShow.IsChecked == true ? txtKey.Text : pwKey.Password;
        return raw.Trim();
    }

    private void ShowResult(string text, bool ok)
    {
        txtResult.Text = text;
        txtResult.Foreground = (Brush)FindResource(ok ? "Green" : "Red");
        txtResult.Visibility = Visibility.Visible;
    }

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        txtResult.Visibility = Visibility.Collapsed;
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        if (chkShow.IsChecked == true)
        {
            txtKey.Text = pwKey.Password;
            pwKey.Visibility = Visibility.Collapsed;
            txtKey.Visibility = Visibility.Visible;
        }
        else
        {
            pwKey.Password = txtKey.Text;
            txtKey.Text = "";
            txtKey.Visibility = Visibility.Collapsed;
            pwKey.Visibility = Visibility.Visible;
        }
        _syncing = false;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        string key = InputKey();
        if (key.Length > 0)
        {
            string? bad = AiSettings.CheckFormat(key);
            if (bad != null)
            {
                ShowResult(bad, false);
                return;
            }
        }
        else
        {
            key = AiSettings.LoadKey() ?? "";
            if (key.Length == 0)
            {
                ShowResult("확인할 키가 없어요. 키를 먼저 입력해 주세요.", false);
                return;
            }
        }

        btnTest.IsEnabled = false;
        btnSave.IsEnabled = false;
        ShowResult("확인하는 중이에요…", true);
        txtResult.Foreground = (Brush)FindResource("TextDim");
        try
        {
            var (ok, message) = await AiSettings.TestKeyAsync(key);
            ShowResult(message, ok);
        }
        finally
        {
            btnTest.IsEnabled = true;
            btnSave.IsEnabled = true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string key = InputKey();
        if (key.Length == 0)
        {
            DialogResult = false;   // 입력 없이 저장 = 바꿀 것이 없음
            return;
        }
        string? bad = AiSettings.CheckFormat(key);
        if (bad != null)
        {
            ShowResult(bad, false);
            return;
        }
        try
        {
            KeyStore.Save(key);
        }
        catch (Exception ex)
        {
            ShowResult("키를 저장하지 못했어요: " + ex.Message, false);
            return;
        }
        pwKey.Password = "";
        txtKey.Text = "";
        Changed = true;
        DialogResult = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "저장된 API 키를 지울까요?\n지운 뒤에는 AI 설명 없이 검진만 동작해요.",
            "AI 설정", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            KeyStore.Delete();
            Changed = true;
            RefreshState();
            ShowResult("저장된 키를 지웠어요.", true);
        }
        catch (Exception ex)
        {
            ShowResult("키를 지우지 못했어요: " + ex.Message, false);
        }
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 브라우저를 못 열어도 설정에는 영향이 없다
        }
        e.Handled = true;
    }
}
