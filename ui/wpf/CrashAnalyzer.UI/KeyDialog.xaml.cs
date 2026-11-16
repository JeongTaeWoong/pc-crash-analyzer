using System.Windows;

namespace CrashAnalyzer.UI;

/// <summary>자기 API 키 입력 (2026-11-16). 입력 칸은 글자를 가린다. 키는 이 창 밖으로는 KeyStore 로만 간다.</summary>
public partial class KeyDialog : Window
{
    public string? ApiKey { get; private set; }

    public KeyDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => pwKey.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string k = pwKey.Password.Trim();
        if (k.Length < 20 || k.Contains(' '))
        {
            txtErr.Text = "키의 모양이 아닙니다. 공백 없이 전체를 붙여 넣어 주세요.";
            return;
        }
        ApiKey = k;
        DialogResult = true;
    }
}
