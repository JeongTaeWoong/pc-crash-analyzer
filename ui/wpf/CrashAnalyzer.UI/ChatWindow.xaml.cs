using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CrashAnalyzer.UI.Models;

namespace CrashAnalyzer.UI;

/// <summary>대화 한 줄. who = user / bot / tool</summary>
public sealed class ChatLine
{
    public string Text { get; init; } = "";
    public string Who { get; init; } = "bot";

    public Brush Bg => Who switch
    {
        "user" => Ui.Accent,
        "bot" => Ui.Hex("#F1F5F3"),
        _ => Brushes.Transparent,
    };
    public Brush Fg => Who switch
    {
        "user" => Brushes.White,
        "bot" => Ui.StrongFg,
        _ => Ui.FaintFg,
    };
    public Thickness Pad => Who == "tool" ? new Thickness(2, 0, 2, 0) : new Thickness(12, 8, 12, 8);
    public Thickness Outer => Who switch
    {
        "user" => new Thickness(70, 4, 0, 4),
        "bot" => new Thickness(0, 4, 50, 4),
        _ => new Thickness(2, 2, 0, 0),
    };
    public HorizontalAlignment Align => Who == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public double Size => Who == "tool" ? 11.5 : 13;
    public FontWeight Weight => Who == "user" ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>
/// 상담 도우미(챗봇) 창 (2026-11-02 시안 2-4). 화면만 맡는다.
/// 보낸 말은 SendRequested 로 메인 창에 넘기고, 메인 창이 에이전트를 불러 답을 Add 로 붙인다.
/// 닫기(X)를 눌러도 숨기기만 해서 대화 내용이 남는다.
/// </summary>
public partial class ChatWindow : Window
{
    private readonly ObservableCollection<ChatLine> _lines = new();

    public event Action<string>? SendRequested;
    public event Action? VisibilityToggled;

    public ChatWindow()
    {
        InitializeComponent();
        icChat.ItemsSource = _lines;
        Closing += OnClosingHide;
        IsVisibleChanged += (_, _) => VisibilityToggled?.Invoke();
    }

    /// <summary>머리 그림 (없으면 숨김)</summary>
    public void SetHeadImage(BitmapSource? image)
    {
        if (image is null) { imgHead.Visibility = Visibility.Collapsed; return; }
        imgHead.Source = image;
        imgHead.Visibility = Visibility.Visible;
    }

    public void SetAgentMode(string text) => txtAgentMode.Text = text;

    public void Add(string who, string text)
    {
        _lines.Add(new ChatLine { Who = who, Text = text });
        // 새 줄이 화면에 그려진 뒤에 맨 아래로 내린다
        Dispatcher.BeginInvoke(new Action(svChat.ScrollToEnd), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ClearLines() => _lines.Clear();

    public void FocusInput() => txtInput.Focus();

    private void OnClosingHide(object? sender, CancelEventArgs e)
    {
        if (ReallyClose) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>앱을 끝낼 때만 true 로 바꿔 실제로 닫는다.</summary>
    public bool ReallyClose { get; set; }

    private void OnSend(object sender, RoutedEventArgs e) => SendInput();

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SendInput(); e.Handled = true; }
    }

    private void SendInput()
    {
        string text = txtInput.Text.Trim();
        if (text.Length == 0) return;
        txtInput.Clear();
        SendRequested?.Invoke(text);
    }

    private void OnQuick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Content is string text) SendRequested?.Invoke(text);
    }
}
