"""Review of case runs after a lab session (read-only on the dumps): is each run fit to be an evaluation case?

    eval\\.venv\\Scripts\\python.exe eval\\review_cases.py F:\\pca-vm\\cases [--since 20261113] [--write] [-v]

For every run folder it checks, from the dump alone:
  effect   did the injected fault do to the PC what the scenario says? (a CPU hog must have filled the CPU, a disk hog must
           have saturated the disk ...). A run where the fault was present but harmless is not a case: there is nothing to find.
  leak     does a dump of an alias run contain a string that gives the answer away (the test program's real name, lab paths)?
  flags    things a reader of the case must know: an event the clone writes on its own (LiveKernelEvent 0x193 on GPU clones),
           a disk that is saturated with no fault at all (the slowed-down profile).
  engine   where the rule engine ranks the culprit (information only - never a reason to keep or drop a case).
--write stores the verdict as review.json next to result.json; eval/cases.py skips runs whose review says effect_ok = false.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from grade import same  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
ENGINE = REPO / "engine" / "build" / "Release" / "crash_engine.exe"
SCENARIOS = json.loads((REPO / "lab" / "vm" / "scenarios.json").read_text(encoding="utf-8"))["scenarios"]

# strings that must not be in a dump shown to a model when the fault program ran under an alias
LEAK_PATTERNS = [r"faultlab", r"faulthost", r"faultplugin", r"task\.cfg", r"c:\\\\lab\\\\", r"guest-case", r"pca-vm", r"LOAD_READY_AT|LOAD_AT|CRASH_AT|HANG_AT"]


def load(p: Path) -> dict:
    return json.loads(p.read_text(encoding="utf-8-sig"))


def engine(snap: Path) -> dict | None:
    with tempfile.TemporaryDirectory() as td:
        out = Path(td) / "e.json"
        r = subprocess.run([str(ENGINE), "diagnose", str(snap), "--json", str(out)], capture_output=True)
        return load(out) if r.returncode == 0 and out.exists() else None


def rank(rows: list, names: list[str], key: str = "name") -> int | None:
    for i, row in enumerate(rows or [], 1):
        if any(same(row.get(key), n) for n in names):
            return i
    return None


def effect(category: str, rr: dict | None, es: dict, names: list[str]) -> tuple[bool | None, str]:
    """(ok, why). None = this kind of case has no effect to check."""
    if category in ("none", None, ""):
        return None, "no fault"
    if category in ("app_bug", "third_party_module"):
        sym = es.get("symptom", {})
        hit = any(same(sym.get("target_process"), n) for n in names) or any(same(m, n) for m in sym.get("faulting_modules") or [] for n in names)
        return hit, f"dump target '{sym.get('target_process')}' modules {sym.get('faulting_modules')}"
    if rr is None:
        return None, "dump has no resource_recent (made before 2026-11-12 night): effect not checked"
    if category == "cpu_contention":
        r = rank(rr.get("top_cpu"), names)
        ok = (rr.get("cpu_total_pct_max") or 0) >= 90 and r is not None
        return ok, f"PC CPU max {rr.get('cpu_total_pct_max')}%, culprit #{r} in top_cpu"
    if category in ("memory_pressure", "memory_leak"):
        r = rank(rr.get("top_memory"), names)
        ok = (rr.get("commit_pct_max") or 0) >= 85 and r is not None
        return ok, f"commit max {rr.get('commit_pct_max')}%, culprit #{r} in top_memory"
    if category == "disk_contention":
        d = (rr.get("disks") or [{}])[0]
        r = rank(rr.get("top_io"), names)
        # harm = the disk was saturated for a while: the whole window (average >= 80 %) or at least 3 samples (30 s) at
        # >= 90 %. A summary made before 2026-11-14 has no sample count; then the maximum is all there is.
        n90 = d.get("busy_ge90_samples")
        saturated = (d.get("busy_pct_avg") or 0) >= 80 or (n90 >= 3 if n90 is not None else (d.get("busy_pct_max") or 0) >= 80)
        ok = saturated and r is not None
        return ok, f"disk busy avg {d.get('busy_pct_avg')}% max {d.get('busy_pct_max')}%, samples >= 90%: {n90}, latency avg {d.get('latency_ms_avg')} ms, culprit #{r} in top_io"
    if category in ("user_workload", "system_work"):
        # the user's own heavy job, or Windows' own background work (names = the program doing it): the PC must really
        # have been busy with it
        d = (rr.get("disks") or [{}])[0]
        rc, ri = rank(rr.get("top_cpu"), names), rank(rr.get("top_io"), names)
        busy = (rr.get("cpu_total_pct_max") or 0) >= 90 or (d.get("busy_ge90_samples") or 0) >= 3 or (d.get("busy_pct_avg") or 0) >= 80
        ok = busy and (rc is not None or ri is not None)
        return ok, f"PC CPU max {rr.get('cpu_total_pct_max')}%, disk samples >= 90%: {d.get('busy_ge90_samples')}, the job #{rc} in top_cpu, #{ri} in top_io"
    return None, f"no effect rule for '{category}'"


def snapshot_path(case_dir: Path, dump: Path) -> Path:
    """The snapshot rebuilt with the current collector summary (eval/replay.py) when there is one, else the dump's own."""
    rp = case_dir.parent / "_replay" / f"{case_dir.name}__{dump.name}.json"
    return rp if rp.exists() else dump / "engine-snapshot.json"


def review_run(case_dir: Path, run: Path, verbose: bool, with_engine: bool = True) -> dict | None:
    label = f"{case_dir.name}/{run.name}"
    if not (run / "result.json").exists() or not (run / "scenario.json").exists():
        print(f"== {label}: no result.json (the run did not finish)")
        return None
    scn, res = load(run / "scenario.json"), load(run / "result.json")
    truth, alias = scn.get("truth") or {}, scn.get("alias") or {}
    culprit = truth.get("culprit")
    names = [n for n in (culprit, alias.get("exe"), alias.get("host"), alias.get("plugin")) if n]
    kind = scn.get("kind", "fault")
    names += list(truth.get("also") or [])
    if culprit and not alias and kind == "fault":
        names += ["FaultLab.exe", "FaultHost.exe", "FaultPlugin.dll"]
    # the user's own heavy job (truth.workload): no culprit, but the effect check looks for the program doing the job
    job_name = truth.get("workload")
    effect_names = [job_name] if truth.get("cause_category") == "user_workload" and job_name else names
    want = (SCENARIOS.get(scn["scenario"]) or {}).get("expect_dump_kind") or "manual"
    dumps = sorted(d for d in (run / "dumps").iterdir() if d.is_dir()) if (run / "dumps").exists() else []
    prefer = (SCENARIOS.get(scn["scenario"]) or {}).get("prefer_dump_kind")
    pick = (next((d for d in dumps if prefer and d.name.split("-", 2)[-1].startswith(prefer)), None)
            or next((d for d in dumps if d.name.split("-", 2)[-1].startswith(want)), None))
    out = {"schema": 1, "reviewed_at": time.strftime("%Y-%m-%dT%H:%M:%S"), "case": label, "dump": pick.name if pick else None,
           "effect_ok": None, "effect": "", "flags": [], "leak_hits": [], "engine_rank": None, "engine_top": None}
    head = f"== {label}  ok={res.get('ok')} profile={scn.get('profile')} gpu={scn.get('gpu_partition')} culprit={culprit} decoys={scn.get('decoys') or []}"
    if not res.get("ok") or pick is None:
        print(head + f"\n   NOT A CASE: result ok={res.get('ok')}, dump of kind '{want}': {pick is not None}, problems {res.get('problems')}")
        out["effect_ok"], out["effect"] = False, "run not ok or no dump of the expected kind"
        return out
    snap = snapshot_path(case_dir, pick)
    es = load(snap)
    rr = es.get("resource_recent")
    out["effect_ok"], out["effect"] = effect(truth.get("cause_category"), rr, es, effect_names)
    rec = res.get("recipe") or {}
    if kind == "recipe" and truth.get("cause_category") == "app_bug" and not out["effect_ok"] and rec.get("exit_code") not in (None, 0) and not rec.get("timed_out"):
        # The program ended badly but no Windows crash event names it (a program that handles its own crash just ends).
        # Still a case - the evidence is weaker, so it carries a flag and can be counted apart.
        out["effect_ok"], out["effect"] = True, f"ended with exit code {rec.get('exit_code')}; no Windows crash event names it ({out['effect']})"
        out["flags"].append("silent_crash")
    if job_name:
        # counted apart: 'user_workload' = the busy PC is the user's own doing (no culprit), 'busy' = a fault next to it
        out["flags"].append(("busy:" if culprit else "user_workload:") + str(job_name))
        if culprit and rr and rank(rr.get("top_cpu"), [job_name]) is None and rank(rr.get("top_io"), [job_name]) is None:
            out["flags"].append("job_not_seen")

    if any("LiveKernelEvent" in str(e.get("subject")) for e in es.get("events") or []):
        out["flags"].append("own_event:LiveKernelEvent")
    extra = [d.name for d in dumps if d != pick]
    if extra:
        out["flags"].append("other_dumps:" + ",".join(x.split("-", 2)[-1] for x in extra))
    if rr and not culprit and (rr.get("disks") or [{}])[0].get("busy_pct_avg", 0) >= 80:
        out["flags"].append("hardware_limit:disk")
    if rr is None:
        out["flags"].append("no_resource_recent")
    # a case made while the PC itself was busy (daytime sessions, 2026-11-14): kept, but marked
    hl = scn.get("host_load") or {}
    if (hl.get("host_cpu_pct_avg") or 0) >= 50 or (hl.get("all_cpu_pct_avg") or 0) >= 85:
        out["flags"].append(f"host_busy:{hl.get('host_cpu_pct_avg')}/{hl.get('all_cpu_pct_avg')}")
    for w in res.get("warnings") or []:
        out["flags"].append("warning:" + str(w)[:80])

    if alias or kind == "recipe" or scn.get("workload"):
        for f in sorted(pick.iterdir()):
            text = f.read_text(encoding="utf-8-sig", errors="replace")
            for pat in LEAK_PATTERNS:
                m = re.search(pat, text, re.IGNORECASE)
                if m:
                    out["leak_hits"].append(f"{f.name}: '{m.group(0)}' ...{text[max(0, m.start() - 60):m.end() + 40]!r}")

    # --no-engine: for test-set runs. Whether a run is a case is decided without looking at what the engine says about
    # it, so that nothing can be tuned on the test set by accident.
    if with_engine:
        eo = engine(snap)
        cands = (eo or {}).get("candidates") or []
        out["engine_rank"] = rank(cands, names, "label") if names else None
        out["engine_top"] = [f"{c.get('label')} {c.get('probability', 0) * 100:.1f}%" for c in cands[:3]]

    print(head)
    print(f"   dump {pick.name}   effect: {'OK' if out['effect_ok'] else 'n/a' if out['effect_ok'] is None else 'NOT REPRODUCED'} - {out['effect']}")
    if out["flags"]:
        print(f"   flags: {out['flags']}")
    for h in out["leak_hits"]:
        print(f"   LEAK: {h}")
    if with_engine:
        print(f"   engine: culprit #{out['engine_rank']}  top: {' | '.join(out['engine_top'])}")
    if verbose and rr:
        for key in ("top_cpu", "top_io", "top_memory"):
            print(f"     {key}: " + " | ".join(json.dumps({k: v for k, v in r.items() if k != 'pid'}, ensure_ascii=False) for r in (rr.get(key) or [])[:3]))
        for d in rr.get("disks") or []:
            print(f"     disk: {json.dumps(d, ensure_ascii=False)}")
    return out


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    if not args:
        print(__doc__)
        return 1
    root = Path(args[0])
    since = sys.argv[sys.argv.index("--since") + 1] if "--since" in sys.argv else ""
    if since in args:
        args.remove(since)
    write, verbose, with_engine = "--write" in sys.argv, "-v" in sys.argv, "--no-engine" not in sys.argv
    rows = []
    for case_dir in sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir()):
            if since and run.name < since:
                continue
            r = review_run(case_dir, run, verbose, with_engine)
            if r is None:
                rows.append((f"{case_dir.name}/{run.name}", "unfinished", "", ""))
                continue
            if write:
                (run / "review.json").write_text(json.dumps(r, indent=2, ensure_ascii=False), encoding="utf-8")
            state = "case" if r["effect_ok"] is not False and not r["leak_hits"] else "NOT A CASE"
            rows.append((r["case"], state, "" if not with_engine else f"engine #{r['engine_rank']}" if r["engine_rank"] else "engine -", ",".join(r["flags"])))
    print("\n=== summary")
    for row in rows:
        print("  {:46} {:11} {:12} {}".format(*row))
    print(f"{sum(1 for r in rows if r[1] == 'case')} usable of {len(rows)}" + ("  (review.json written)" if write else "  (nothing written; add --write)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
