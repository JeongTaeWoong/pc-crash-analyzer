using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace BlackBox.Participant.Ui;

/// <summary>최근 기록 목록 — 참여자가 "무엇이 저장·전송됐는지" 직접 볼 수 있게. 메모를 나중에 붙일 수 있다.</summary>
internal sealed class RecordsForm : Form
{
    public sealed record Row(string Name, string Dir, DateTimeOffset MarkTime, string Kind, string? Process, string? Note, string Upload);

    private readonly ListView _list;

    /// <summary>메모 남기기를 누름 (덤프 폴더 이름).</summary>
    public event Action<Row>? AnnotateRequested;

    public RecordsForm(IReadOnlyList<Row> rows, string dumpRoot)
    {
        UiKit.Setup(this, $"{Texts.AppTitle} — 최근 기록", new Size(760, 460));
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(560, 300);

        var col = UiKit.Column(3);
        col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        col.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        col.Controls.Add(UiKit.Label(rows.Count == 0
            ? "아직 기록이 없어요. 문제가 생기면 자동으로, 또는 Ctrl+Shift+F12 로 기록돼요."
            : $"기록 {rows.Count}건 · 전송 상태는 몇 분마다 갱신돼요", UiKit.Small, UiKit.Muted), 0, 0);

        _list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
        _list.Columns.Add("시각", 130);
        _list.Columns.Add("종류", 150);
        _list.Columns.Add("프로그램", 150);
        _list.Columns.Add("증상·메모", 190);
        _list.Columns.Add("전송", 90);
        foreach (var r in rows)
        {
            var item = new ListViewItem(new[] { r.MarkTime.ToString("MM-dd HH:mm:ss"), Texts.KindLabel(r.Kind), r.Process ?? "", r.Note ?? "", r.Upload }) { Tag = r };
            _list.Items.Add(item);
        }
        col.Controls.Add(_list, 0, 1);

        var annotate = UiKit.Button("메모 남기기", primary: true);
        annotate.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count == 0) { MessageBox.Show(this, "메모를 남길 기록을 먼저 고르세요.", Texts.AppTitle); return; }
            AnnotateRequested?.Invoke((Row)_list.SelectedItems[0].Tag!);
        };
        var open = UiKit.Button("폴더 열기");
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(dumpRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dumpRoot}\"") { UseShellExecute = true });
        };
        var close = UiKit.Button("닫기");
        close.Click += (_, _) => Close();
        col.Controls.Add(UiKit.ButtonRow(close, open, annotate), 0, 2);
        Controls.Add(col);
        CancelButton = close;
    }

    /// <summary>덤프 폴더들의 meta.json·report.json 에서 목록을 만든다(최근 200건).</summary>
    public static List<Row> LoadRows(string dumpRoot, Func<string, string> uploadState)
    {
        var rows = new List<Row>();
        foreach (var d in LocalData.DumpDirs(dumpRoot).AsEnumerable().Reverse().Take(200))
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.FullName, "meta.json")));
                var m = meta.RootElement;
                var kind = m.GetProperty("kind").GetString() ?? "?";
                var at = m.GetProperty("markTime").GetDateTimeOffset();
                string? process = null, note = null;
                if (m.TryGetProperty("trigger", out var t))
                {
                    var trig = t.TryGetProperty("primary", out var p) ? p : t;
                    process = kind == "manual" ? Str(trig, "target") : Texts.IsAppKind(kind) ? Str(trig, "ProcessName") : null;
                    var sym = Str(trig, "symptom");
                    if (sym != null) note = Texts.SymptomLabel(sym);
                }
                var reportPath = Path.Combine(d.FullName, "report.json");
                if (File.Exists(reportPath))
                {
                    using var rep = JsonDocument.Parse(File.ReadAllText(reportPath));
                    var sym = Str(rep.RootElement, "symptom");
                    var memo = Str(rep.RootElement, "memo");
                    note = string.Join(" · ", new[] { sym != null ? Texts.SymptomLabel(sym) : null, memo }.Where(x => !string.IsNullOrEmpty(x)));
                }
                rows.Add(new Row(d.Name, d.FullName, at, kind, process, note, uploadState(d.Name)));
            }
            catch { /* 쓰는 중이거나 손상된 폴더 — 건너뛴다 */ }
        }
        return rows;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
