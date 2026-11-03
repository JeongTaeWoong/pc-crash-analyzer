using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CrashAnalyzer.UI.Services;

namespace CrashAnalyzer.UI;

public partial class App : Application
{
    private void OnAppStartup(object sender, StartupEventArgs e)
    {
        // --selftest [출력파일]: GUI 없이 엔진 연결·진단 변환을 검증하고 종료 (CI/스모크용)
        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            string outPath = e.Args.Length >= 2 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "crashui_selftest.txt");
            int code = 0;
            string text;
            try
            {
                string root = RepoPaths.FindRepoRoot() ?? throw new InvalidOperationException("저장소 루트를 찾지 못함");
                var engine = new EngineClient(RepoPaths.ResolveEnginePath(root));
                string snap = DumpLocator.FindLatestEngineSnapshot(RepoPaths.ResolveDumpsRoot(root))
                              ?? Path.Combine(root, "engine", "data", "samples", "s1_anticheat_crash.json");
                var result = engine.CallTool("diagnose", new JsonObject { ["snapshot_path"] = snap });
                var vm = Models.ViewMap.FromDiagnose(result);
                text = $"OK snapshot={Path.GetFileName(Path.GetDirectoryName(snap) ?? snap)}/{Path.GetFileName(snap)} " +
                       $"layer={vm.LayerTop}({vm.LayerConfidencePct}%) cand1={vm.Candidates.FirstOrDefault()?.Label} " +
                       $"cands={vm.Candidates.Count} steps={vm.Steps.Count}";
            }
            catch (Exception ex)
            {
                code = 1;
                text = "FAIL " + ex.Message;
            }
            File.WriteAllText(outPath, text);
            Shutdown(code);
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var win = new MainWindow();
        MainWindow = win;

        // --open <스냅샷경로>: 시작하자마자 해당 스냅샷 진단 (시연용)
        int openIdx = Array.IndexOf(e.Args, "--open");
        string? openPath = openIdx >= 0 && openIdx + 1 < e.Args.Length ? e.Args[openIdx + 1] : null;

        // --shot <png경로>: 화면 밖에서 창을 렌더해 PNG 저장 후 종료 (사용자 화면을 캡처하지 않는 자가 검수용)
        int shotIdx = Array.IndexOf(e.Args, "--shot");
        string? shotPath = shotIdx >= 0 && shotIdx + 1 < e.Args.Length ? e.Args[shotIdx + 1] : null;

        if (shotPath != null)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -16000; win.Top = 0;
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
        }
        // --rehearse <png> <txt>: 오프스크린에서 수집기 기동→크래시 자동 진단 시나리오 전체 검증
        int rhIdx = Array.IndexOf(e.Args, "--rehearse");
        if (rhIdx >= 0 && rhIdx + 2 < e.Args.Length)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -16000; win.Top = 0;
            win.ShowActivated = false;
            win.ShowInTaskbar = false;
            win.SuppressDialogs = true;
            string png = e.Args[rhIdx + 1], txt = e.Args[rhIdx + 2];
            win.Loaded += (_, _) => win.StartRehearse(png, txt);
            win.Show();
            return;
        }

        // --record-demo <marker.txt>: 촬영용 — 고정 위치·최상위 창에서 수집 시작→로그 탭을 자동 수행,
        // 크래시 자동 진단이 끝나면 marker 파일을 써서 외부 녹화 스크립트(tools/record-demo.ps1)에 알린다.
        int rdIdx = Array.IndexOf(e.Args, "--record-demo");
        if (rdIdx >= 0)
        {
            string marker = rdIdx + 1 < e.Args.Length
                ? e.Args[rdIdx + 1]
                : Path.Combine(Path.GetTempPath(), "crashui_demo_marker.txt");
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = 60; win.Top = 60; win.Width = 1600; win.Height = 900;
            win.Topmost = true; // 녹화 영역에 다른 창이 끼어들지 않도록
            win.Loaded += (_, _) => win.StartRecordDemo(marker);
            win.Show();
            return;
        }

        if (openPath != null)
            win.Loaded += (_, _) => win.OpenSnapshotOnStartup(openPath);
        if (shotPath != null)
            win.Loaded += async (_, _) =>
            {
                await Task.Delay(3500); // 진단·레이아웃 완료 대기
                win.SaveWindowPng(shotPath);
                Shutdown(0);
            };
        win.Show();
    }
}
