"""Rebuild the engine snapshot of every dump with the CURRENT collector summary (resource_recent).

Why: the summary a dump carries was written by the collector build that ran in the guest. When the summary rules change
(2026-11-12 resource_recent added, 2026-11-14 cpu_avg_active / io_mbps_avg_active / busy_ge90_samples), older dumps
would be read with the old fields. The raw samples are in the dump's events.json, so the summary can be computed again:

    BlackBox --resource-recent <events.json>      (read-only, collects nothing)

Rebuilt the same way since 2026-11-15: merged_triggers (from meta.json) and process_exits
(BlackBox --process-exits <events.json> [target]).

The rebuilt snapshot = the dump's engine-snapshot.json with those fields replaced, written to
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


def process_exits(exe: Path, events: Path, target: str) -> dict | None:
    """BlackBox --process-exits <events.json> [target]: the exits of the dump window (collector field since 2026-11-15)."""
    env = dict(os.environ)
    env.setdefault("DOTNET_ROLL_FORWARD", "Major")
    r = subprocess.run([str(exe), "--process-exits", str(events)] + ([target] if target else []), capture_output=True, env=env)
    if r.returncode != 0:
        raise RuntimeError(f"collector exit {r.returncode} on {events}: {r.stderr.decode('utf-8', 'replace')[:300]}")
    text = r.stdout.decode("utf-8-sig", "replace").strip()
    return json.loads(text) if text and text != "null" else None


def merged_from_meta(meta_path: Path) -> list:
    """meta.json trigger.mergedWithinCooldown -> the engine snapshot's merged_triggers (EngineSnapshotBuilder)."""
    if not meta_path.exists():
        return []
    trig = json.loads(meta_path.read_text(encoding="utf-8-sig")).get("trigger")
    rows = (trig or {}).get("mergedWithinCooldown") if isinstance(trig, dict) else None
    return [{"kind": m.get("Kind"), "event_id": m.get("EventId"), "received_at": m.get("ReceivedAt"), "process": m.get("ProcessName"),
             "faulting_module": m.get("FaultingModule"), "exception_code": m.get("ExceptionCode"), "wer_event_type": m.get("WerEventType"),
             "symptom_type": (m.get("Class") or {}).get("SymptomType")} for m in rows or []]


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
    sys.path.insert(0, str(Path(__file__).parent))
    from cases import is_test_run, test_session_windows       # test-set runs are left alone until they are moved
    windows = test_session_windows(root)
    for case_dir in sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir() and p.name >= args.since):
            dumps = run / "dumps"
            if not dumps.exists() or is_test_run(root, run.name, windows):
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
                # merged_triggers (collector field since 2026-11-15): other automatic triggers that arrived within the
                # cooldown and were folded into this dump. Older dumps have them in meta.json only - same field, same
                # content, so that every case is read the same way whatever collector build made it.
                mg = merged_from_meta(d / "meta.json")
                if mg:
                    s["merged_triggers"] = mg
                else:
                    s.pop("merged_triggers", None)
                # process_exits (collector field since 2026-11-15): processes that ended in the dump window, by name and
                # exit code. A program that closed without a Windows crash event is found only here.
                px = process_exits(exe, events, (s.get("symptom") or {}).get("target_process") or "")
                if px is None:
                    s.pop("process_exits", None)
                else:
                    s["process_exits"] = px
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
