namespace CrashAnalyzer.UI.Services.Executor;

// 실행기 — 2026-11-16 추가
//
// 엔진이 정한 조치(action.automation)를 실제로 실행하고, 문제가 생기면 원래 상태로 되돌린다.
//   1) 바꾸기 전 상태를 읽어 기록 파일에 먼저 적는다 (원복 지점)
//   2) 하나씩 바꾸고, 바꿀 때마다 기록한다
//   3) 실행 중 실패하면 바꾼 것을 거꾸로 되돌린다
//   4) 실행 뒤에는 "확인 중"으로 두고, 다음 경우 되돌린다 (되돌리기는 UI 가 부른다)
//      - 점검 결과가 '그대로'/'할 수 없어요' (원인이 아니었음)
//      - 확인 중에 블랙박스가 새 오류를 기록함 (악화)
//      - 사용자가 [지금 되돌리기]를 누름
//      - 다음에 UI 를 켤 때 '실행 중'/'확인 중'으로 남은 기록이 있음 (도중에 PC 가 꺼짐)
//
// 이번 버전에서 자동 실행하는 범위: Lv1~2 (되돌릴 수 있는 조치). Lv3(되돌리기 어려운 조치)·Lv4(직접 작업)는 실행하지 않는다.
// 수집기(PC블랙박스)는 읽기만 하는 원칙이라, 바꾸는 일은 이 실행기(UI 쪽)만 한다.

/// <summary>실행할 조치 하나 (엔진 응답의 action)</summary>
public sealed class ActionRequest
{
    public string Label { get; init; } = "";
    public string Automation { get; init; } = "";
    public string Execution { get; init; } = "guide";
    public bool Reversible { get; init; }
}

public sealed class ActionExecutor
{
    private readonly ISystemOps _real;
    private readonly ISystemOps _dry;
    private readonly object _lock = new();

    public ExecJournal Journal { get; }

    /// <summary>기록이 바뀔 때마다 (작업 스레드에서 불림)</summary>
    public event Action<ExecEntry>? Changed;

    public ActionExecutor(ISystemOps real, ExecJournal journal)
    {
        _real = real;
        _dry = new DryRunSystemOps(real);
        Journal = journal;
    }

    private ISystemOps Ops(ExecEntry e) => e.DryRun ? _dry : _real;

    private void Note(ExecEntry e, string kind, string text)
    {
        e.Events.Add(new ExecEvent { Time = DateTime.Now, Kind = kind, Text = text });
        Journal.Save();
        Changed?.Invoke(e);
    }

    /// <summary>조치가 이번 버전에서 자동 실행 가능한가. 안 되면 이유를 돌려준다(null = 가능).</summary>
    public static string? WhyNotSupported(string automation)
    {
        if (string.IsNullOrWhiteSpace(automation)) return "자동화 명령이 없는 조치(물리 점검·수동 확인)예요";
        string op = automation.Split(':')[0];
        switch (op)
        {
            case "stop_process":
            case "stop_service":
            case "service_start_type":
            case "firewall_allow":
                return null;
            case "av_exclude":
            case "av_realtime":
                return IsDefender(Part(automation, 1)) ? null
                    : "Microsoft Defender 가 아닌 백신은 제품마다 설정 방법이 달라 자동 실행하지 않아요";
            case "toggle_overlay":
                return "오버레이 설정은 프로그램마다 달라 아직 자동 실행하지 않아요";
            default:
                return "'" + op + "' 조치는 아직 자동 실행을 지원하지 않아요";
        }
    }

    private static bool IsDefender(string product) =>
        product.Contains("Defender", StringComparison.OrdinalIgnoreCase) || product.Contains("디펜더");

    private static string Part(string automation, int index)
    {
        string[] parts = automation.Split(':');
        return index < parts.Length ? parts[index].Trim() : "";
    }

    /// <summary>"vgk.sys" → "vgk", "C:\x\LogiKbdFilt.exe" → "LogiKbdFilt"</summary>
    public static string BaseName(string subject)
    {
        string s = subject.Trim().Replace('/', '\\');
        int slash = s.LastIndexOf('\\');
        if (slash >= 0) s = s.Substring(slash + 1);
        string[] exts = { ".sys", ".exe" };
        for (int i = 0; i < exts.Length; i++)
            if (s.EndsWith(exts[i], StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - exts[i].Length);
        return s;
    }

    private static int StartTypeOf(string word) => word.ToLowerInvariant() switch
    {
        "auto" or "automatic" => 2,
        "manual" or "demand" => 3,
        "disabled" => 4,
        _ => throw new ExecException("알 수 없는 시작 유형: " + word),
    };

    private static string StartTypeKo(int t) => t switch
    {
        0 => "부팅",
        1 => "시스템",
        2 => "자동",
        3 => "수동",
        4 => "사용 안 함",
        _ => t.ToString(),
    };

    // ═══════════════════════ 실행 ═══════════════════════

    /// <summary>조치들을 실행한다 (작업 스레드에서 부를 것). 결과 기록을 돌려준다.</summary>
    public ExecEntry Run(string sessionPath, string context, string label, int level, bool dryRun, List<ActionRequest> actions)
    {
        var e = new ExecEntry
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"),
            SessionPath = sessionPath,
            Context = context,
            Label = label,
            Level = level,
            DryRun = dryRun,
            StartedAt = DateTime.Now,
            State = EntryState.Running,
        };
        for (int i = 0; i < actions.Count; i++) e.Automations.Add(actions[i].Automation);

        lock (_lock)
        {
            Journal.Entries.Add(e);
            Note(e, "info", (dryRun ? "[시험 실행] " : "") + "조치 시작: " + label + " (" + RiskLevel.Name(level) + ")");

            if (level >= 3)
                return Refuse(e, "Lv3 이상(되돌리기 어려운 조치)은 이번 버전에서 자동 실행하지 않아요. 단계를 보고 직접 해 주세요.");
            if (!Ops(e).IsElevated)
                return Refuse(e, "관리자 권한이 필요해요. UI 를 '관리자 권한으로 실행'한 뒤 다시 해 주세요.");

            var supported = new List<ActionRequest>();
            for (int i = 0; i < actions.Count; i++)
            {
                string? why = WhyNotSupported(actions[i].Automation);
                if (why == null) supported.Add(actions[i]);
                else Note(e, "info", "건너뜀 — " + actions[i].Label + ": " + why);
            }
            if (supported.Count == 0)
                return Refuse(e, "자동으로 실행할 수 있는 조치가 없어요. 안내된 단계를 직접 해 주세요.");

            try
            {
                for (int i = 0; i < supported.Count; i++)
                    Apply(e, supported[i]);
                e.State = EntryState.Verifying;
                Note(e, "done", "실행 완료 — 결과를 확인하는 동안 문제가 생기면 원래대로 되돌려요.");
            }
            catch (Exception ex)
            {
                Note(e, "error", "실행 실패: " + ex.Message);
                RollbackSteps(e);
                e.State = EntryState.Failed;
                e.EndedAt = DateTime.Now;
                Note(e, "done", "실행에 실패해서 바꾼 것을 원래대로 되돌렸어요.");
            }
            return e;
        }
    }

    private ExecEntry Refuse(ExecEntry e, string reason)
    {
        e.State = EntryState.Refused;
        e.EndedAt = DateTime.Now;
        Note(e, "error", reason);
        return e;
    }

    /// <summary>바꾸기 전 상태를 먼저 기록하고(Applied=false), 바꾼 뒤 Applied=true 로 기록한다.</summary>
    private void Step(ExecEntry e, ExecStep s, string recordText, Action apply, string applyText)
    {
        e.Steps.Add(s);
        Note(e, "record", "원래 상태 기록 — " + recordText);
        apply();
        s.Applied = true;
        Note(e, "apply", applyText);
    }

    private void Apply(ExecEntry e, ActionRequest a)
    {
        ISystemOps ops = Ops(e);
        string op = Part(a.Automation, 0);

        if (op == "stop_process")
        {
            string subject = Part(a.Automation, 1);
            if (subject.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || subject.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
                throw new ExecException("DLL·드라이버(" + subject + ")는 프로세스로 끌 수 없어요");
            string name = BaseName(subject);
            List<ProcInfo> procs = ops.FindProcesses(name);
            if (procs.Count == 0)
            {
                Note(e, "info", name + " 는 실행 중이 아니라 끌 것이 없어요.");
                return;
            }
            for (int i = 0; i < procs.Count; i++)
            {
                ProcInfo p = procs[i];
                Step(e, new ExecStep { Kind = "process_stop", Target = p.Name, Before = "running:" + p.Pid, After = "stopped", Detail = p.Path },
                    p.Name + " 실행 중 (PID " + p.Pid + (p.Path.Length > 0 ? ", " + p.Path : "") + ")",
                    () => ops.KillProcess(p.Pid), p.Name + " 종료");
            }
            return;
        }

        if (op == "stop_service")
        {
            string svc = BaseName(Part(a.Automation, 1));
            if (ops.GetServiceStartType(svc) is null && !ops.IsDryRun) throw new ExecException("서비스를 찾지 못했어요: " + svc);
            bool running = ops.IsServiceRunning(svc);
            if (!running)
            {
                Note(e, "info", svc + " 서비스는 이미 멈춰 있어서 바꿀 것이 없어요.");
                return;
            }
            Step(e, new ExecStep { Kind = "service_stop", Target = svc, Before = "running", After = "stopped" },
                svc + " 서비스 실행 중", () => ops.StopService(svc), svc + " 서비스 중지");
            return;
        }

        if (op == "service_start_type")
        {
            string svc = BaseName(Part(a.Automation, 1));
            int want = StartTypeOf(Part(a.Automation, 2));
            int? now = ops.GetServiceStartType(svc);
            if (now is null)
            {
                if (!ops.IsDryRun) throw new ExecException("서비스를 찾지 못했어요: " + svc);
                now = 2; // 시험 실행에서 못 읽으면 '자동'이었다고 보고 흐름만 보여 준다
            }
            if (now.Value == want)
            {
                Note(e, "info", svc + " 서비스는 이미 '" + StartTypeKo(want) + "'이라 바꿀 것이 없어요.");
                return;
            }
            int before = now.Value;
            Step(e, new ExecStep { Kind = "service_start_type", Target = svc, Before = before.ToString(), After = want.ToString() },
                svc + " 시작 유형 '" + StartTypeKo(before) + "'", () => ops.SetServiceStartType(svc, want),
                svc + " 시작 유형 '" + StartTypeKo(before) + "' → '" + StartTypeKo(want) + "'" + (want == 4 ? " (재부팅 후 적용)" : ""));
            return;
        }

        if (op == "firewall_allow")
        {
            string proc = BaseName(Part(a.Automation, 1));
            string path = "";
            List<ProcInfo> procs = ops.FindProcesses(proc);
            for (int i = 0; i < procs.Count; i++)
                if (procs[i].Path.Length > 0) { path = procs[i].Path; break; }
            if (path.Length == 0)
            {
                if (!ops.IsDryRun) throw new ExecException(proc + " 의 실행 파일 위치를 찾지 못했어요. 대상 프로그램을 켠 상태에서 다시 실행해 주세요.");
                path = "(" + proc + ".exe 위치)";
            }
            string rule = "PcCrashAnalyzer allow " + proc;
            if (ops.FirewallRuleExists(rule))
            {
                Note(e, "info", "방화벽 허용 규칙이 이미 있어서 바꿀 것이 없어요.");
                return;
            }
            Step(e, new ExecStep { Kind = "firewall_rule", Target = rule, Before = "absent", After = "allow", Detail = path },
                "방화벽 규칙 '" + rule + "' 없음", () => ops.AddFirewallAllow(rule, path), "방화벽 허용 규칙 추가 (" + path + ")");
            return;
        }

        if (op == "av_exclude")
        {
            string proc = Part(a.Automation, 2);
            if (!proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc += ".exe";
            if (ops.DefenderHasProcessExclusion(proc))
            {
                Note(e, "info", proc + " 는 이미 Defender 예외에 있어서 바꿀 것이 없어요.");
                return;
            }
            Step(e, new ExecStep { Kind = "defender_exclusion", Target = proc, Before = "absent", After = "excluded" },
                "Defender 예외에 " + proc + " 없음", () => ops.DefenderAddProcessExclusion(proc), "Defender 예외에 " + proc + " 추가");
            return;
        }

        if (op == "av_realtime")
        {
            if (!ops.DefenderRealtimeEnabled())
            {
                Note(e, "info", "Defender 실시간 보호가 이미 꺼져 있어서 바꿀 것이 없어요.");
                return;
            }
            int seconds = 0;
            if (Part(a.Automation, 2) == "pause") int.TryParse(Part(a.Automation, 3), out seconds);
            Step(e, new ExecStep { Kind = "defender_realtime", Target = "Microsoft Defender", Before = "on", After = "off", Detail = seconds.ToString() },
                "Defender 실시간 보호 켜짐", () => ops.DefenderSetRealtime(false),
                "Defender 실시간 보호 끔" + (seconds > 0 ? " (" + seconds / 60 + "분 뒤 자동으로 다시 켬)" : ""));
            if (seconds > 0) e.AutoRollbackAt = DateTime.Now.AddSeconds(seconds);
            return;
        }

        throw new ExecException("지원하지 않는 조치: " + a.Automation);
    }

    // ═══════════════════════ 되돌리기 ═══════════════════════

    /// <summary>바꾼 것을 거꾸로 되돌린다. 하나가 실패해도 나머지는 계속 되돌린다.</summary>
    private bool RollbackSteps(ExecEntry e)
    {
        ISystemOps ops = Ops(e);
        bool allOk = true;
        for (int i = e.Steps.Count - 1; i >= 0; i--)
        {
            ExecStep s = e.Steps[i];
            if (!s.Applied || s.RolledBack) continue;
            try
            {
                string text;
                switch (s.Kind)
                {
                    case "process_stop":
                        if (s.Detail.Length == 0)
                        {
                            text = s.Target + " 는 실행 파일 위치를 몰라 다시 켜지 못했어요 — 직접 켜 주세요.";
                            break;
                        }
                        ops.StartProgram(s.Detail);
                        text = s.Target + " 다시 실행";
                        break;
                    case "service_stop":
                        ops.StartService(s.Target);
                        text = s.Target + " 서비스 다시 시작";
                        break;
                    case "service_start_type":
                        ops.SetServiceStartType(s.Target, int.Parse(s.Before));
                        text = s.Target + " 시작 유형을 원래대로('" + StartTypeKo(int.Parse(s.Before)) + "') 되돌림";
                        break;
                    case "firewall_rule":
                        ops.DeleteFirewallRule(s.Target);
                        text = "방화벽 허용 규칙 삭제";
                        break;
                    case "defender_exclusion":
                        ops.DefenderRemoveProcessExclusion(s.Target);
                        text = "Defender 예외에서 " + s.Target + " 삭제";
                        break;
                    case "defender_realtime":
                        ops.DefenderSetRealtime(true);
                        text = "Defender 실시간 보호 다시 켬";
                        break;
                    default:
                        throw new ExecException("알 수 없는 기록 종류: " + s.Kind);
                }
                s.RolledBack = true;
                Note(e, "rollback", text);
            }
            catch (Exception ex)
            {
                allOk = false;
                Note(e, "error", "되돌리지 못함 — " + s.Target + ": " + ex.Message);
            }
        }
        return allOk;
    }

    /// <summary>원래 상태로 되돌린다 (확인 중·효과 있음 상태 모두 가능).</summary>
    public void Rollback(ExecEntry e, string reason)
    {
        lock (_lock)
        {
            if (e.State == EntryState.RolledBack || e.State == EntryState.Failed || e.State == EntryState.Refused) return;
            Note(e, "info", "되돌리는 이유: " + reason);
            bool ok = RollbackSteps(e);
            e.State = EntryState.RolledBack;
            e.EndedAt = DateTime.Now;
            e.AutoRollbackAt = null;
            Note(e, "done", ok ? "원래 상태로 되돌렸어요." : "일부는 되돌리지 못했어요. 위 기록을 보고 직접 확인해 주세요.");
        }
    }

    /// <summary>효과가 있어 그대로 둔다.</summary>
    public void Keep(ExecEntry e, string reason)
    {
        lock (_lock)
        {
            if (e.State != EntryState.Verifying) return;
            e.State = EntryState.Kept;
            e.EndedAt = DateTime.Now;
            Note(e, "done", "그대로 둠 — " + reason);
        }
    }

    /// <summary>
    /// UI 를 켤 때 부른다. '실행 중'/'확인 중'으로 남은 기록 = 지난번 조치 도중 UI 나 PC 가 꺼진 것 → 원래대로 되돌린다.
    /// </summary>
    public List<ExecEntry> RecoverPending()
    {
        var recovered = new List<ExecEntry>();
        for (int i = 0; i < Journal.Entries.Count; i++)
        {
            ExecEntry e = Journal.Entries[i];
            if (!e.IsActive) continue;
            Rollback(e, "지난번 조치 도중 프로그램이 꺼졌거나 PC 가 다시 시작돼서, 문제가 생기기 전 상태로 되돌려요");
            recovered.Add(e);
        }
        return recovered;
    }

    /// <summary>시간이 정해진 조치(예: 실시간 보호 10분 일시 중지)의 되돌릴 시간이 지났으면 되돌린다.</summary>
    public List<ExecEntry> RollbackExpired()
    {
        var done = new List<ExecEntry>();
        for (int i = 0; i < Journal.Entries.Count; i++)
        {
            ExecEntry e = Journal.Entries[i];
            if (e.IsActive && e.AutoRollbackAt is DateTime at && DateTime.Now >= at)
            {
                Rollback(e, "정해 둔 일시 중지 시간이 끝났어요");
                done.Add(e);
            }
        }
        return done;
    }

    /// <summary>이 세션의 이 대상(점검·해결책)에 대한 가장 최근 기록</summary>
    public ExecEntry? Latest(string sessionPath, string context)
    {
        for (int i = Journal.Entries.Count - 1; i >= 0; i--)
        {
            ExecEntry e = Journal.Entries[i];
            if (e.SessionPath == sessionPath && e.Context == context) return e;
        }
        return null;
    }

    /// <summary>지금 '확인 중'인 기록들</summary>
    public List<ExecEntry> Active(string sessionPath)
    {
        var list = new List<ExecEntry>();
        for (int i = 0; i < Journal.Entries.Count; i++)
            if (Journal.Entries[i].SessionPath == sessionPath && Journal.Entries[i].IsActive) list.Add(Journal.Entries[i]);
        return list;
    }
}
