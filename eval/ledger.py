"""Spending ledger: every paid API call goes through here (docs/evaluation-design-v3.md section 8).

  check(phase, estimate)   before a call or a run - raises BudgetExceeded when the phase limit would be passed
  record(...)              after a call - appends what it cost to eval/runs/ledger.jsonl

The limits are in eval/budget.json. The reserve is not a phase: nothing can be charged to it. To use reserve money,
the limits in budget.json are changed by hand after the owner agrees - on purpose not a command-line switch.
The ledger is our own arithmetic (usage x price table); the real bill is checked against the Console weekly.
"""
from __future__ import annotations

import json
import time
from pathlib import Path

HERE = Path(__file__).parent
BUDGET = HERE / "budget.json"
LEDGER = HERE / "runs" / "ledger.jsonl"
WARN_AT = 0.8


class BudgetExceeded(RuntimeError):
    pass


def limits() -> dict:
    return json.loads(BUDGET.read_text(encoding="utf-8"))


def spent(phase: str | None = None) -> float:
    if not LEDGER.exists():
        return 0.0
    total = 0.0
    for line in LEDGER.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if phase is None or row["phase"] == phase:
            total += row["usd"]
    return total


def remaining(phase: str) -> float:
    lim = limits()["phases"]
    if phase not in lim:
        raise BudgetExceeded(f"'{phase}' is not a budget phase ({', '.join(lim)}). The reserve cannot be charged.")
    return lim[phase]["limit_usd"] - spent(phase)


def check(phase: str, estimate_usd: float = 0.0) -> None:
    b = limits()
    left = remaining(phase)
    if estimate_usd > left:
        raise BudgetExceeded(f"phase '{phase}': {left:.2f} USD left of {b['phases'][phase]['limit_usd']:.2f}, this needs about {estimate_usd:.2f}. Stop and report.")
    if spent() + estimate_usd > b["total_usd"] - b["reserve_usd"]:
        raise BudgetExceeded(f"all phases together would pass {b['total_usd'] - b['reserve_usd']:.2f} USD (total {b['total_usd']:.0f} minus reserve {b['reserve_usd']:.0f}). Stop and report.")


def near_limit(phase: str) -> bool:
    lim = limits()["phases"][phase]["limit_usd"]
    return spent(phase) >= WARN_AT * lim


def record(phase: str, model: str, usd: float, usage: dict, run_id: str, note: str = "") -> None:
    LEDGER.parent.mkdir(parents=True, exist_ok=True)
    row = {"at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "phase": phase, "model": model, "usd": round(usd, 6), "usage": usage, "run_id": run_id, "note": note}
    with LEDGER.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


def report() -> str:
    b = limits()
    lines = [f"{'phase':10} {'spent':>8} {'limit':>8}"]
    for name, p in b["phases"].items():
        s = spent(name)
        lines.append(f"{name:10} {s:8.2f} {p['limit_usd']:8.2f}{'   <- 80% reached' if s >= WARN_AT * p['limit_usd'] else ''}")
    lines.append(f"{'all':10} {spent():8.2f} {b['total_usd'] - b['reserve_usd']:8.2f}   (reserve {b['reserve_usd']:.0f} not included, total {b['total_usd']:.0f})")
    return "\n".join(lines)


if __name__ == "__main__":
    print(report())
