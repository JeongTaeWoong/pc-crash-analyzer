using System.Drawing;
using System.Windows.Forms;
using StudyCore;

namespace BlackBox.Participant.Ui;

/// <summary>처음 실행할 때의 참여 동의. "동의"에 체크해야 시작할 수 있고, 거절하면 아무것도 저장하지 않고 끝난다.</summary>
internal sealed class ConsentForm : Form
{
    private readonly CheckBox _agree;
    private readonly CheckBox _autostart;
    private readonly TextBox _nickname;
    private readonly Button _ok;

    public string ParticipantId { get; }
    public string Nickname => _nickname.Text;
    public bool Autostart => _autostart.Checked;

    public ConsentForm(StudyConfig study, string participantId)
    {
        ParticipantId = participantId;
        UiKit.Setup(this, $"{Texts.AppTitle} — 연구 참여 안내", new Size(640, 700));

        var col = UiKit.Column(6);
        col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        col.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) col.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        col.Controls.Add(UiKit.Label("PC 블랙박스 연구에 참여하시겠어요?", UiKit.Title, UiKit.Accent), 0, 0);

        var text = new RichTextBox
        {
            ReadOnly = true,
            Text = Texts.Consent(study, participantId),
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(248, 249, 251),
            Font = UiKit.Small,
            DetectUrls = false,
            Margin = new Padding(0, 6, 0, 8),
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        col.Controls.Add(text, 0, 1);

        _agree = new CheckBox { Text = "위 내용을 읽었고, 연구 참여와 자동 전송에 동의합니다. (필수)", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        col.Controls.Add(_agree, 0, 2);

        _autostart = new CheckBox { Text = "Windows에 로그인하면 자동으로 켜기 (권장 — 문제는 언제 생길지 모르니까요)", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 2) };
        col.Controls.Add(_autostart, 0, 3);

        var nickRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        nickRow.Controls.Add(UiKit.Label("별명 (선택):"));
        _nickname = new TextBox { Width = 150, MaxLength = 20, Margin = new Padding(6, 2, 6, 0) };
        nickRow.Controls.Add(_nickname);
        nickRow.Controls.Add(UiKit.Label("연구팀이 누구 기록인지 알아보는 용도 — 실명 대신 별명을 권해요", UiKit.Small, UiKit.Muted));
        col.Controls.Add(nickRow, 0, 4);

        _ok = UiKit.Button("동의하고 시작", primary: true);
        _ok.Enabled = false;
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var no = UiKit.Button("참여하지 않음");
        no.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        col.Controls.Add(UiKit.ButtonRow(_ok, no), 0, 5);

        _agree.CheckedChanged += (_, _) =>
        {
            _ok.Enabled = _agree.Checked;
            _ok.BackColor = _agree.Checked ? UiKit.Accent : Color.Silver;
        };
        _ok.BackColor = Color.Silver;

        Controls.Add(col);
        AcceptButton = _ok;
        CancelButton = no;
    }
}
