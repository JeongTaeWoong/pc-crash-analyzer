"""A5 from answers already paid for: ask the small model three times; when the three first candidates agree take that
answer, when they differ ask the large model once.  No API call is made here.

    eval\\.venv\\Scripts\\python.exe eval\\cascade.py --set eval\\sets\\dev-1.txt --set eval\\sets\\dev-hard-1.txt [--condition A4]

Why agreement and not the engine's probability: on the development cases the engine's first-candidate probability on
runs with nothing wrong (0.05-0.08) overlaps with runs with a fault (0.06-0.14), and what a model says about its own
confidence is known to be overconfident.  Three cheap answers that agree are the confidence signal.
Reads eval/runs/*/results.jsonl of one prompt version; uses the newest run of each (condition, model).
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from prompts import PROMPT_VERSION  # noqa: E402

RUNS = Path(__file__).parent / "runs"


def good(row: dict) -> bool:
    """An answer that does no harm: the culprit first, or - when there is none - nobody accused."""
    g = row["grade"]
    return bool(g["top1"]) if row["truth"] else not g["false_accusation"]


def key_of(row: dict) -> str:
    return (row["first"] or "").lower().replace(".exe", "").replace(".dll", "") if row["grade"]["verdict"] == "culprit_found" else "-"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--set", dest="sets", action="append", required=True)
    ap.add_argument("--condition", default="A4")
    ap.add_argument("--small", default="claude-haiku-5-5")
    ap.add_argument("--large", default="claude-sonnet-5-5")
    args = ap.parse_args()

    wanted: list[str] = []
    for s in args.sets:
        wanted += [ln.split("#")[0].strip() for ln in Path(s).read_text(encoding="utf-8").splitlines() if ln.split("#")[0].strip()]
    rows: dict[str, dict[str, list]] = {args.small: defaultdict(list), args.large: defaultdict(list)}
    for run in sorted(p for p in RUNS.iterdir() if p.is_dir()):      # later runs overwrite earlier ones of the same case
        cfg_p, res_p = run / "config.json", run / "results.jsonl"
        if not cfg_p.exists() or not res_p.exists():
            continue
        cfg = json.loads(cfg_p.read_text(encoding="utf-8"))
        if cfg.get("prompt_version") != PROMPT_VERSION or cfg["condition"] != args.condition or cfg["model"] not in rows:
            continue
        seen = defaultdict(list)
        for ln in res_p.read_text(encoding="utf-8").splitlines():
            r = json.loads(ln)
            if r["case"] in wanted and r["grade"]["status"] == "ok":
                seen[r["case"]].append(r)
        for case, rs in seen.items():
            rows[cfg["model"]][case] = rs

    n = agree = esc = ok_small_only = ok_large_only = ok_cascade = wrong_when_agree = 0
    usd_small = usd_large = usd_cascade = 0.0
    missing = []
    detail = []
    for case in wanted:
        s, l = rows[args.small].get(case) or [], rows[args.large].get(case) or []
        if len(s) < 3 or not l:
            missing.append(case)
            continue
        n += 1
        s3, big = s[:3], l[0]
        small_cost = sum(r["usd"] for r in s3)
        usd_small += small_cost
        usd_large += big["usd"]
        unanimous = len({key_of(r) for r in s3}) == 1
        ok_small_only += good(s3[0])
        ok_large_only += good(big)
        if unanimous:
            agree += 1
            ok_cascade += good(s3[0])
            wrong_when_agree += not good(s3[0])
            usd_cascade += small_cost
            if not good(s3[0]):
                detail.append(f"  agreed and wrong: {case}  small said {key_of(s3[0])!r}, truth {s3[0]['truth']}")
        else:
            esc += 1
            ok_cascade += good(big)
            usd_cascade += small_cost + big["usd"]
            detail.append(f"  escalated: {case}  small said {[key_of(r) for r in s3]}, large said {key_of(big)!r} ({'ok' if good(big) else 'wrong'}), truth {big['truth']}")
    if not n:
        print(f"no case has 3 answers of {args.small} and 1 of {args.large} under {PROMPT_VERSION} / {args.condition}; missing {len(missing)}")
        return 1
    print(f"{args.condition}, prompt {PROMPT_VERSION}: {n} cases (not counted: {len(missing)} without the needed answers)")
    print(f"  small model, first answer only : good {ok_small_only}/{n}   {usd_small / 3 / n:.4f} USD per case")
    print(f"  large model only               : good {ok_large_only}/{n}   {usd_large / n:.4f} USD per case")
    print(f"  cascade (3 small, large on split): good {ok_cascade}/{n}   {usd_cascade / n:.4f} USD per case   escalated {esc}/{n}, agreed {agree}/{n}, agreed-and-wrong {wrong_when_agree}")
    print("\n".join(detail))
    return 0


if __name__ == "__main__":
    sys.exit(main())
