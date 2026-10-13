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
        win.Show();
    }
}
