"""Code grader for one answer (docs/evaluation-design-v3.md section 2). No model is involved here.

Graded properties, each separate:
  top1 / top3        the true culprit is the first / among the first three candidates
  none_correct       healthy case: the answer says no problem was found
  false_accusation   a program that is not the culprit is named first with high or medium confidence
  grounded           share of cited evidence ids that exist in what the model was shown (None when the input had no ids)
An answer that could not be produced (refusal, cut off, not parseable) is NOT a wrong answer: status says which.

    eval\\.venv\\Scripts\\python.exe eval\\grade.py --selftest F:\\pca-vm\\cases
"""
from __future__ import annotations

import re
import sys


def norm(name: str | None) -> str:
    """'C:\\X\\DocSyncAgent.exe' / 'docsyncagent' / 'DocSyncAgent (프로그램)' -> 'docsyncagent'"""
    if not name:
        return ""
    n = name.strip().lower().replace("/", "\\").split("\\")[-1]
    n = re.sub(r"\s*\(.*?\)\s*", "", n)
    n = re.sub(r"\.(exe|dll|sys)\b", "", n)
    return re.sub(r"[^a-z0-9가-힣]", "", n)


def same(a: str | None, b: str | None) -> bool:
    x, y = norm(a), norm(b)
    return bool(x) and bool(y) and (x == y or (len(y) >= 5 and y in x) or (len(x) >= 5 and x in y))


def grade(answer: dict | None, truth: dict, ids: set[str] | None, status: str = "ok") -> dict:
    g = {"status": status, "top1": None, "top3": None, "none_correct": None, "abstained": None, "false_accusation": None, "grounded": None, "cited": 0, "verdict": None, "first_kind": None}
    if status != "ok" or answer is None:
        return g
    cands = answer.get("candidates") or []
    culprit = truth.get("culprit")
    g["verdict"] = answer.get("verdict")
    first = cands[0] if cands else None
    # kind of the first candidate: on a PC that is slow because of its own hardware (review flag hardware_limit) naming
    # the part (kind hardware) is a sound answer and naming a program is not - eval/summarize.py counts those apart
    g["first_kind"] = first.get("kind") if first else None
    accuses = answer.get("verdict") == "culprit_found" and first is not None and first.get("confidence") in ("high", "medium")
    if culprit:
        # truth.also = other names the same culprit goes by (a real program: 'MsMpEng.exe' is 'Microsoft Defender')
        names = [culprit] + list(truth.get("also") or [])
        g["top1"] = bool(first) and any(same(first.get("name"), n) for n in names)
        g["top3"] = any(same(c.get("name"), n) for c in cands[:3] for n in names)
        g["false_accusation"] = bool(accuses and not g["top1"])
    else:
        # A healthy case has two acceptable outcomes that are counted apart: saying outright that nothing is wrong
        # (none_correct), and naming no culprit while saying the evidence does not show one (abstained). In the first
        # paid test (2026-11-12) both models chose the second on a healthy PC - a sound answer, not a miss.
        g["none_correct"] = answer.get("verdict") == "no_problem_found"
        g["abstained"] = not accuses
        g["false_accusation"] = bool(accuses)
    cited = [i for c in cands for i in (c.get("evidence_ids") or [])]
    g["cited"] = len(cited)
    if ids is not None:
        g["grounded"] = (sum(1 for i in cited if i in ids) / len(cited)) if cited else None
    return g


def selftest(case_root: str) -> int:
    """Oracle answers must pass, empty and constant answers must fail on culprit cases."""
    from cases import load_cases
    cases = load_cases(case_root)
    bad = 0
    for c in cases:
        culprit = c.truth.get("culprit")
        oracle = {"verdict": "culprit_found" if culprit else "no_problem_found",
                  "candidates": [{"name": culprit, "kind": "process", "confidence": "high", "evidence_ids": ["sym"], "reason": ""}] if culprit else []}
        empty = {"verdict": "insufficient_evidence", "candidates": []}
        always_none = {"verdict": "no_problem_found", "candidates": []}
        wrong = {"verdict": "culprit_found", "candidates": [{"name": "svchost.exe", "kind": "process", "confidence": "high", "evidence_ids": ["nope"], "reason": ""}]}
        o, e, n, w = (grade(a, c.truth, {"sym"}) for a in (oracle, empty, always_none, wrong))
        if culprit:
            ok = o["top1"] and o["top3"] and not o["false_accusation"] and not e["top1"] and not n["top1"] and not w["top1"] and w["false_accusation"] and w["grounded"] == 0.0 and o["grounded"] == 1.0
        else:
            ok = o["none_correct"] and not e["none_correct"] and e["abstained"] and n["none_correct"] and w["false_accusation"] and not w["none_correct"] and not w["abstained"]
        if not ok:
            bad += 1
            print(f"FAIL {c.id}: oracle={o} empty={e} none={n} wrong={w}")
    # name matching
    checks = [("DocSyncAgent.exe", "docsyncagent", True), (r"C:\Program Files\X\dsfilter64.dll", "dsfilter64.dll", True), ("FaultLab (프로그램 자체의 메모리 누적)", "FaultLab.exe", True),
              ("svchost.exe", "FaultLab.exe", False), ("", "FaultLab.exe", False), ("chrome", "chromedriver.exe", True), ("lab", "FaultLab.exe", False)]
    for a, b, want in checks:
        if same(a, b) != want:
            bad += 1
            print(f"FAIL name match: {a!r} vs {b!r} expected {want}")
    blocked = grade(None, {"culprit": "x.exe"}, None, status="refusal")
    if blocked["top1"] is not None:
        bad += 1
        print("FAIL: an answer that was not produced must not be graded as wrong")
    print(f"grader self-test: {len(cases)} cases, {len(checks)} name checks, failures {bad}")
    return 1 if bad else 0


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--selftest":
        sys.exit(selftest(sys.argv[2]))
    print(__doc__)
