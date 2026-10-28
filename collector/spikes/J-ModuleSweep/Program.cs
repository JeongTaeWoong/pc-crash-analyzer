// 스파이크 J (2026-10-29): 전체 프로세스 비시스템 모듈 열거 비용 비교 — 읽기 전용.
// A) .NET Process.Modules (현재 StateSnapshotter 방식)
// B) Toolhelp32: CreateToolhelp32Snapshot(TH32CS_SNAPMODULE|SNAPMODULE32) + Module32FirstW/NextW
// 같은 결과(프로세스별 비시스템 모듈 수)가 나오는지와 소요 시간을 비교한다.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string[] roots = { Path.Combine(winDir, "System32"), Path.Combine(winDir, "SysWOW64"), Path.Combine(winDir, "WinSxS") };
bool NonSystem(string f) => !string.IsNullOrEmpty(f) && !roots.Any(r => f.StartsWith(r, StringComparison.OrdinalIgnoreCase));

var pids = Process.GetProcesses().Select(p => { var id = p.Id; p.Dispose(); return id; }).ToList();

// A — StateSnapshotter 와 같은 방식: GetProcesses() 한 번 → 각 p.Modules (GetProcessById 반복은 매번 전체 열거라 불공정)
var swA = Stopwatch.StartNew();
var a = new Dictionary<int, int>(); int deniedA = 0;
foreach (var p in Process.GetProcesses())
{
    try
    {
        int n = 0;
        foreach (ProcessModule m in p.Modules) if (NonSystem(m.FileName)) n++;
        a[p.Id] = n;
    }
    catch { deniedA++; }
    finally { p.Dispose(); }
}
long msA = swA.ElapsedMilliseconds;

// B
var swB = Stopwatch.StartNew();
var b = new Dictionary<int, int>(); int deniedB = 0;
foreach (var pid in pids)
{
    var snap = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, (uint)pid); // TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32
    if (snap == new IntPtr(-1)) { deniedB++; continue; }
    try
    {
        var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
        int n = 0;
        if (Module32FirstW(snap, ref me))
        {
            do { if (NonSystem(me.szExePath)) n++; } while (Module32NextW(snap, ref me));
        }
        b[pid] = n;
    }
    finally { CloseHandle(snap); }
}
long msB = swB.ElapsedMilliseconds;

if (args.Contains("diff"))
{
    // 차이 나는 프로세스 3개의 모듈 경로 차집합 (경로는 화면에만, 파일로 남기지 않음)
    foreach (var pid in a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k]).Take(3))
    {
        var setA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listA = new List<string>();
        try { using var p = Process.GetProcessById(pid); foreach (ProcessModule m in p.Modules) if (NonSystem(m.FileName)) { listA.Add(m.FileName); setA.Add(m.FileName); } } catch { }
        var setB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listB = new List<string>();
        var snap = CreateToolhelp32Snapshot(0x00000008 | 0x00000010, (uint)pid);
        if (snap != new IntPtr(-1))
        {
            var me = new MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<MODULEENTRY32W>() };
            if (Module32FirstW(snap, ref me)) do { if (NonSystem(me.szExePath)) { listB.Add(me.szExePath); setB.Add(me.szExePath); } } while (Module32NextW(snap, ref me));
            CloseHandle(snap);
        }
        string name = ""; try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
        Console.WriteLine($"pid {pid} {name}: dotnet {listA.Count} (고유 {setA.Count}) / toolhelp {listB.Count} (고유 {setB.Count})");
        foreach (var x in setA.Except(setB).Take(4)) Console.WriteLine("  only dotnet: " + x);
        foreach (var x in setB.Except(setA).Take(4)) Console.WriteLine("  only toolhelp: " + x);
        var dupA = listA.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"{Path.GetFileName(g.Key)}×{g.Count()}").Take(4);
        Console.WriteLine("  dotnet 중복: " + string.Join(", ", dupA));
    }
}

int both = a.Keys.Intersect(b.Keys).Count();
int same = a.Keys.Intersect(b.Keys).Count(k => a[k] == b[k]);
Console.WriteLine(JsonSerializer.Serialize(new
{
    signal = "J-ModuleSweep",
    processes = pids.Count,
    dotnet = new { ms = msA, enumerated = a.Count, denied = deniedA, nonSystemModules = a.Values.Sum() },
    toolhelp = new { ms = msB, enumerated = b.Count, denied = deniedB, nonSystemModules = b.Values.Sum() },
    bothEnumerated = both,
    sameCount = same,
    onlyToolhelp = b.Keys.Except(a.Keys).Count(),
    onlyDotnet = a.Keys.Except(b.Keys).Count(),
}, new JsonSerializerOptions { WriteIndented = true }));


[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);
[DllImport("kernel32.dll", SetLastError = true)]
static extern bool CloseHandle(IntPtr h);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct MODULEENTRY32W
{
    public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage;
    public IntPtr modBaseAddr;
    public uint modBaseSize;
    public IntPtr hModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath;
}
