namespace BlackBox.Participant;

/// <summary>
/// 명령줄 옵션. 참여자는 인자 없이 실행한다(자동 실행 바로가기만 --autostart).
/// --test-* 와 --startup-dir 은 study.json 의 수신 주소가 127.0.0.1 시험 수신기일 때만 허용된다 — 배포본에서 동의 화면을 건너뛸 길을 막는다.
/// </summary>
public sealed class AppOptions
{
    public static readonly string DefaultDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcCrashAnalyzer");

    public string DataDir { get; private set; } = DefaultDataDir;

    /// <summary>자동 실행 바로가기에 넘길 데이터 폴더 — 기본 폴더면 null(참여자 실행).</summary>
    public string? ShortcutDataDir => DataDirIsDefault ? null : DataDir;
    public bool DataDirIsDefault => string.Equals(Path.GetFullPath(DataDir).TrimEnd('\\'), Path.GetFullPath(DefaultDataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public string StudyConfigPath { get; private set; } = Path.Combine(AppContext.BaseDirectory, "study.json");

    /// <summary>시작 프로그램 바로가기로 켜짐 — 환영 알림을 띄우지 않는다.</summary>
    public bool Autostart { get; private set; }

    /// <summary>팀 점검용: 창 없이 마킹 1건 → 봉투 → 전송 → 하트비트까지 해 보고 결과를 출력한다.</summary>
    public bool SelfTest { get; private set; }

    public int PostSeconds { get; private set; } = 30;
    public string? OutFile { get; private set; }

    /// <summary>화면 점검용: 창들을 그림 파일로 저장하고 끝난다(설정 불필요).</summary>
    public string? RenderFormsDir { get; private set; }

    public bool TestConsent { get; private set; }
    public int TestMarkAfterSec { get; private set; }
    public bool TestAutoAnswer { get; private set; }
    public int TestExitAfterSec { get; private set; }
    /// <summary>시험: N초 뒤 "참여 그만두기"(삭제 요청 + 로컬 삭제)를 창 없이 실행 — 철회 순서 검증용.</summary>
    public int TestWithdrawAfterSec { get; private set; }
    public string? StartupDir { get; private set; }

    public bool Headless => SelfTest || RenderFormsDir != null;

    public bool HasTestFlags => TestConsent || TestMarkAfterSec > 0 || TestAutoAnswer || TestExitAfterSec > 0 || TestWithdrawAfterSec > 0 || StartupDir != null;

    /// <summary>같은 데이터 폴더로 두 개가 돌지 않게 한다.</summary>
    public string MutexName => @"Local\PcBlackBox-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DataDir.ToLowerInvariant())))[..12];

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} 뒤에 값이 필요합니다");
            int NextInt() => int.TryParse(Next(), out var v) ? v : throw new ArgumentException($"{a} 값은 정수");
            switch (a)
            {
                case "--data-dir": o.DataDir = Path.GetFullPath(Next()); break;
                case "--study-config": o.StudyConfigPath = Path.GetFullPath(Next()); break;
                case "--autostart": o.Autostart = true; break;
                case "--selftest": o.SelfTest = true; break;
                case "--post-seconds": o.PostSeconds = Math.Clamp(NextInt(), 1, 120); break;
                case "--out": o.OutFile = Path.GetFullPath(Next()); break;
                case "--render-forms": o.RenderFormsDir = Path.GetFullPath(Next()); break;
                case "--test-consent": o.TestConsent = true; break;
                case "--test-mark-after": o.TestMarkAfterSec = NextInt(); break;
                case "--test-auto-answer": o.TestAutoAnswer = true; break;
                case "--test-exit-after": o.TestExitAfterSec = NextInt(); break;
                case "--test-withdraw-after": o.TestWithdrawAfterSec = NextInt(); break;
                case "--startup-dir": o.StartupDir = Path.GetFullPath(Next()); break;
                default: break; // 모르는 인자는 무시(바로가기 인자 호환)
            }
        }
        return o;
    }
}
