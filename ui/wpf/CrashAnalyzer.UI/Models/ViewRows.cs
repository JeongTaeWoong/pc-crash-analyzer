using System.Windows;
using System.Windows.Media;

namespace CrashAnalyzer.UI.Models;

// 시안 2-4 화면의 목록 한 줄씩 (2026-11-02). 값은 엔진 응답을 옮겨 담기만 한다.

/// <summary>원인 후보 순위 한 줄 (막대). Index = -1 이면 '기타 n개'.</summary>
public sealed class RankRow
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public double Probability { get; init; }
    public bool IsFirst { get; init; }
    public bool IsSelected { get; init; }

    public Brush RowBg => IsSelected ? Ui.AccentSoft : Brushes.Transparent;
    public FontWeight Weight => IsFirst ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush NameFg => Index < 0 ? Ui.DimFg : Ui.StrongFg;
    public Brush PctFg => IsFirst ? Ui.Accent : Ui.StrongFg;
    public Brush BarFill => IsFirst ? Ui.Accent : (Index < 0 ? Ui.Hex("#B9CBC1") : Ui.Accent2);
    public string PctText => Ui.Pct(Probability);
}

/// <summary>점검 진행 한 줄. Kind = done / now / later</summary>
public sealed class ProgressRow
{
    public string Num { get; init; } = "";
    public string Text { get; init; } = "";
    public string State { get; init; } = "";
    public string Kind { get; init; } = "later";

    public Brush Fg => Kind == "now" ? Ui.StrongFg : (Kind == "done" ? Ui.DimFg : Ui.FaintFg);
    public Brush NumFg => Kind == "now" ? Ui.Accent : Ui.FaintFg;
    public Brush StateFg => Kind == "now" ? Ui.Accent : Fg;
    public FontWeight Weight => Kind == "now" ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>해결 방법 표 한 줄</summary>
public sealed class RemedyRow
{
    public int Index { get; init; }
    public string RemedyId { get; init; } = "";
    public string Label { get; init; } = "";
    public double Success { get; init; }
    public string State { get; init; } = "";
    public string RateText => Ui.Pct(Success);
}

/// <summary>"문제 순간에 실행 중이던 후보" 한 줄</summary>
public sealed class MomentRow
{
    public string Name { get; init; } = "";
    public bool Present { get; init; }
    public double Probability { get; init; }
    public bool IsFirst { get; init; }

    public Brush Bg => IsFirst ? Ui.Hex("#F1F5F3") : Ui.Hex("#FFFFFF");
    public FontWeight Weight => IsFirst ? FontWeights.SemiBold : FontWeights.Normal;
    public string State => (Present ? "실행 중이었음" : "기록에 없음") + " · " + Ui.Pct(Probability);
    public Brush StateFg => Present ? Ui.Accent : Ui.FaintFg;
}
