using System.Diagnostics;
using System.Runtime.InteropServices;
using BlackBox.Core;

namespace BlackBox.Sources;

public enum SnapshotStrategy
{
    /// <summary>60초마다 전체 프로세스 모듈 열거, 그 사이 10초 틱에는 새로 생긴 프로세스만.</summary>
    FullSweepEvery60s,

    /// <summary>10초마다 전경 창 프로세스 + 최근 60초 내 시작된 프로세스만 모듈 열거.</summary>
    ForegroundPlusRecent,
}

/// <summary>
/// 프로세스 목록(10초 주기) + 선별 대상의 비시스템 모듈 목록을 버퍼에 넣는다.
/// 크래시 이벤트가 도착한 시점엔 프로세스가 이미 죽어 있으므로,
/// 죽기 직전(최대 10초 전)의 모듈 목록이 버퍼에 남아 있게 하는 것이 목적.
/// 각 프로세스에 "보이는 창 보유(win)" 플래그와 전경 PID(fgPid)를 함께 기록 — 유의성 점수의 "사용자 대면" 판정용.
/// </summary>
public sealed class ProcessListSource : PolledSourceBase
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string[] SystemRoots =
    {
        Path.Combine(WinDir, "System32"),
        Path.Combine(WinDir, "SysWOW64"),
        Path.Combine(WinDir, "WinSxS"),
    };

    private readonly SnapshotStrategy _strategy;
    private readonly Dictionary<int, DateTime> _seen = new(); // pid → 처음 관측한 시각
    private long _tick = -1;

    public ProcessListSource(SnapshotStrategy strategy) : base("processes", TimeSpan.FromSeconds(10))
        => _strategy = strategy;

    /// <summary>벤치 전용 — 운영 코드에서 쓰지 않는다.</summary>
    internal object SampleOnceForBench() => Sample();

    /// <summary>롤업용 요약: Private Bytes 상위 12 + 전경 프로세스.</summary>
    public ProcessSummary? LatestSummary { get; private set; }

    protected override object Sample()
    {
        _tick++;
        var sw = Stopwatch.StartNew();
        var procs = Process.GetProcesses();
        try
        {
            int fgPid = GetForegroundPid();
            var winPids = VisibleWindowPids(); // EnumWindows 1회 — Process.MainWindowHandle를 400번 묻는 것보다 훨씬 싸다

            var basic = new List<object>(procs.Length);
            var rows = new List<ProcSummary>(procs.Length);
            var alive = new HashSet<int>();
            foreach (var p in procs)
            {
                try
                {
                    var row = new ProcSummary(p.Id, p.ProcessName, Math.Round(p.PrivateMemorySize64 / 1048576.0, 1), p.HandleCount, winPids.Contains(p.Id));
                    rows.Add(row);
                    basic.Add(new
                    {
                        pid = p.Id,
                        name = p.ProcessName,
                        wsMB = Math.Round(p.WorkingSet64 / 1048576.0, 1),
                        privMB = row.PrivMB, // 느려짐 A 메커니즘: 기울기 계산의 원천
                        handles = row.Handles,
                        win = row.Win,
                    });
                    alive.Add(p.Id);
                }
                catch { }
            }
            var summary = rows.OrderByDescending(r => r.PrivMB).Take(12).ToList();
            if (fgPid > 0 && summary.All(r => r.Pid != fgPid) && rows.FirstOrDefault(r => r.Pid == fgPid) is { } fgRow) summary.Add(fgRow);
            LatestSummary = new ProcessSummary(DateTimeOffset.Now, fgPid, summary);

            // 모듈 열거 대상 선정
            var targets = new List<Process>();
            switch (_strategy)
            {
                case SnapshotStrategy.FullSweepEvery60s:
                    if (_tick % 6 == 0)
                        targets.AddRange(procs);
                    else
                        targets.AddRange(procs.Where(p => !_seen.ContainsKey(p.Id)));
                    break;

                case SnapshotStrategy.ForegroundPlusRecent:
                    var cutoff = DateTime.Now.AddSeconds(-60);
                    foreach (var p in procs)
                    {
                        bool pick = p.Id == fgPid;
                        if (!pick)
                        {
                            try { pick = p.StartTime >= cutoff; }
                            catch { /* 타 사용자/보호 프로세스 — StartTime 접근 불가 */ }
                        }
                        if (pick) targets.Add(p);
                    }
                    break;
            }

            var modules = new List<object>();
            int moduleDenied = 0;
            foreach (var p in targets.DistinctBy(x => x.Id))
            {
                try
                {
                    var nonSystem = new List<string>();
                    foreach (ProcessModule m in p.Modules)
                    {
                        var file = m.FileName;
                        if (!string.IsNullOrEmpty(file) && !SystemRoots.Any(r => file.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                            nonSystem.Add(file);
                    }
                    modules.Add(new { pid = p.Id, name = p.ProcessName, mods = nonSystem.Take(40).ToList() });
                }
                catch { moduleDenied++; }
            }

            // seen 갱신: 죽은 pid 제거(재사용 대비) 후 현재 pid 추가
            foreach (var dead in _seen.Keys.Where(k => !alive.Contains(k)).ToList())
                _seen.Remove(dead);
            foreach (var id in alive)
                _seen.TryAdd(id, DateTime.Now);

            return new
            {
                count = basic.Count,
                strategy = _strategy.ToString(),
                scanMs = sw.ElapsedMilliseconds,
                fgPid,
                moduleScanned = modules.Count,
                moduleDenied,
                processes = basic,
                modules,
            };
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>전경 창의 PID (없으면 -1). StateSnapshotter 도 같은 구현을 쓴다.</summary>
    internal static int GetForegroundPid()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return -1;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch { return -1; }
    }

    private static HashSet<int> VisibleWindowPids()
    {
        var set = new HashSet<int>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h))
            {
                GetWindowThreadProcessId(h, out uint pid);
                set.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return set;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
