"""Is the raw-dump token count inflated by pretty-printing? Count three dumps as written and minified.
The raw baseline of the experiment (A2) must be the fairest form: nobody would send indentation to the model."""
import json
import sys
from pathlib import Path

import anthropic

sys.path.insert(0, str(Path(__file__).parent))
from count_tokens import api_key, count  # noqa: E402

client = anthropic.Anthropic(api_key=api_key())
model = "claude-opus-5-5"
dumps = sorted(p.parent for p in Path(sys.argv[1]).glob("*/*/dumps/*/engine-snapshot.json") if "kernel-live" not in p.parent.name and not p.parts[-5].startswith("_"))
for d in dumps[:: max(1, len(dumps) // 3)][:3]:
    ev = (d / "events.json").read_text(encoding="utf-8-sig")
    sn = (d / "snapshot.json").read_text(encoding="utf-8-sig")
    mini = lambda s: json.dumps(json.loads(s), ensure_ascii=False, separators=(",", ":"))  # noqa: E731
    events = json.loads(ev)
    events = events if isinstance(events, list) else events["events"]
    by_src: dict[str, int] = {}
    for e in events:
        by_src[e["source"]] = by_src.get(e["source"], 0) + len(json.dumps(e, ensure_ascii=False, separators=(",", ":")))
    a, b = count(client, model, ev + "\n" + sn), count(client, model, mini(ev) + "\n" + mini(sn))
    print(f"{d.parents[2].name:22} as written {a:>8,}  minified {b:>8,}  ({100 * b / a:.0f}%)  events only minified {count(client, model, mini(ev)):>8,}")
    total = sum(by_src.values())
    print("    minified bytes by source: " + ", ".join(f"{k} {100 * v / total:.0f}%" for k, v in sorted(by_src.items(), key=lambda x: -x[1])))
