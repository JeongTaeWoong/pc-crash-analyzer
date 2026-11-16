using System.Text.RegularExpressions;

namespace CrashAnalyzer.Ai;

/// <summary>
/// AI 의 권고를 사용자에게 보여 주기 전에 코드로 한 번 거른다. 지시문으로 부탁하는 것과 별개로, 지키지 않은 권고가
/// 표시 없이 화면에 나가지 않게 하는 장치다(원칙: 권한은 코드로 제한 — 프롬프트를 믿지 않는다).
///
/// 왜 이 세 가지인가: 2026-11-16 개발용 답 50개를 채점했을 때 해로운 권고는 두 종류였다 — 원인이 아닌 것을 건드리기
/// ("그래픽 드라이버도 업데이트", "Visual C++ 재배포 패키지 복구", "메모리 증설": 10건)와 사용자의 작업을 첫 조치로 끝내기(6건).
/// 보안 끄기는 한 건도 없었지만 제품 원칙이라 함께 본다.
///
/// 코드가 잡을 수 있는 것만 잡는다.
///  - "원인이 아닌 것"은 이름 대조로 하지 않는다(후보 MsMpEng.exe 와 권고의 "Microsoft Defender" 는 같은 것이다).
///    대신 종류로 본다: 권고가 드라이버·런타임·하드웨어를 건드리는데 후보 가운데 그 종류가 하나도 없을 때.
///  - "그래도 반복되면 ~" 처럼 조건을 단 다음 단계는 걸지 않는다(채점표와 같은 기준).
///  - "사용자의 작업인지"는 글만으로 알 수 없어 여기서 다루지 않는다.
/// 판정은 표시일 뿐 권고를 지우지 않는다 — 무엇이 걸렸는지 사용자가 볼 수 있어야 한다.
/// </summary>
public static class AdviceCheck
{
    public enum Flag
    {
        /// <summary>원인 후보에 없는 종류(드라이버·런타임·하드웨어)를 조건 없이 건드리는 조치.</summary>
        NotTheCause,
        /// <summary>백신·보안 기능을 끄거나 지우라는 조치.</summary>
        DisablesSecurity,
        /// <summary>Windows 를 다시 설치·초기화하거나 레지스트리를 고치라는 조치.</summary>
        SystemDamage,
    }

    public sealed record Finding(int ActionIndex, Flag Flag, string Why);

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex Security = new(@"백신|보안\s*프로그램|실시간\s*(보호|감시)|defender|디펜더|방화벽|antivirus|firewall|smartscreen", Opt);
    // 한국어 활용형: 끄다 → 끕니다·끌·끈·꺼 (글자가 달라진다)
    private static readonly Regex TurnOffOrRemove = new(@"끄|끕|끌|끈|꺼|해제|비활성|중지|중단|제거|삭제|지우|지워|지웁|disable|uninstall|turn\s*off|remove", Opt);
    private static readonly Regex NotTurningOff = new(@"끄지\s*(마|말|않)|끄면\s*안|지우지\s*(마|말|않)|예외|제외|예약|일정", Opt);
    private static readonly Regex Damage = new(@"(윈도우|windows)\s*(를|을)?\s*(다시\s*설치|재설치|초기화|포맷)|레지스트리.{0,12}(삭제|편집|수정)", Opt);
    private static readonly Regex Conditional = new(@"반복되면|계속되면|계속\s*나|재발하면|다시\s*(나|생기|발생)|해결되지\s*않|안\s*되면|그래도|지속되면|자주\s*(나|생기|반복)|필요하면", Opt);

    /// <summary>건드리는 대상의 종류. 후보의 kind·이름·이유에 같은 종류가 있으면 "원인 쪽"으로 본다.</summary>
    private static readonly (string Name, Regex InAction, Regex InCandidate, string[] Kinds)[] Classes =
    [
        ("드라이버", new(@"드라이버|driver", Opt), new(@"드라이버|driver|\.sys\b|그래픽|gpu|디스플레이", Opt), ["driver"]),
        ("런타임", new(@"visual\s*c\+\+|재배포|런타임|runtime|\.net\s*(런타임|프레임워크|framework)|directx", Opt), new(@"visual\s*c\+\+|vcruntime|msvcp|ucrtbase|런타임|runtime|\.net|clr|directx", Opt), []),
        ("하드웨어", new(@"(메모리|램|ram)\s*(를|을)?\s*(검사|진단|증설|교체|추가)|디스크\s*(를|을)?\s*(검사|점검|교체)|ssd|하드웨어\s*점검|bios|바이오스|펌웨어", Opt),
                     new(@"메모리|램|ram|디스크|ssd|저장\s*장치|하드웨어|bios|펌웨어|온도|전원", Opt), ["hardware"]),
    ];

    public static IReadOnlyList<Finding> Check(AiAnswer answer)
    {
        var found = new List<Finding>();
        for (int i = 0; i < answer.Actions.Count; i++)
        {
            var a = answer.Actions[i];
            string text = $"{a.Action} {a.Target}";

            if (Security.IsMatch(text) && TurnOffOrRemove.IsMatch(a.Action) && !NotTurningOff.IsMatch(a.Action))
                found.Add(new Finding(i, Flag.DisablesSecurity, "백신·보안 기능을 끄거나 지우는 조치는 권하지 않습니다. 검사 시간을 옮기거나 폴더를 예외로 두는 방법이 있습니다."));
            if (Damage.IsMatch(text))
                found.Add(new Finding(i, Flag.SystemDamage, "Windows 를 다시 설치하거나 레지스트리를 고치는 조치는 근거가 분명할 때만 해야 합니다."));

            if (answer.Candidates.Count == 0 || Conditional.IsMatch($"{a.Action} {a.Why}")) continue;
            foreach (var (name, inAction, inCandidate, kinds) in Classes)
            {
                if (!inAction.IsMatch(text)) continue;
                bool covered = answer.Candidates.Any(c => kinds.Contains(c.Kind) || inCandidate.IsMatch($"{c.Name} {c.Reason}"));
                if (!covered)
                    found.Add(new Finding(i, Flag.NotTheCause, $"원인 후보에 {name} 쪽이 없는데 {name}을(를) 건드리는 조치입니다. 위의 조치로 해결되지 않을 때만 고려하세요."));
            }
        }
        return found;
    }
}
