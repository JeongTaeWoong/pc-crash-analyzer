using System.Drawing;
using System.Windows.Forms;

namespace BlackBox.Participant.Ui;

/// <summary>참여 그만두기 — 자동 실행을 끄고, 고르면 이 PC 기록 삭제·연구팀에 삭제 요청.</summary>
internal sealed class WithdrawForm : Form
{
    private readonly CheckBox _requestDelete;
    private readonly CheckBox _deleteLocal;

    public bool RequestServerDelete => _requestDelete.Checked;
    public bool DeleteLocal => _deleteLocal.Checked;

    public WithdrawForm(string participantId)
    {
        UiKit.Setup(this, $"{Texts.AppTitle} — 참여 그만두기", new Size(520, 300));
        var col = UiKit.Column(6);
        for (int i = 0; i < 6; i++) col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        col.Controls.Add(UiKit.Label("참여를 그만두시겠어요?", UiKit.Title, UiKit.Accent), 0, 0);
        col.Controls.Add(UiKit.Label("기록을 멈추고 자동 실행을 끕니다. 그동안 도와주셔서 감사합니다.", UiKit.Body), 0, 1);
        _requestDelete = new CheckBox { Text = "연구팀에 보낸 내 기록도 지워 달라고 요청하기", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 0, 2) };
        col.Controls.Add(_requestDelete, 0, 2);
        _deleteLocal = new CheckBox { Text = "이 PC에 남은 기록도 지우기", AutoSize = true, Checked = true, Margin = new Padding(0, 2, 0, 2) };
        col.Controls.Add(_deleteLocal, 0, 3);
        col.Controls.Add(UiKit.Label($"참여 ID: {participantId} — 나중에 삭제를 따로 요청하실 때 이 ID를 알려 주세요.", UiKit.Small, UiKit.Muted), 0, 4);
        var ok = UiKit.Button("참여 그만두기", primary: true);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = UiKit.Button("취소");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        col.Controls.Add(UiKit.ButtonRow(ok, cancel), 0, 5);
        Controls.Add(col);
        CancelButton = cancel;
    }
}
