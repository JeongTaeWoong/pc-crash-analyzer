"""Where the rule engine ranks the true culprit, on the DEVELOPMENT cases. No model is involved; nothing is paid for.

    eval\\.venv\\Scripts\\python.exe eval\\engine_rank.py F:\\pca-vm\\cases [--engine path\\crash_engine.exe] [--json out.json] [-v]
    eval\\.venv\\Scripts\\python.exe eval\\engine_rank.py --compare before.json after.json

Used to check an engine change before and after (2026-11-17: the program that crashed was ranked below programs that
were merely present). For every case the engine's candidate list is compared with the answer key:
  culprit cases     rank of the culprit (1 = first), its share, and what the engine put first instead
  cases without one (healthy PC, the user's own heavy job, a hardware limit): the engine always lists candidates, so the
                    only thing reported is how large the share of the first one is - a high share is a false alarm
The test set is never read: the folder cases-test is refused, and test-set runs still sitting in the development folder
are skipped by eval/cases.py.
"""
from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import cases as cases_mod  # noqa: E402
from grade import same  # noqa: E402


def run_engine(engine: Path, case) -> dict:
    snap = case.replay_snapshot or (case.dump_dir / "engine-snapshot.json")
    with tempfile.TemporaryDirectory() as td:
        if case.event:                                                # the same view of a merged event the models get
            snap = Path(td) / "view.json"
            snap.write_text(json.dumps(case.snapshot(), ensure_ascii=False), encoding="utf-8")
        out = Path(td) / "engine.json"
        r = subprocess.run([str(engine), "diagnose", str(snap), "--json", str(out)], capture_output=True)
        if r.returncode != 0 or not out.exists():
            raise RuntimeError(f"engine failed on {snap}: exit {r.returncode}")
        return json.loads(out.read_text(encoding="utf-8-sig"))


def culprit_names(case) -> list[str]:
    truth, alias = case.truth, case.alias or {}
    names = [n for n in (truth.get("culprit"), alias.get("exe"), alias.get("host"), alias.get("plugin")) if n]
    names += list(truth.get("also") or [])
    if case.leaky:
        names += ["FaultLab.exe", "FaultHost.exe", "FaultPlugin.dll"]
    return names


def is_named(cand: dict, names: list[str]) -> bool:
    return any(same(cand.get(k), n) for k in ("label", "subject", "product") for n in names)


def measure(root: Path, engine: Path, verbose: bool) -> list[dict]:
    rows = []
    for c in cases_mod.load_cases(root):
        eo = run_engine(engine, c)
        cands = eo.get("candidates") or []
        names = culprit_names(c) if c.truth.get("culprit") else []
        rank = next((i for i, x in enumerate(cands, 1) if is_named(x, names)), None) if names else None
        row = {
            "id": c.id, "family": c.family, "symptom": c.truth.get("symptom") or "none", "category": c.truth.get("cause_category") or "none",
            "culprit": c.truth.get("culprit"), "rank": rank, "candidates": len(cands),
            "culprit_share": round(cands[rank - 1].get("probability", 0), 4) if rank else None,
            "top": cands[0].get("label") if cands else None, "top_share": round(cands[0].get("probability", 0), 4) if cands else None,
            "top3": [f"{x.get('label')} {x.get('probability', 0) * 100:.0f}%" for x in cands[:3]],
            "flags": c.meta.get("flags") or [],
        }
        rows.append(row)
        if verbose:
            print(f"{c.id:58} {row['symptom']:9} culprit={row['culprit']!s:22} rank={row['rank']!s:4} top: {' | '.join(row['top3'])}")
    return rows


def summary(rows: list[dict]) -> None:
    with_culprit = [r for r in rows if r["culprit"]]
    without = [r for r in rows if not r["culprit"]]
    print(f"\n{len(rows)} cases: {len(with_culprit)} with a culprit, {len(without)} without")
    by = defaultdict(list)
    for r in with_culprit:
        by[(r["symptom"], r["category"])].append(r)
    print(f"\n{'symptom':10} {'cause':20} {'n':>3} {'first':>6} {'in 3':>5} {'absent':>7}  median share of the culprit")
    tot1 = tot3 = 0
    for key in sorted(by):
        g = by[key]
        n1 = sum(1 for r in g if r["rank"] == 1)
        n3 = sum(1 for r in g if r["rank"] and r["rank"] <= 3)
        na = sum(1 for r in g if r["rank"] is None)
        shares = sorted(r["culprit_share"] for r in g if r["culprit_share"] is not None)
        med = f"{shares[len(shares) // 2] * 100:.0f}%" if shares else "-"
        tot1, tot3 = tot1 + n1, tot3 + n3
        print(f"{key[0]:10} {key[1]:20} {len(g):3} {n1:6} {n3:5} {na:7}  {med}")
    if with_culprit:
        print(f"{'all':10} {'':20} {len(with_culprit):3} {tot1:6} {tot3:5}")
    if without:
        shares = sorted(r["top_share"] or 0 for r in without)
        print(f"\nno culprit ({len(without)}): share of the first candidate - median {shares[len(shares) // 2] * 100:.0f}%, max {shares[-1] * 100:.0f}%, "
              f"at 50% or more: {sum(1 for s in shares if s >= 0.5)}")


def compare(a_path: Path, b_path: Path) -> None:
    a = {r["id"]: r for r in json.loads(a_path.read_text(encoding="utf-8"))}
    b = {r["id"]: r for r in json.loads(b_path.read_text(encoding="utf-8"))}
    better = worse = 0
    for i in sorted(a.keys() & b.keys()):
        ra, rb = a[i], b[i]
        if not ra["culprit"]:
            if abs((ra["top_share"] or 0) - (rb["top_share"] or 0)) >= 0.10 or ra["top"] != rb["top"]:
                print(f"  no culprit  {i:58} first: {ra['top']} {(ra['top_share'] or 0) * 100:.0f}% -> {rb['top']} {(rb['top_share'] or 0) * 100:.0f}%")
            continue
        if ra["rank"] != rb["rank"]:
            up = (rb["rank"] or 999) < (ra["rank"] or 999)
            better, worse = better + up, worse + (not up)
            print(f"  {'BETTER' if up else 'WORSE '}      {i:58} rank {ra['rank']} -> {rb['rank']}   first now: {' | '.join(rb['top3'])}")
    print(f"\nculprit rank: better in {better}, worse in {worse}  (cases only in one file: {len(a.keys() ^ b.keys())})")


def main() -> int:
    argv = sys.argv[1:]
    if "--compare" in argv:
        i = argv.index("--compare")
        compare(Path(argv[i + 1]), Path(argv[i + 2]))
        return 0
    args = [a for a in argv if not a.startswith("-")]
    opt = {k: argv[argv.index(k) + 1] for k in ("--engine", "--json") if k in argv}
    args = [a for a in args if a not in opt.values()]
    if not args:
        print(__doc__)
        return 1
    root = Path(args[0])
    if root.name.lower() == "cases-test":
        print("cases-test is the test set: the engine is not measured or tuned on it")
        return 2
    engine = Path(opt.get("--engine") or cases_mod.ENGINE)
    rows = measure(root, engine, "-v" in argv)
    summary(rows)
    if "--json" in opt:
        Path(opt["--json"]).write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
