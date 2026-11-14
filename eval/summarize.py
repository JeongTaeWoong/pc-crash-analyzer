"""Table of finished runs: one line per (method, model, effort).

    eval\\.venv\\Scripts\\python.exe eval\\summarize.py [--since 20261113-12] [--set eval\\sets\\pilot-1.txt] [--json out.json]

Counted apart, never mixed (docs/evaluation-design-v3.md section 2):
  fault cases     top1 / top3 over answers that were produced; refusals, cut-off and unparseable answers are listed, not scored
  no-fault cases  none = says outright nothing is wrong, abstain = names no culprit, accuse = names a program with
                  medium or high confidence. Cases whose review.json carries a flag are kept out of the no-fault
                  numbers: 'flagged' = the clone's own event (GPU clones), 'hw' = a PC slow because of its own hardware
                  (flag hardware_limit; part = the answer names the hardware, program = it accuses a program).
  --families      one more line per row: top1 by scenario family (cases of one family are alike - report them apart)
  agreement       when a case was run more than once: share of cases where every repetition named the same first candidate
  cost            what the ledger was charged, per answer (mean) - measured, not estimated
Small numbers of cases: read the counts, not percentages.
"""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from pathlib import Path

RUNS = Path(__file__).parent / "runs"


def main() -> int:
    since = sys.argv[sys.argv.index("--since") + 1] if "--since" in sys.argv else ""
    set_file = sys.argv[sys.argv.index("--set") + 1] if "--set" in sys.argv else None
    out_json = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    wanted = None
    if set_file:
        wanted = {ln.split("#")[0].strip() for ln in Path(set_file).read_text(encoding="utf-8").splitlines()} - {""}
    flags: dict[str, list] = {}
    groups: dict[tuple, list] = defaultdict(list)
    errors: dict[tuple, int] = defaultdict(int)
    for run in sorted(p for p in RUNS.iterdir() if p.is_dir() and p.name >= since):
        cfg_p, res_p = run / "config.json", run / "results.jsonl"
        if not cfg_p.exists() or not res_p.exists():
            continue
        cfg = json.loads(cfg_p.read_text(encoding="utf-8"))
        key = (cfg["condition"], cfg["model"].replace("claude-", ""), cfg.get("effort") or "default", cfg.get("prompt_version"))
        for line in res_p.read_text(encoding="utf-8").splitlines():
            row = json.loads(line)
            if wanted is not None and row["case"] not in wanted:
                continue
            groups[key].append(row)
        if (run / "errors.jsonl").exists():
            errors[key] += len((run / "errors.jsonl").read_text(encoding="utf-8").splitlines())

    def case_flags(case_id: str) -> list:
        if case_id not in flags:
            flags[case_id] = []
            for root in (Path(r"F:\pca-vm\cases"), Path(r"F:\pca-vm\cases-test")):
                rv = root / case_id / "review.json"
                if rv.exists():
                    flags[case_id] = [f for f in json.loads(rv.read_text(encoding="utf-8-sig")).get("flags") or [] if f.startswith(("own_event", "hardware_limit", "user_workload"))]
        return flags[case_id]

    def job_of(case_id: str) -> str:
        return next((f.split(":", 1)[1] for f in case_flags(case_id) if f.startswith("user_workload:")), "")

    def same_name(a: str | None, b: str) -> bool:
        x, y = (a or "").lower().replace(".exe", ""), b.lower().replace(".exe", "")
        return bool(x) and bool(y) and (x == y or x in y or y in x)

    table = []
    for key in sorted(groups):
        rows = groups[key]
        fault = [r for r in rows if r["truth"]]
        clean = [r for r in rows if not r["truth"] and not case_flags(r["case"])]
        hw = [r for r in rows if not r["truth"] and any(f.startswith("hardware_limit") for f in case_flags(r["case"]))]
        work = [r for r in rows if not r["truth"] and job_of(r["case"])]
        flagged = [r for r in rows if not r["truth"] and case_flags(r["case"]) and r not in hw and r not in work]
        ok = lambda rs: [r for r in rs if r["grade"]["status"] == "ok"]  # noqa: E731
        f_ok, c_ok, g_ok, h_ok, w_ok = ok(fault), ok(clean), ok(flagged), ok(hw), ok(work)
        by_family = defaultdict(lambda: [0, 0])
        for r in f_ok:
            by_family[r["family"]][1] += 1
            by_family[r["family"]][0] += 1 if r["grade"]["top1"] else 0
        grounded = [r["grade"]["grounded"] for r in ok(rows) if r["grade"]["grounded"] is not None]
        by_case = defaultdict(list)
        for r in ok(rows):
            by_case[r["case"]].append((r["first"] or "").lower().replace(".exe", ""))
        multi = [v for v in by_case.values() if len(v) > 1]
        entry = {
            "method": key[0], "model": key[1], "effort": key[2], "prompt": key[3], "answers": len(rows), "not_scored": len(rows) - len(ok(rows)), "api_errors": errors[key],
            "fault_n": len(f_ok), "top1": sum(1 for r in f_ok if r["grade"]["top1"]), "top3": sum(1 for r in f_ok if r["grade"]["top3"]),
            "fault_accuse_wrong": sum(1 for r in f_ok if r["grade"]["false_accusation"]),
            "clean_n": len(c_ok), "clean_none": sum(1 for r in c_ok if r["grade"]["none_correct"]),
            "clean_abstain": sum(1 for r in c_ok if r["grade"]["abstained"] and not r["grade"]["none_correct"]),
            "clean_accuse": sum(1 for r in c_ok if r["grade"]["false_accusation"]),
            "flagged_n": len(g_ok), "flagged_accuse": sum(1 for r in g_ok if r["grade"]["false_accusation"]),
            # a PC slow because of its own hardware: 'part' = the first candidate is the hardware, 'program' = a program
            # is accused with medium or high confidence (an answer that could make the user remove something useful)
            "hw_n": len(h_ok), "hw_part": sum(1 for r in h_ok if r["grade"].get("first_kind") == "hardware"),
            "hw_program": sum(1 for r in h_ok if r["grade"]["false_accusation"] and r["grade"].get("first_kind") in ("process", "module", "driver")),
            # the user's own heavy job (no culprit): 'calm' = nobody accused, 'job' = the program doing the job is accused
            # (medium or high confidence), 'other' = some other program is accused
            "work_n": len(w_ok), "work_calm": sum(1 for r in w_ok if not r["grade"]["false_accusation"]),
            "work_job": sum(1 for r in w_ok if r["grade"]["false_accusation"] and same_name(r["first"], job_of(r["case"]))),
            "work_other": sum(1 for r in w_ok if r["grade"]["false_accusation"] and not same_name(r["first"], job_of(r["case"]))),
            "by_family": {k: f"{v[0]}/{v[1]}" for k, v in sorted(by_family.items())},
            "grounded_min": round(min(grounded), 3) if grounded else None,
            "agree": f"{sum(1 for v in multi if len(set(v)) == 1)}/{len(multi)}" if multi else "-",
            "in_tok": round(sum(r["usage"].get("input_tokens", 0) for r in rows) / len(rows)), "out_tok": round(sum(r["usage"].get("output_tokens", 0) for r in rows) / len(rows)),
            "usd": round(sum(r["usd"] for r in rows) / len(rows), 4), "usd_total": round(sum(r["usd"] for r in rows), 3), "sec": round(sum(r["seconds"] for r in rows) / len(rows), 1),
        }
        table.append(entry)

    print(f"{'method':6} {'model':11} {'effort':7} {'n':>3} {'fault top1':>10} {'top3':>5} {'wrong!':>6} {'clean none/abst/accuse':>22} {'flagged accuse':>14} {'cite ok':>7} {'agree':>6} {'in tok':>8} {'out tok':>7} {'USD/ans':>8} {'total':>7} {'sec':>5}")
    for e in table:
        print(f"{e['method']:6} {e['model']:11} {e['effort']:7} {e['answers']:>3} {str(e['top1']) + '/' + str(e['fault_n']):>10} {str(e['top3']) + '/' + str(e['fault_n']):>5} {e['fault_accuse_wrong']:>6} "
              f"{str(e['clean_none']) + '/' + str(e['clean_abstain']) + '/' + str(e['clean_accuse']) + ' of ' + str(e['clean_n']):>22} {str(e['flagged_accuse']) + ' of ' + str(e['flagged_n']):>14} "
              f"{str(e['grounded_min']):>7} {e['agree']:>6} {e['in_tok']:>8,} {e['out_tok']:>7,} {e['usd']:>8.4f} {e['usd_total']:>7.2f} {e['sec']:>5}"
              + (f"   hw part/program {e['hw_part']}/{e['hw_program']} of {e['hw_n']}" if e["hw_n"] else "")
              + (f"   user job calm/job/other {e['work_calm']}/{e['work_job']}/{e['work_other']} of {e['work_n']}" if e["work_n"] else "")
              + (f"   not scored {e['not_scored']}" if e["not_scored"] else "") + (f"   api errors {e['api_errors']}" if e["api_errors"] else ""))
        if "--families" in sys.argv:
            print("         " + "  ".join(f"{k} {v}" for k, v in e["by_family"].items()))
    if out_json:
        Path(out_json).write_text(json.dumps(table, indent=2, ensure_ascii=False), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
