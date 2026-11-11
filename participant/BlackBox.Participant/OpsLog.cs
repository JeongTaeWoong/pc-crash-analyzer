using System.Text;
using BlackBox.Core;

namespace BlackBox.Participant;

/// <summary>
/// 운영 기록(시작·전송 성공/실패 등) — 메모리에만 최근 300줄. 디스크에 쓰지 않는다(CLAUDE.md 2).
/// 하트비트에 끝부분이 실려 팀이 "왜 안 올라오지?"를 원격으로 볼 수 있다. 실을 때 경로·이름을 치환한다.
/// </summary>
public static class OpsLog
{
    private const int Capacity = 300;
    private static readonly LinkedList<string> Lines = new();
    private static readonly object Gate = new();
    private static TextWriter? _tee;

    /// <summary>Console 출력(수집기 코드의 [source]·[dump] 줄)을 여기로 모은다. tee 가 있으면 그쪽에도 쓴다(헤드리스 점검).</summary>
    public static void Install(bool teeToConsole)
    {
        _tee = teeToConsole ? Console.Out : null;
        Console.SetOut(TextWriter.Synchronized(new Writer()));
    }

    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:MM-dd HH:mm:ss} {line}";
        lock (Gate)
        {
            Lines.AddLast(stamped);
            while (Lines.Count > Capacity) Lines.RemoveFirst();
        }
        try { _tee?.WriteLine(line); } catch { }
    }

    public static List<string> Tail(int n)
    {
        lock (Gate)
            return Lines.Skip(Math.Max(0, Lines.Count - n)).Select(l => Sanitizer.SanitizeText(l.Length > 400 ? l[..400] + "…" : l)).ToList();
    }

    private sealed class Writer : TextWriter
    {
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var s = _sb.ToString().TrimEnd('\r');
                _sb.Clear();
                if (s.Length > 0) OpsLog.Write(s);
            }
            else _sb.Append(value);
        }

        public override void WriteLine(string? value) => OpsLog.Write(value ?? "");
    }
}
