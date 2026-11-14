"""How many input tokens does a case cost in each form? (measured with the API's count_tokens, not estimated)

    eval\\.venv\\Scripts\\python.exe eval\\count_tokens.py --cases F:\\pca-vm\\cases --replay <folder with patched snapshots>

Forms of one dump:
  raw      events.json + snapshot.json as written by the collector (everything in the dump window)
  summary  engine-snapshot.json as written at the time (no resource_recent)
  summary+ the same snapshot rebuilt with today's collector summary (resource_recent) - from --replay, if given

Only lab VM cases are read (synthetic PCs, no participant data). The API key comes from the environment
(ANTHROPIC_API_KEY at user scope); it is never written anywhere. Output: eval/runs/token-counts.json + a table.
"""
from __future__ import annotations

import argparse
import json
import os
import statistics
import sys
import winreg
from pathlib import Path

import anthropic

MODELS = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"]


def api_key() -> str | None:
    """The key was set at user scope; a shell started earlier does not have it in its environment yet."""
    if os.environ.get("ANTHROPIC_API_KEY"):
        return os.environ["ANTHROPIC_API_KEY"]
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as k:
            return winreg.QueryValueEx(k, "ANTHROPIC_API_KEY")[0]
    except OSError:
        return None


def count(client: anthropic.Anthropic, model: str, text: str) -> int:
    return client.messages.count_tokens(model=model, messages=[{"role": "user", "content": text}]).input_tokens


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", required=True)
    ap.add_argument("--replay", default=None, help="folder with <case>__<dump>.json snapshots that include resource_recent")
    ap.add_argument("--all-models-for", type=int, default=3, help="count with every model for the first N dumps, the rest with the first model only")
    ap.add_argument("--out", default=str(Path(__file__).parent / "runs" / "token-counts.json"))
    args = ap.parse_args()

    key = api_key()
    if not key:
        print("ANTHROPIC_API_KEY is not set", file=sys.stderr)
        return 2
    client = anthropic.Anthropic(api_key=key)

    rows = []
    case_root = Path(args.cases)
    dumps = sorted(p.parent for p in case_root.glob("*/*/dumps/*/engine-snapshot.json") if not p.parts[len(case_root.parts)].startswith("_"))
    for i, d in enumerate(dumps):
        case = d.parents[2].name
        kind = d.name.split("-", 2)[-1]
        if kind.startswith("kernel-live"):
            continue  # the clone's own warning dumps are not cases
        raw = (d / "events.json").read_text(encoding="utf-8-sig") + "\n" + (d / "snapshot.json").read_text(encoding="utf-8-sig")
        summary = (d / "engine-snapshot.json").read_text(encoding="utf-8-sig")
        plus = None
        if args.replay:
            rp = Path(args.replay) / f"{case}__{d.name}.json"
            if rp.exists():
                plus = rp.read_text(encoding="utf-8-sig")
        models = MODELS if len(rows) < args.all_models_for else MODELS[:1]
        row = {"case": case, "dump": d.name, "kind": kind, "bytes": {"raw": len(raw.encode()), "summary": len(summary.encode()), "summary_plus": len(plus.encode()) if plus else None}, "tokens": {}}
        for m in models:
            t = {"raw": count(client, m, raw), "summary": count(client, m, summary)}
            if plus:
                t["summary_plus"] = count(client, m, plus)
            row["tokens"][m] = t
        rows.append(row)
        t0 = row["tokens"][MODELS[0]]
        print(f"{case:22} {kind:11} raw {t0['raw']:>8,}  summary {t0['summary']:>6,}  summary+ {t0.get('summary_plus', 0):>6,}  ratio {t0['raw'] / max(1, t0.get('summary_plus') or t0['summary']):5.1f}x")

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rows, indent=2), encoding="utf-8")

    m0 = MODELS[0]
    for form in ("raw", "summary", "summary_plus"):
        vals = [r["tokens"][m0][form] for r in rows if form in r["tokens"][m0]]
        if vals:
            print(f"{m0} {form:13} n={len(vals)} min {min(vals):,} median {int(statistics.median(vals)):,} max {max(vals):,}")
    print("--- same dump, each model's tokenizer")
    for r in rows:
        if len(r["tokens"]) > 1:
            print(f"{r['case']:22} " + "  ".join(f"{m.replace('claude-', '')}: raw {t['raw']:,} / summary {t['summary']:,}" for m, t in r["tokens"].items()))
    bpt = [r["bytes"]["raw"] / r["tokens"][m0]["raw"] for r in rows]
    print(f"bytes per token (raw JSON): {statistics.mean(bpt):.2f}")
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
