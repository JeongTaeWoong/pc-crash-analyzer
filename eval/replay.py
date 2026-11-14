"""Rebuild the engine snapshot of every dump with the CURRENT collector summary (resource_recent).

Why: the summary a dump carries was written by the collector build that ran in the guest. When the summary rules change
(2026-11-12 resource_recent added, 2026-11-14 cpu_avg_active / io_mbps_avg_active / busy_ge90_samples), older dumps
would be read with the old fields. The raw samples are in the dump's events.json, so the summary can be computed again:

    BlackBox --resource-recent <events.json>      (read-only, collects nothing)

The rebuilt snapshot = the dump's engine-snapshot.json with resource_recent replaced, written to
<cases root>/_replay/<case folder>__<dump folder>.json. eval/cases.py and eval/review_cases.py read that file when it
exists. Nothing in the dump itself is changed.

    eval\\.venv\\Scripts\\python.exe eval\\replay.py F:\\pca-vm\\cases [--since 20261114] [--check]

--check  rebuild nothing; report dumps whose replay is missing or older than the collector build.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
COLLECTOR_CANDIDATES = [
    REPO / "collector" / "BlackBox" / "bin" / "Debug" / "net8.0-windows" / "BlackBox.exe",
    REPO / "collector" / "BlackBox" / "bin" / "Release" / "net8.0-windows" / "BlackBox.exe",
]


def collector() -> Path:
    built = [p for p in COLLECTOR_CANDIDATES if p.exists()]
    if not built:
        raise SystemExit("no collector build found - run: dotnet build collector\\BlackBox")
    return max(built, key=lambda p: p.stat().st_mtime)


def summarize(exe: Path, events: Path) -> dict | None:
    env = dict(os.environ)
    env.setdefault("DOTNET_ROLL_FORWARD", "Major")  # the host has no .NET 8 runtime since the reinstall (NOTES 11/10)
    r = subprocess.run([str(exe), "--resource-recent", str(events)], capture_output=True, env=env)
    if r.returncode != 0:
        raise RuntimeError(f"collector exit {r.returncode} on {events}: {r.stderr.decode('utf-8', 'replace')[:300]}")
    text = r.stdout.decode("utf-8-sig", "replace").strip()
    return json.loads(text) if text and text != "null" else None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("root")
    ap.add_argument("--since", default="", help="only runs whose folder name is >= this (yyyymmdd[-hhmmss])")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    root = Path(args.root)
    out_dir = root / "_replay"
    exe = collector()
    built_at = exe.stat().st_mtime
    made = stale = skipped = 0
    for case_dir in sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir() and p.name >= args.since):
            dumps = run / "dumps"
            if not dumps.exists():
                continue
            for d in sorted(p for p in dumps.iterdir() if p.is_dir()):
                snap, events = d / "engine-snapshot.json", d / "events.json"
                if not snap.exists() or not events.exists():
                    skipped += 1
                    continue
                target = out_dir / f"{case_dir.name}__{d.name}.json"
                if args.check:
                    if not target.exists() or target.stat().st_mtime < built_at:
                        stale += 1
                        print(f"stale: {target.name}")
                    continue
                s = json.loads(snap.read_text(encoding="utf-8-sig"))
                rr = summarize(exe, events)
                if rr is None:
                    s.pop("resource_recent", None)
                else:
                    s["resource_recent"] = rr
                out_dir.mkdir(exist_ok=True)
                target.write_text(json.dumps(s, ensure_ascii=False, indent=2), encoding="utf-8")
                made += 1
    if args.check:
        print(f"{stale} replay snapshot(s) missing or older than the collector build ({exe})")
        return 1 if stale else 0
    print(f"rebuilt {made} snapshot(s) into {out_dir} with {exe} ({skipped} dump(s) without events or snapshot skipped)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
