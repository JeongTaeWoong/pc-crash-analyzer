using System.Windows;
using System.Windows.Media;

namespace CrashAnalyzer.UI.Models;

/// <summary>
/// 상담 도우미 대화 한 줄. who = user / bot / tool.
/// 2026-11-02: 따로 뜨던 챗봇 창(ChatWindow)을 없애고 오른쪽 카드 안의 상담 패널로 옮기면서 이 파일로 뺐다.
/// 확정안 2-4-B 처럼 사용자 말은 초록 말풍선, 도우미 말은 흰 바탕 + 테두리.
/// </summary>
public sealed class ChatLine
{
    public string Text { get; init; } = "";
    public string Who { get; init; } = "bot";

    public Brush Bg => Who switch
    {
        "user" => Ui.Accent,
        "bot" => Brushes.White,
        _ => Brushes.Transparent,
    };
    public Brush Border => Who == "bot" ? Ui.Hex("#DCE6E0") : Brushes.Transparent;
    public Thickness BorderWidth => Who == "bot" ? new Thickness(1) : new Thickness(0);
    public Brush Fg => Who switch
    {
        "user" => Brushes.White,
        "bot" => Ui.StrongFg,
        _ => Ui.FaintFg,
    };
    public Thickness Pad => Who == "tool" ? new Thickness(2, 0, 2, 0) : new Thickness(12, 8, 12, 8);
    public Thickness Outer => Who switch
    {
        "user" => new Thickness(56, 4, 0, 4),
        "bot" => new Thickness(0, 4, 36, 4),
        _ => new Thickness(2, 2, 0, 0),
    };
    public HorizontalAlignment Align => Who == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public double Size => Who == "tool" ? 11.5 : 12.5;
    public FontWeight Weight => Who == "user" ? FontWeights.SemiBold : FontWeights.Normal;
}
