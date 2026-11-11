using System.Runtime.InteropServices;

namespace BlackBox.Core;

/// <summary>
/// 프로세스의 로드된 모듈 경로 열거 — Toolhelp32(CreateToolhelp32Snapshot + Module32FirstW/NextW). 읽기 전용.
/// 스파이크 J(2026-10-29, 비관리자, 546 프로세스): .NET Process.Modules 전수 6.1초 vs Toolhelp32 0.33초(18배).
/// 열거되는 프로세스 집합과 모듈 집합은 같고, Toolhelp32 는 WOW64 프로세스의 실행 파일을 한 번 더 내므로 중복을 제거한다.
/// 접근 거부(보호·타 사용자 프로세스)는 null 을 돌려준다 — 호출자가 "denied" 로 센다.
/// </summary>
public static class ModuleEnumerator
{
    private const uint TH32CS_SNAPMODULE = 0x00000008;
    private const uint TH32CS_SNAPMODULE32 = 0x00000010;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>모듈 전체 경로(중복 제거, 열거 순서 유지). 실패하면 null.</summary>
    public static List<string>? ModulePaths(int pid)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, (uint)pid);
        if (snap == InvalidHandle) return null;
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (!Module32FirstW(snap, ref me)) return list;
            do
            {
                var path = me.szExePath;
                if (!string.IsNullOrEmpty(path) && seen.Add(path)) list.Add(path);
            } while (Module32NextW(snap, ref me));
            return list;
        }
        finally { CloseHandle(snap); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MODULEENTRY32W
    {
        public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
    }
}
