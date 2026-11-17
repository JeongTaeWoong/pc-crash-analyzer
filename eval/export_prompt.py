"""Write the prompt, the answer format and reference bundles the PRODUCT uses (ui/wpf/CrashAnalyzer.Ai) from this harness.

The product must send what the experiment measured: the same instruction (eval/prompts.py), the same answer schema and the
same evidence bundle (eval/cases.py build_bundle). The C# side embeds the three files written here and its tests compare
its own bundle text with the reference bundles, character for character.

    eval\\.venv\\Scripts\\python.exe eval\\export_prompt.py            write the files
    eval\\.venv\\Scripts\\python.exe eval\\export_prompt.py --check    exit 1 when the product's files differ from the harness

Reference bundles are made from lab (virtual machine) development cases only - never from the test set, never from
participants' records.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from cases import BUNDLE_VERSION, build_bundle, load_cases  # noqa: E402
from prompts import ANSWER_SCHEMA, PROMPT_VERSION, SYSTEM  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
RES = REPO / "ui" / "wpf" / "CrashAnalyzer.Ai" / "Resources"
GOLD = REPO / "ui" / "wpf" / "CrashAnalyzer.Ai.Tests" / "golden"
# one case of each shape the bundle has: a crash with modules, a program that closed without a crash record (process
# exits), a slow PC (resource lists), a crash merged into another event's dump (merged triggers), a PC with nothing wrong
GOLDEN_CASES = ["design1-real-git-maintenance/20261115-065854", "office1-real-blender-bgscene/20261115-064319", "office1-work-7zip/20261114-163151",
                "office2-sys-defender-scan/20261115-070333", "dev1-cpu-hog+alias/20261114-033045", "office1-idle+alias/20261114-023032"]


def outputs() -> dict[Path, str]:
    out = {
        RES / "system-prompt.txt": SYSTEM,
        RES / "answer-schema.json": json.dumps(ANSWER_SCHEMA, ensure_ascii=False, indent=1),
        RES / "versions.json": json.dumps({"prompt": PROMPT_VERSION, "bundle": BUNDLE_VERSION}, indent=1),
    }
    cases = {c.id: c for c in load_cases(r"F:\pca-vm\cases")}
    for cid in GOLDEN_CASES:
        c = cases.get(cid)
        if c is None:
            continue
        c.event = 0                                                     # the product has no per-event view yet: the dump as it is
        snap = c.snapshot()
        name = cid.replace("/", "__").replace("+", "_")
        out[GOLD / f"{name}.snapshot.json"] = json.dumps(snap, ensure_ascii=False, indent=1)
        out[GOLD / f"{name}.bundle.txt"] = build_bundle(snap, None)[0]
    return out


def main() -> int:
    check = "--check" in sys.argv
    bad = 0
    for path, text in outputs().items():
        if check:
            have = path.read_text(encoding="utf-8") if path.exists() else None
            if have != text:
                bad += 1
                print(f"DIFFERENT: {path.relative_to(REPO)}")
        else:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8", newline="\n")
            print(f"wrote {path.relative_to(REPO)} ({len(text):,} chars)")
    if check:
        print("product files match the harness" if not bad else f"{bad} file(s) differ - run eval\\export_prompt.py and rebuild")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
