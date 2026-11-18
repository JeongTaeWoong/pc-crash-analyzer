using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace CrashAnalyzer.UI.Services;

// 한글 단어 단위 줄바꿈 — 2026-11-18 추가
//
// WPF 의 TextWrapping="Wrap" 은 한글을 글자마다 끊을 수 있다고 봐서 "문\n제", "시작해\n요." 처럼 단어 중간에서 줄을 바꾼다.
// WPF 에는 CSS 의 word-break: keep-all 같은 설정이 없어서, 여기서 직접 띄어쓰기 자리에서만 줄을 나눈다.
//   1) 앱 전체의 TextBlock 이 화면에 올라올 때(Loaded) 줄바꿈(Wrap)이 켜져 있고 글이 코드·XAML 로 들어간 것만 맡는다.
//      바인딩된 글(Text="{Binding …}")과 링크·굵은 글처럼 여러 조각으로 된 글은 건드리지 않는다(바인딩이 끊기지 않게).
//   2) 쓸 수 있는 폭을 재고, 단어를 하나씩 붙여 보며 넘치면 그 앞 띄어쓰기를 줄바꿈("\n")으로 바꾼다.
//   3) 창 크기가 바뀌거나 코드에서 글을 바꾸면 원래 글로 다시 계산한다.
// 한 단어가 폭보다 길면 그 단어는 WPF 가 원래대로 글자 단위로 끊는다(어쩔 수 없는 경우).
public static class KoreanWordWrap
{
    private sealed class State
    {
        public string Original = "";
        public string? Applied;   // 2026-11-18 추가: 우리가 마지막으로 넣은(줄바꿈 넣은) 글 — 다시 붙을 때 원래 글로 착각하지 않게
        public bool Applying;
        public double LastWidth = -1;
        public FrameworkElement? Parent;
        public SizeChangedEventHandler? ParentHandler;
        public EventHandler? TextHandler;
    }

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();
    private static bool _installed;

    /// <summary>App 시작 때 한 번 부른다. 이후 만들어지는 모든 창의 TextBlock 에 적용된다.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));
    }

    private static bool Eligible(TextBlock tb)
    {
        if (tb.TextWrapping == TextWrapping.NoWrap) return false;
        if (BindingOperations.IsDataBound(tb, TextBlock.TextProperty)) return false;
        // 링크·굵은 글처럼 여러 조각(Inline)으로 된 글은 Text 를 다시 넣으면 모양이 사라지므로 건드리지 않는다
        if (tb.Inlines.Count > 1) return false;
        if (tb.Inlines.Count == 1 && tb.Inlines.FirstInline is not Run) return false;
        return true;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) Attach(tb);
    }

    // 2026-11-18 수정: Loaded 만으로 시작하면 안 됐다.
    // WPF 는 Loaded 를 '직접 붙인 처리기'가 있는 요소에만 보내서, 위의 앱 전체 처리기(RegisterClassHandler)가 아예 불리지 않았다
    // (그래서 폭 계산을 고쳐도 화면이 그대로였음). 크기가 정해질 때(SizeChanged)는 모든 TextBlock 에 오므로 거기서도 붙인다.
    private static void Attach(TextBlock tb)
    {
        if (!Eligible(tb)) return;
        if (States.TryGetValue(tb, out State? existing) && existing.TextHandler != null) return;   // 이미 붙음
        if (!States.TryGetValue(tb, out State? st))
        {
            st = new State();
            States.Add(tb, st);
        }
        // 화면에서 잠깐 빠졌다(Unloaded) 다시 붙는 경우, 지금 글이 우리가 줄바꿈을 넣은 글이면 원래 글을 그대로 쓴다
        if (st.Applied == null || tb.Text != st.Applied) st.Original = tb.Text ?? "";
        st.LastWidth = -1;

        if (st.TextHandler == null)
        {
            State s = st;
            st.TextHandler = (_, _) =>
            {
                if (s.Applying) return;
                s.Original = tb.Text ?? "";   // 코드에서 새 글을 넣었다
                s.LastWidth = -1;
                Reflow(tb, s);
            };
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(tb, st.TextHandler);
        }

        // 왼쪽 정렬처럼 글 길이만큼만 커지는 TextBlock 은 창이 넓어져도 자기 크기가 안 바뀌므로 부모 크기도 본다
        if (st.Parent == null && VisualTreeHelper.GetParent(tb) is FrameworkElement parent)
        {
            State s = st;
            st.Parent = parent;
            st.ParentHandler = (_, _) => Reflow(tb, s);
            parent.SizeChanged += st.ParentHandler;
        }
        Reflow(tb, st);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock tb || !States.TryGetValue(tb, out State? st)) return;
        if (st.TextHandler != null)
        {
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .RemoveValueChanged(tb, st.TextHandler);
            st.TextHandler = null;
        }
        if (st.Parent != null && st.ParentHandler != null) st.Parent.SizeChanged -= st.ParentHandler;
        st.Parent = null;
        st.ParentHandler = null;
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock tb) return;
        if (States.TryGetValue(tb, out State? st) && st.TextHandler != null)
            Reflow(tb, st);
        else
            Attach(tb);   // 2026-11-18 수정: 처음 크기가 정해질 때 붙인다(Loaded 가 안 오는 경우)
    }

    /// <summary>쓸 수 있는 폭: 부모가 내어 준 자리(layout slot)에서 여백을 뺀 값. 없으면 지금 폭.</summary>
    private static double AvailableWidth(TextBlock tb)
    {
        Rect slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(tb);
        double w = slot.Width > 0 ? slot.Width - tb.Margin.Left - tb.Margin.Right : tb.ActualWidth;
        if (!double.IsNaN(tb.Width) && tb.Width > 0) w = Math.Min(w, tb.Width);
        if (!double.IsInfinity(tb.MaxWidth)) w = Math.Min(w, tb.MaxWidth);
        w -= tb.Padding.Left + tb.Padding.Right;
        return w;
    }

    private static void Reflow(TextBlock tb, State st)
    {
        if (st.Applying || !Eligible(tb)) return;
        double width = AvailableWidth(tb);
        if (width <= 10 || double.IsInfinity(width) || double.IsNaN(width)) return;
        if (Math.Abs(width - st.LastWidth) < 0.5) return;
        st.LastWidth = width;

        // 2026-11-18 수정: 1픽셀 여유로는 부족했다(화면에서 "문\n제", "시작해\n요." 그대로).
        // 화면 글씨는 Display 모드라 글자 폭이 픽셀 단위로 반올림돼 계산값보다 조금 넓게 그려진다.
        // 그래서 재는 방식도 화면과 같게 맞추고(Measure), 폭의 3% 와 4픽셀 중 큰 만큼 여유를 둔다.
        double margin = Math.Max(4, width * 0.03);
        string wrapped = Wrap(tb, st.Original, width - margin);
        if (wrapped == tb.Text) return;
        st.Applying = true;
        try
        {
            tb.Text = wrapped;
            st.Applied = wrapped;
        }
        finally
        {
            st.Applying = false;
        }
    }

    /// <summary>원래 글을 width 안에 들어가게, 띄어쓰기 자리에서만 줄을 바꾼다. 원래 있던 줄바꿈은 그대로 둔다.</summary>
    public static string Wrap(TextBlock tb, string text, double width)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(' ') < 0) return text;
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        double dip = VisualTreeHelper.GetDpi(tb).PixelsPerDip;

        string[] paragraphs = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (int p = 0; p < paragraphs.Length; p++)
        {
            if (p > 0) sb.Append('\n');
            string[] words = paragraphs[p].Split(' ');
            string line = "";
            for (int i = 0; i < words.Length; i++)
            {
                string candidate = i == 0 ? words[i] : line + " " + words[i];
                if (i == 0 || Measure(candidate, tb, typeface, dip) <= width)
                {
                    line = candidate;
                }
                else
                {
                    sb.Append(line).Append('\n');
                    line = words[i];
                }
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static double Measure(string s, TextBlock tb, Typeface typeface, double dip)
    {
        // 2026-11-18 수정: 화면과 같은 글자 그리기 방식(TextOptions.TextFormattingMode, 이 앱은 Display)과 언어로 잰다.
        // 예전에는 기본값(Ideal)으로 재서 실제 화면보다 좁게 나왔고, 그 차이만큼 WPF 가 단어 중간을 끊었다.
        TextFormattingMode mode = TextOptions.GetTextFormattingMode(tb);
        CultureInfo culture;
        try { culture = tb.Language.GetSpecificCulture(); }
        catch { culture = CultureInfo.CurrentUICulture; }
        var ft = new FormattedText(s, culture, tb.FlowDirection, typeface, tb.FontSize, Brushes.Black,
                                   new NumberSubstitution(), mode, dip);
        return ft.WidthIncludingTrailingWhitespace;
    }
}
