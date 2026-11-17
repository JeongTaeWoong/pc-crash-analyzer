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
    ap.add_argument("--since", default=None, help="first run folder name to move (yyyymmdd[-hhmmss]); runs before it stay")
    ap.add_argument("--until", default="99999999", help="runs with a folder name >= this stay")
    ap.add_argument("--auto", action="store_true", help="take the time ranges from the session logs: every session that ran a plan named test-*.json")
    ap.add_argument("--move", action="store_true")
    args = ap.parse_args()
    src, dst = Path(args.src), Path(args.dst)
    if args.auto:
        sys.path.insert(0, str(Path(__file__).parent))
        from cases import test_session_windows
        windows = test_session_windows(src)
        open_ended = [w for w in windows if w[1] == "99999999"]
        if open_ended:
            print(f"a test-set session is still running (started {open_ended[0][0]}): wait for it to end")
            return 2
        print("test-set sessions:", ", ".join(f"{a}..{b}" for a, b in windows) or "none")
    elif args.since:
        windows = [(args.since, args.until)]
    else:
        ap.error("give --since or --auto")
    n = 0
    for case_dir in sorted(p for p in src.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir() and any(a <= p.name <= b if args.auto else a <= p.name < b for a, b in windows)):
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
