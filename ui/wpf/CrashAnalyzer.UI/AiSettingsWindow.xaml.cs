using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Navigation;
using CrashAnalyzer.UI.Services;

namespace CrashAnalyzer.UI;

// AI 설정 창 — 2026-11-16 추가
//   키 입력(가림/보기) · 모델 선택 · 연결 확인(요금 없는 모델 목록 조회) · 저장(자격 증명 관리자) · 저장된 키 지우기
//   입력한 키는 이 창 안에서만 쓰고, 화면에는 가린 형태(sk-ant-…abcd)로만 보인다.
public partial class AiSettingsWindow : Window
{
    private readonly AiSettings _settings;
    private bool _syncing;

    /// <summary>저장 또는 지우기를 했는가 (메인 창의 'AI 설정' 버튼 글자를 새로 고칠 때 씀)</summary>
    public bool Changed { get; private set; }

    public AiSettingsWindow()
    {
        InitializeComponent();
        _settings = AiSettings.Load();
        for (int i = 0; i < AiSettings.Models.Length; i++)
        {
            cboModel.Items.Add(AiSettings.Models[i]);
            if (AiSettings.Models[i].Id == _settings.Model) cboModel.SelectedIndex = i;
        }
        if (cboModel.SelectedIndex < 0) cboModel.SelectedIndex = 0;
        RefreshState();
        Loaded += (_, _) => pwKey.Focus();
    }

    private void RefreshState()
    {
        string? key = AiSettings.ResolveKey(out ApiKeySource source);
        switch (source)
        {
            case ApiKeySource.CredentialStore:
                txtState.Text = "저장된 키를 쓰고 있어요: " + AiSettings.Mask(key!);
                break;
            case ApiKeySource.EnvironmentVariable:
                txtState.Text = "환경 변수 " + AiSettings.EnvVarName + " 의 키를 쓰고 있어요: " + AiSettings.Mask(key!)
                                + "\n여기서 키를 저장하면 저장한 키를 먼저 써요.";
                break;
            default:
                txtState.Text = "키 없음 — AI 설명·상담 없이 검진만 동작해요.";
                break;
        }
        btnDelete.IsEnabled = source == ApiKeySource.CredentialStore;
    }

    /// <summary>입력 칸의 키 (앞뒤 공백·줄바꿈 제거)</summary>
    private string InputKey()
    {
        string raw = chkShow.IsChecked == true ? txtKey.Text : pwKey.Password;
        return raw.Trim();
    }

    private string SelectedModel()
    {
        return cboModel.SelectedItem is AiModelChoice m ? m.Id : AiSettings.DefaultModel;
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
            key = AiSettings.ResolveKey(out _) ?? "";
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
            var (ok, message) = await AiSettings.TestKeyAsync(key, SelectedModel());
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
        if (key.Length > 0)
        {
            string? bad = AiSettings.CheckFormat(key);
            if (bad != null)
            {
                ShowResult(bad, false);
                return;
            }
            try
            {
                ApiKeyStore.Save(key);
            }
            catch (Exception ex)
            {
                ShowResult(ex.Message, false);
                return;
            }
        }

        try
        {
            _settings.Model = SelectedModel();
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowResult("모델 설정을 저장하지 못했어요: " + ex.Message, false);
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
            "저장된 API 키를 지울까요?\n지운 뒤에는 AI 설명·상담 없이 검진만 동작해요(환경 변수 키가 있으면 그 키를 써요).",
            "AI 설정", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            ApiKeyStore.Delete();
            Changed = true;
            RefreshState();
            ShowResult("저장된 키를 지웠어요.", true);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, false);
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
