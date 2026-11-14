"""One-call methods (A0, A2, A3, A4): one request per (case, repetition), graded by code.

    eval\\.venv\\Scripts\\python.exe eval\\run_single.py --cases F:\\pca-vm\\cases --condition A4 --model claude-sonnet-5-5 --effort medium --phase pilot --dry-run
    ... without --dry-run to spend money. --only <text> keeps cases whose id contains the text, --limit N the first N.

Rules this runner keeps (docs/evaluation-design-v3.md, eval health checklist):
  - every paid call is checked against the phase limit before it is made and written to the ledger after;
  - model input comes from the dump only (cases.py); the answer key is read by the grader, never put into a request;
  - the model that answered must be the model asked for - no fallback model (a refusal is recorded as a refusal);
  - a response that could not be used (refusal, cut off, not parseable) is its own status, not a wrong answer;
  - the full request and response of every call are kept under eval/runs/<run id>/.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import anthropic

sys.path.insert(0, str(Path(__file__).parent))
import ledger  # noqa: E402
from cases import load_cases  # noqa: E402
from count_tokens import api_key  # noqa: E402
from grade import grade  # noqa: E402
from pricing import PRICES, cost_usd, usage_cost  # noqa: E402
from prompts import ANSWER_SCHEMA, PROMPT_VERSION, SYSTEM, user_message  # noqa: E402

MAX_TOKENS = 16000
ASSUMED_OUTPUT_TOKENS = 6000   # for the pre-call estimate only (thinking + answer); the ledger records the real usage


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", required=True)
    ap.add_argument("--replay", default=None, help="folder with rebuilt snapshots for dumps made before resource_recent existed")
    ap.add_argument("--condition", required=True, choices=["A0", "A2", "A3", "A4"])
    ap.add_argument("--model", required=True, choices=sorted(PRICES))
    ap.add_argument("--effort", default=None, choices=["low", "medium", "high", "xhigh", "max"], help="omit = the model's default")
    ap.add_argument("--reps", type=int, default=1)
    ap.add_argument("--phase", required=True)
    ap.add_argument("--only", default=None)
    ap.add_argument("--set", dest="case_set", default=None, help="text file with one case id per line (eval/sets/*.txt); a named, fixed list of cases")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--include-leaky", action="store_true", help="also run cases whose culprit name gives the answer away (plumbing and cost only)")
    ap.add_argument("--dry-run", action="store_true", help="count tokens and estimate the cost, make no paid call")
    args = ap.parse_args()

    cases = load_cases(args.cases, args.replay)
    if args.case_set:
        wanted = [ln.split("#")[0].strip() for ln in Path(args.case_set).read_text(encoding="utf-8").splitlines()]
        wanted = [w for w in wanted if w]
        by_id = {c.id: c for c in cases}
        missing = [w for w in wanted if w not in by_id]
        if missing:
            print(f"case set {args.case_set}: not found or not usable: {missing}")
            return 1
        cases = [by_id[w] for w in wanted]
    if args.only:
        cases = [c for c in cases if args.only in c.id]
    if not args.include_leaky:
        cases = [c for c in cases if not c.leaky]
    if args.limit:
        cases = cases[: args.limit]
    if not cases:
        print("no cases selected (cases without an alias are skipped unless --include-leaky)")
        return 1

    client = anthropic.Anthropic(api_key=api_key(), max_retries=3)
    run_id = time.strftime("%Y%m%d-%H%M%S") + f"-{args.condition}-{args.model.replace('claude-', '')}" + (f"-{args.effort}" if args.effort else "")
    out_dir = Path(__file__).parent / "runs" / run_id

    # what each request will be, and what the run is expected to cost
    plan = []
    for c in cases:
        text, ids = user_message(args.condition, c)
        n_in = client.messages.count_tokens(model=args.model, system=SYSTEM, messages=[{"role": "user", "content": text}]).input_tokens
        plan.append((c, text, ids, n_in))
    est = sum(cost_usd(args.model, n_in, ASSUMED_OUTPUT_TOKENS) for _, _, _, n_in in plan) * args.reps
    print(f"run {run_id}: {len(plan)} cases x {args.reps} rep(s), input tokens min {min(p[3] for p in plan):,} max {max(p[3] for p in plan):,}, estimated {est:.2f} USD (phase '{args.phase}', {ledger.remaining(args.phase):.2f} left)")
    if args.dry_run:
        for c, _, ids, n_in in plan:
            print(f"  {c.id:48} in {n_in:>8,}  items {len(ids) if ids is not None else '-'}{'  LEAKY' if c.leaky else ''}")
        return 0
    ledger.check(args.phase, est)

    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "transcripts").mkdir()
    config = {"run_id": run_id, "condition": args.condition, "model": args.model, "effort": args.effort, "reps": args.reps, "phase": args.phase, "case_set": args.case_set,
              "prompt_version": PROMPT_VERSION, "max_tokens": MAX_TOKENS, "cases": [c.id for c in cases], "estimate_usd": round(est, 4)}
    (out_dir / "config.json").write_text(json.dumps(config, indent=2, ensure_ascii=False), encoding="utf-8")

    output_config: dict = {"format": {"type": "json_schema", "schema": ANSWER_SCHEMA}}
    if args.effort:
        output_config["effort"] = args.effort

    rows = 0
    for c, text, ids, n_in in plan:
        for rep in range(1, args.reps + 1):
            ledger.check(args.phase, cost_usd(args.model, n_in, ASSUMED_OUTPUT_TOKENS))
            request = {"model": args.model, "max_tokens": MAX_TOKENS, "system": SYSTEM, "messages": [{"role": "user", "content": text}], "output_config": output_config}
            # An answer cut off at max_tokens is asked for once more (2026-11-14: 2 of 36 Sonnet answers ran into a loop
            # of repeated characters inside a JSON string and stopped at 16,000 tokens; the answer before the loop was
            # fine). Both attempts are paid and recorded; the row says how many attempts it took. A second cut-off
            # stays 'truncated' and is not scored.
            msg, attempts, usd_before = None, 0, 0.0
            t0 = time.time()
            while attempts < 2:
                attempts += 1
                try:
                    # streamed so that a long answer to a 400k-token prompt cannot hit an HTTP timeout
                    with client.messages.stream(**request) as stream:
                        msg = stream.get_final_message()
                except anthropic.APIStatusError as e:
                    _append(out_dir / "errors.jsonl", {"case": c.id, "rep": rep, "class": "api_error", "status": e.status_code, "message": str(e)[:500]})
                    print(f"  {c.id} r{rep}: API error {e.status_code} - not scored")
                    msg = None
                    break
                except anthropic.APIConnectionError as e:
                    _append(out_dir / "errors.jsonl", {"case": c.id, "rep": rep, "class": "connection", "message": str(e)[:500]})
                    print(f"  {c.id} r{rep}: connection error - not scored")
                    msg = None
                    break
                usage = msg.usage.to_dict()
                usd = usage_cost(args.model, usage)
                ledger.record(args.phase, args.model, usd, usage, run_id, f"{c.id} r{rep}" + (f" attempt {attempts}" if attempts > 1 else ""))
                if msg.stop_reason != "max_tokens" or attempts == 2:
                    break
                usd_before += usd
                _append(out_dir / "errors.jsonl", {"case": c.id, "rep": rep, "class": "truncated_retry", "usd": round(usd, 6), "output_tokens": usage.get("output_tokens")})
                print(f"  {c.id} r{rep}: cut off at max_tokens - asking once more")
            if msg is None:
                continue
            seconds = round(time.time() - t0, 1)
            usd += usd_before  # what this answer cost, the discarded attempt included
            if not msg.model.startswith(args.model):
                _append(out_dir / "errors.jsonl", {"case": c.id, "rep": rep, "class": "served_model_mismatch", "asked": args.model, "served": msg.model})
                print(f"  {c.id} r{rep}: served by {msg.model}, asked for {args.model} - not scored")
                continue

            status, answer = "ok", None
            if msg.stop_reason == "refusal":
                status = "refusal"
            elif msg.stop_reason == "max_tokens":
                status = "truncated"
            else:
                body = next((b.text for b in msg.content if b.type == "text"), None)
                try:
                    answer = json.loads(body) if body else None
                except json.JSONDecodeError:
                    answer = None
                if answer is None:
                    status = "unparseable"
            g = grade(answer, c.truth, ids, status)
            row = {"case": c.id, "family": c.family, "clone": c.clone, "rep": rep, "leaky": c.leaky, "truth": c.truth.get("culprit"), "flags": c.meta.get("flags") or [], "condition": args.condition, "model": args.model,
                   "effort": args.effort, "stop_reason": msg.stop_reason, "grade": g, "first": (answer or {}).get("candidates", [{}])[0].get("name") if (answer or {}).get("candidates") else None,
                   "usage": usage, "usd": round(usd, 6), "attempts": attempts, "seconds": seconds, "message_id": msg.id, "request_id": getattr(msg, "_request_id", None)}
            _append(out_dir / "results.jsonl", row)
            (out_dir / "transcripts" / f"{c.id.replace('/', '__')}__r{rep}.json").write_text(
                json.dumps({"request": request, "response": msg.to_dict(), "answer": answer, "grade": g, "truth": c.truth}, indent=2, ensure_ascii=False), encoding="utf-8")
            rows += 1
            mark = "top1" if g["top1"] else "none-ok" if g["none_correct"] else "abstain" if g["abstained"] else status if status != "ok" else "miss"
            print(f"  {c.id:46} r{rep} {mark:8} first={row['first']!s:22} in {usage.get('input_tokens', 0):>7,} out {usage.get('output_tokens', 0):>6,}  {usd:.4f} USD  {seconds:>5}s")
            if ledger.near_limit(args.phase):
                print(f"phase '{args.phase}' has reached 80% of its limit - stopping here.")
                print(ledger.report())
                return 3
    print(f"{rows} answers scored -> {out_dir}")
    print(ledger.report())
    return 0


def _append(path: Path, row: dict) -> None:
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ledger.BudgetExceeded as e:
        print(f"BUDGET: {e}")
        sys.exit(4)
