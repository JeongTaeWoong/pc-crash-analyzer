using System.Drawing;
using System.Windows.Forms;
using BlackBox.Marking;

namespace BlackBox.Participant.Ui;

internal enum ReportMode
{
    /// <summary>단축키·메뉴로 새로 기록 — 마킹은 이미 시작됐고 여기서는 증상·대상·메모만 받는다.</summary>
    NewMark,
    /// <summary>이미 저장된 기록(자동 감지 등)에 나중에 메모를 붙인다.</summary>
    Annotate,
}

/// <summary>
/// "어떤 문제가 있었나요?" — 전부 선택 사항. 고르지 않고 닫아도 기록은 남는다.
/// 일정 시간이 지나면 고른 내용 그대로 저절로 닫힌다(창이 계속 떠 있지 않게).
/// </summary>
internal sealed class ReportForm : Form
{
    private sealed record AppItem(string? Value, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ReportMode _mode;
    private readonly List<(RadioButton Radio, string? Code)> _radios = new();
    private readonly ComboBox? _target;
    private readonly string? _fixedTarget;
    private readonly TextBox _memo;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _remaining;

    /// <summary>참여자의 답. null = 고르지 않고 닫음.</summary>
    public ManualMark? Answer { get; private set; }

    public ReportForm(ReportMode mode, DateTimeOffset markTime, string? preselectSymptom, string? fixedTarget, string? foregroundTarget,
        IReadOnlyList<(string ProcessName, string Label)> apps, int autoCloseSeconds, string? kindLabel = null, bool testAutoAnswer = false)
    {
        _mode = mode;
        _fixedTarget = fixedTarget;
        _remaining = autoCloseSeconds;
        UiKit.Setup(this, mode == ReportMode.NewMark ? $"{Texts.AppTitle} — 문제 기록" : $"{Texts.AppTitle} — 메모 남기기", new Size(560, 530));
        TopMost = true;
        ShowInTaskbar = true;

        var col = UiKit.Column(6);
        for (int i = 0; i < 6; i++) col.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        col.Controls.Add(UiKit.Label(mode == ReportMode.NewMark ? "어떤 문제가 있었나요?" : "무슨 일이 있었는지 알려 주세요", UiKit.Title, UiKit.Accent), 0, 0);
        var sub = mode == ReportMode.NewMark
            ? $"{markTime:HH:mm:ss} 순간의 PC 상태는 이미 기록하고 있어요. 아래는 고르지 않아도 괜찮아요."
            : $"{markTime:M월 d일 HH:mm} · {kindLabel} {(fixedTarget != null ? "· " + fixedTarget : "")}";
        col.Controls.Add(UiKit.Label(sub, UiKit.Small, UiKit.Muted), 0, 1);

        var radios = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
        foreach (var (code, label) in Texts.Symptoms)
        {
            var rb = new RadioButton { Text = label, AutoSize = true, Margin = new Padding(2, 1, 2, 1), Checked = code != null && code == preselectSymptom };
            _radios.Add((rb, code));
            radios.Controls.Add(rb);
        }
        col.Controls.Add(radios, 0, 2);

        var targetRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 4) };
        targetRow.Controls.Add(UiKit.Label("문제 프로그램:"));
        if (mode == ReportMode.NewMark)
        {
            _target = new ComboBox { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(6, 2, 0, 0) };
            _target.Items.Add(new AppItem(null, "(특정 프로그램 아님 / 잘 모르겠어요)"));
            AppItem? selected = null;
            foreach (var (name, label) in apps)
            {
                var item = new AppItem(name, label);
                _target.Items.Add(item);
                if (foregroundTarget != null && name.Equals(foregroundTarget, StringComparison.OrdinalIgnoreCase)) selected = item;
            }
            if (foregroundTarget != null && selected == null)
            {
                selected = new AppItem(foregroundTarget, foregroundTarget);
                _target.Items.Insert(1, selected);
            }
            _target.SelectedItem = selected ?? _target.Items[0];
            targetRow.Controls.Add(_target);
        }
        else
        {
            targetRow.Controls.Add(UiKit.Label(fixedTarget ?? "(PC 전체)", UiKit.Body, UiKit.Muted));
        }
        col.Controls.Add(targetRow, 0, 3);

        var memoBox = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        memoBox.Controls.Add(UiKit.Label("무엇을 하던 중이었나요? (선택 · 이름·연락처 같은 개인정보는 적지 마세요)", UiKit.Small, UiKit.Muted), 0, 0);
        _memo = new TextBox { Multiline = true, Height = 64, Width = 516, MaxLength = 300, ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, 2, 0, 0) };
        memoBox.Controls.Add(_memo, 0, 1);
        col.Controls.Add(memoBox, 0, 4);

        _countdown = UiKit.Label("", UiKit.Small, UiKit.Muted);
        var send = UiKit.Button(mode == ReportMode.NewMark ? "보내기" : "메모 보내기", primary: true);
        send.Click += (_, _) => Submit(force: true);
        var skip = UiKit.Button(mode == ReportMode.NewMark ? "고르지 않고 닫기" : "닫기");
        skip.Click += (_, _) => { Answer = null; Close(); };
        var bottom = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_countdown, 0, 0);
        bottom.Controls.Add(UiKit.ButtonRow(send, skip), 1, 0);
        col.Controls.Add(bottom, 0, 5);

        Controls.Add(col);
        AcceptButton = send;
        CancelButton = skip;

        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            UpdateCountdown();
            if (_remaining <= 0) Submit(force: false);
        };
        Shown += (_, _) =>
        {
            _timer.Start();
            Activate();
            if (testAutoAnswer)
            {
                // 시험(루프백 수신기 전용): 2초 뒤 "점점 느려졌어요" + 메모로 자동 응답
                var t = new System.Windows.Forms.Timer { Interval = 2000 };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    _radios.First(r => r.Code == "slow").Radio.Checked = true;
                    _memo.Text = "자동 시험 응답";
                    Submit(force: true);
                };
                t.Start();
            }
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void UpdateCountdown() =>
        _countdown.Text = $"{Math.Max(0, _remaining)}초 뒤 저절로 닫혀요";

    /// <param name="force">버튼으로 보냄. false = 시간 초과 — 고른 것이 하나도 없으면 답 없음으로 닫는다.</param>
    private void Submit(bool force)
    {
        _timer.Stop();
        string? symptom = _radios.FirstOrDefault(r => r.Radio.Checked).Code;
        bool anyRadio = _radios.Any(r => r.Radio.Checked);
        string? target = _mode == ReportMode.NewMark ? (_target?.SelectedItem as AppItem)?.Value : _fixedTarget;
        string memo = _memo.Text.Trim();
        bool touched = anyRadio || memo.Length > 0 || (_mode == ReportMode.NewMark && _target?.SelectedIndex > 0);
        Answer = force || touched ? new ManualMark(symptom, target, memo.Length > 0 ? memo : null) : null;
        Close();
    }
}
