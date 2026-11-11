using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BlackBox.Participant.Ui;

/// <summary>창 공통 모양. 코드로만 만든다(디자이너 파일 없음).</summary>
internal static class UiKit
{
    public static readonly Font Body = new("Malgun Gothic", 10f);
    public static readonly Font Small = new("Malgun Gothic", 9f);
    public static readonly Font Title = new("Malgun Gothic", 13f, FontStyle.Bold);
    public static readonly Color Muted = Color.FromArgb(90, 96, 110);
    public static readonly Color Accent = Color.FromArgb(31, 42, 68);

    public static void Setup(Form f, string title, Size size)
    {
        f.Text = title;
        f.Font = Body;
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.AutoScaleDimensions = new SizeF(96F, 96F);
        f.ClientSize = size;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false;
        f.MinimizeBox = false;
        f.ShowIcon = true;
        f.Icon = AppIcons.Recording;
        f.BackColor = Color.White;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Color.Black,
        Margin = new Padding(0, 4, 0, 4),
        MaximumSize = new Size(560, 0),
    };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(6, 0, 0, 0),
            Font = Body,
            UseVisualStyleBackColor = !primary,
        };
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
        }
        return b;
    }

    public static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0),
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    public static TableLayoutPanel Column(int rows)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = rows,
            Padding = new Padding(18, 14, 18, 14),
            BackColor = Color.White,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }
}

/// <summary>트레이·창 아이콘 — 그려서 만든다(리소스 파일 없음). 수집 중 = 남색 원 + 빨간 점, 일시중지 = 회색 원 + 두 막대.</summary>
internal static class AppIcons
{
    public static readonly Icon Recording = Make(paused: false);
    public static readonly Icon Paused = Make(paused: true);

    private static Icon Make(bool paused)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(paused ? Color.FromArgb(120, 124, 132) : Color.FromArgb(31, 42, 68));
            g.FillEllipse(bg, 1, 1, 30, 30);
            if (paused)
            {
                using var w = new SolidBrush(Color.White);
                g.FillRectangle(w, 10, 9, 4, 14);
                g.FillRectangle(w, 18, 9, 4, 14);
            }
            else
            {
                using var ring = new Pen(Color.White, 2f);
                g.DrawEllipse(ring, 8, 8, 16, 16);
                using var dot = new SolidBrush(Color.FromArgb(234, 67, 53));
                g.FillEllipse(dot, 11, 11, 10, 10);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
