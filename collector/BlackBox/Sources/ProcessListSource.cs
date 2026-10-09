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

    protected override object Sample()
    {
        _tick++;
        var sw = Stopwatch.StartNew();
        var procs = Process.GetProcesses();
        try
        {
            var basic = new List<object>(procs.Length);
            var alive = new HashSet<int>();
            foreach (var p in procs)
            {
                try
                {
                    basic.Add(new { pid = p.Id, name = p.ProcessName, wsMB = Math.Round(p.WorkingSet64 / 1048576.0, 1) });
                    alive.Add(p.Id);
                }
                catch { }
            }

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
                    int fgPid = GetForegroundPid();
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

    private static int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
