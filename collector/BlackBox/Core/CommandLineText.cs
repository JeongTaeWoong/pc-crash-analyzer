namespace BlackBox.Core;

/// <summary>명령줄 문자열 다루기 (읽기만).</summary>
public static class CommandLineText
{
    public const string StrippedMarker = " <인자 생략>";

    /// <summary>
    /// 명령줄에서 실행 파일 부분만 남긴다. 인자에는 접속 토큰·주소·문서 이름이 들어갈 수 있어 참여자판은 인자를 남기지 않는다(CLAUDE.md 6).
    /// "C:\Program Files\X\x.exe" --token=… → "C:\Program Files\X\x.exe" &lt;인자 생략&gt;
    /// </summary>
    public static string ExecutableOnly(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        var s = cmd.Trim();
        string exe;
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            exe = end > 0 ? s[..(end + 1)] : s;
        }
        else
        {
            // 따옴표 없는 공백 경로(C:\Program Files\...)만 ".exe" 까지 남긴다: 드라이브 경로로 시작하고, ".exe" 앞에 옵션(" -", " /")이 없을 때.
            // 그 밖은 첫 공백까지 — `curl -H "Authorization: …" -o setup.exe` 가 통째로 남던 문제(10/29 검토 지적)
            int dotExe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            bool drivePath = s.Length > 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/');
            if (drivePath && dotExe > 0 && (dotExe + 4 == s.Length || s[dotExe + 4] == ' ') &&
                !s[..dotExe].Contains(" -") && !s[..dotExe].Contains(" /") && !s[..dotExe].Contains('"'))
                exe = s[..(dotExe + 4)];
            else
            {
                int sp = s.IndexOf(' ');
                exe = sp > 0 ? s[..sp] : s;
            }
        }
        return exe.Length < s.Length ? exe + StrippedMarker : exe;
    }
}
