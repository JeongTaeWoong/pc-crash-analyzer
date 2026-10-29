using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace BlackBox.Participant;

/// <summary>
/// "Windows 로그인 시 자동 실행" — 참여자가 동의 화면·트레이 메뉴에서 켰을 때만, 사용자 시작 프로그램 폴더에 바로가기(.lnk) 하나를 만든다.
/// 레지스트리·서비스·예약 작업을 쓰지 않는다. 작업 관리자 "시작 프로그램" 탭과 시작 프로그램 폴더에서 그대로 보이고, 지우면 끝이다.
/// 참여자가 직접 지운 바로가기는 다시 만들지 않는다(선택 존중) — 폴더를 옮긴 경우에만 대상 경로를 고친다.
/// </summary>
public static class StartupShortcut
{
    public const string FileName = "PC 블랙박스 (캡스톤 연구).lnk";

    public static string DefaultFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static string PathIn(string? folder) => Path.Combine(folder ?? DefaultFolder, FileName);

    public static bool Exists(string? folder = null) => File.Exists(PathIn(folder));

    public static void Create(string exePath, string? folder = null)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetArguments("--autostart");
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetDescription("PC 블랙박스 — 중앙대 캡스톤 연구용 문제 상황 기록기 (트레이 메뉴 '참여 그만두기'로 끌 수 있음)");
            link.SetIconLocation(exePath, 0);
            Directory.CreateDirectory(folder ?? DefaultFolder);
            ((IPersistFile)link).Save(PathIn(folder), true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static string? Target(string? folder = null)
    {
        var path = PathIn(folder);
        if (!File.Exists(path)) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        catch { return null; }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void Remove(string? folder = null)
    {
        var path = PathIn(folder);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>바로가기가 있는데 다른 exe 를 가리키면(폴더를 옮김) 현재 exe 로 고친다. 없으면 만들지 않는다. 반환: 지금 바로가기가 있는가.</summary>
    public static bool RepairIfMoved(string exePath, string? folder = null)
    {
        var target = Target(folder);
        if (target == null) return Exists(folder);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
            Create(exePath, folder);
        return true;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
