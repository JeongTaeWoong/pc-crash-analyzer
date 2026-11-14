"""Move the runs of a test-set session out of the development case folder.

case.ps1 writes every run to <vm root>\\cases. Runs made by a test-set plan (lab/vm/plans/test-*.json) must not stay
there: the development tools (review with the engine, eval/sets/dev-*.txt, prompt work) read that folder. This moves
the run folders made at or after --since to <vm root>\\cases-test, keeping <clone>-<scenario>[+alias]\\<time>.

    eval\\.venv\\Scripts\\python.exe eval\\split_test.py F:\\pca-vm\\cases F:\\pca-vm\\cases-test --since 20261114-23          (lists)
    eval\\.venv\\Scripts\\python.exe eval\\split_test.py F:\\pca-vm\\cases F:\\pca-vm\\cases-test --since 20261114-23 --move

Then, on the test folder only:
    eval\\replay.py F:\\pca-vm\\cases-test
    eval\\review_cases.py F:\\pca-vm\\cases-test --no-engine --write
Until the test set is frozen and used once (docs/evaluation-design-v3.md), neither the engine nor a model is run on it.
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--since", required=True, help="first run folder name to move (yyyymmdd[-hhmmss]); runs before it stay")
    ap.add_argument("--until", default="99999999", help="runs with a folder name >= this stay")
    ap.add_argument("--move", action="store_true")
    args = ap.parse_args()
    src, dst = Path(args.src), Path(args.dst)
    n = 0
    for case_dir in sorted(p for p in src.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir() and args.since <= p.name < args.until):
            target = dst / case_dir.name / run.name
            n += 1
            if not args.move:
                print(f"would move {case_dir.name}\\{run.name}")
                continue
            if target.exists():
                print(f"SKIP (already there): {target}")
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(run), str(target))
            print(f"moved {case_dir.name}\\{run.name}")
        if args.move and not any(case_dir.iterdir()):
            case_dir.rmdir()  # a case folder that only held test runs
    print(f"{n} run(s) " + ("moved" if args.move else "would be moved (add --move)") + f" to {dst}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
