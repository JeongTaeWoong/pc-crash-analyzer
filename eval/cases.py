"""Cases and what a model is shown (docs/evaluation-design-v3.md sections 2-3).

A case = one run folder of lab/vm/case.ps1:  <root>/<clone>-<scenario>[+alias]/<time>/
  scenario.json   the answer (truth, alias, decoys)          -> grader only, never shown to a model
  result.json     what happened in the guest                 -> grader only
  dumps/<...>/    what the collector wrote                   -> the only source of model input

Model input is built from the dump alone:
  A0  one or two sentences a user would say (fixed per scenario family)
  A2  events.json + snapshot.json as written (the whole dump window)
  A3  the evidence bundle: engine-snapshot.json flattened into numbered items [ev1], [cpu1], ...
  A4  A3 + the engine's candidates as items [eng1], ...
Every item has an id so that a model's citations can be checked by code.
"""
from __future__ import annotations

import json
import subprocess
import tempfile
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
ENGINE = REPO / "engine" / "build" / "Release" / "crash_engine.exe"
SCENARIOS = json.loads((REPO / "lab" / "vm" / "scenarios.json").read_text(encoding="utf-8"))["scenarios"]

# What a user would type into a chat today. One fixed sentence per symptom; it names no program
# (a user who cannot read logs does not know which program is the cause).
SYMPTOM_SENTENCES = {
    "slow": "컴퓨터가 갑자기 많이 느려졌어요. 프로그램 몇 개를 켜 놓고 평소처럼 쓰고 있었는데 클릭해도 한참 있다가 반응해요. 원인이 뭔지, 어떻게 해야 하는지 알려 주세요.",
    "app_crash": "쓰고 있던 프로그램 창이 갑자기 꺼졌어요. 오류 창도 제대로 못 봤습니다. 원인이 뭔지, 어떻게 해야 하는지 알려 주세요.",
    "freeze": "프로그램 창이 하얗게 되면서 '응답 없음'이 뜨고 멈췄어요. 원인이 뭔지, 어떻게 해야 하는지 알려 주세요.",
    "none": "컴퓨터 상태가 괜찮은지 한번 봐 주세요. 특별히 이상한 건 못 느꼈습니다.",
}


@dataclass
class Case:
    id: str
    family: str
    clone: str
    truth: dict
    alias: dict | None
    decoys: list[str]
    leaky: bool              # the culprit's name gives the answer away (a run without an alias)
    dump_dir: Path
    replay_snapshot: Path | None = None
    meta: dict = field(default_factory=dict)

    # ---- model inputs --------------------------------------------------------------------------------------------
    def snapshot(self) -> dict:
        p = self.replay_snapshot or (self.dump_dir / "engine-snapshot.json")
        return json.loads(p.read_text(encoding="utf-8-sig"))

    def input_a0(self) -> str:
        return SYMPTOM_SENTENCES.get(self.truth.get("symptom") or "none", SYMPTOM_SENTENCES["none"])

    def input_a2(self) -> str:
        return ("events.json (수집기가 마킹 전 5분~후 30초 동안 모은 원시 기록):\n" + (self.dump_dir / "events.json").read_text(encoding="utf-8-sig")
                + "\n\nsnapshot.json (마킹 시점의 상태 조회):\n" + (self.dump_dir / "snapshot.json").read_text(encoding="utf-8-sig"))

    def bundle(self, with_engine: bool) -> tuple[str, set[str]]:
        return build_bundle(self.snapshot(), engine_output(self) if with_engine else None)


def engine_output(case: Case) -> dict:
    """The engine's diagnosis of this case's snapshot (crash_engine diagnose --json)."""
    snap = case.replay_snapshot or (case.dump_dir / "engine-snapshot.json")
    with tempfile.TemporaryDirectory() as td:
        out = Path(td) / "engine.json"
        r = subprocess.run([str(ENGINE), "diagnose", str(snap), "--json", str(out)], capture_output=True)
        if r.returncode != 0 or not out.exists():
            raise RuntimeError(f"engine failed on {snap}: exit {r.returncode}")
        return json.loads(out.read_text(encoding="utf-8-sig"))


def _c(obj) -> str:
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def build_bundle(s: dict, engine: dict | None) -> tuple[str, set[str]]:
    """Flatten an engine snapshot into numbered evidence items. Returns (text, set of ids)."""
    lines: list[str] = []
    ids: set[str] = set()

    def head(title: str) -> None:
        lines.append(f"\n## {title}")

    def add(i: str, obj) -> None:
        ids.add(i)
        lines.append(f"[{i}] {obj if isinstance(obj, str) else _c(obj)}")

    sym = s.get("symptom", {})
    head("사건")
    add("sym", {"marked_at": s.get("marked_at"), "trigger": s.get("trigger"), "symptom": sym.get("type"), "scope": sym.get("scope"),
                "target_process": sym.get("target_process") or None, "faulting_modules": sym.get("faulting_modules") or []})
    add("host", s.get("host", {}))

    if s.get("events"):
        head("이벤트 로그 (Windows 이벤트, 덤프 창 안)")
        for n, e in enumerate(s["events"], 1):
            add(f"ev{n}", e)

    rr = s.get("resource_recent")
    if rr:
        head("마킹 직전 구간의 자원 사용 (10초 표본, CPU 는 30초마다 측정)")
        # what the fields mean - on p1 a model read "cpu_avg 28.5, measurements 2" as weak evidence (pilot 2026-11-13)
        lines.append("읽는 법: measurements = 그 프로세스가 상위 목록에 잡힌 측정 횟수(전체 측정 횟수는 cpu_measurements). "
                     "cpu_avg·io_mbps_avg = 구간 전체 평균(잡히지 않은 측정은 0 으로 셈 — 구간 끝에 켜진 프로그램은 낮게 나온다). "
                     "cpu_avg_active·io_mbps_avg_active = 잡힌 측정만의 평균. cpu_last·io_mbps_last = 마지막 측정(마킹 무렵)의 값, null 이면 그때 상위 목록에 없었다. "
                     "busy_ge90_samples = 디스크가 90% 이상 바빴던 표본 수(표본 1개 = 10초). busy_pct_recent·latency_ms_recent·mbps_recent = 마지막 표본 6개(약 1분, 마킹 무렵)의 평균.")
        add("res", {k: v for k, v in rr.items() if not isinstance(v, list)})
        for key, prefix in (("top_cpu", "cpu"), ("top_io", "io"), ("top_memory", "mem"), ("top_memory_growth", "grow"), ("disks", "disk")):
            for n, row in enumerate(rr.get(key) or [], 1):
                add(f"{prefix}{n}", {"list": key, **row})

    rt = s.get("resource_trends")
    if rt:
        head("최근 60분 추세 (1분 표본, 프로세스가 10분 이상 있어야 행이 생긴다)")
        add("trend", {k: v for k, v in rt.items() if v not in (None, [], {})})

    if s.get("modules"):
        head("대상 프로세스에 올라와 있던 비시스템 모듈")
        for n, m in enumerate(s["modules"][:60], 1):
            add(f"mod{n}", m)

    if s.get("drivers"):
        head("표준 경로 밖 커널 드라이버·파일 시스템 필터")
        for n, d in enumerate(s["drivers"], 1):
            add(f"drv{n}", d)
    if s.get("device_filters"):
        add("filt", s["device_filters"])
    if s.get("security"):
        head("보안 제품")
        add("sec", s["security"])

    if s.get("change_history"):
        head("최근 설치·변경 (날짜만 알 수 있다)")
        for n, ch in enumerate(s["change_history"], 1):
            add(f"chg{n}", ch)

    if s.get("processes"):
        head("마킹 시점에 실행 중이던 프로세스 (이름×개수)")
        counts = Counter(s["processes"])
        add("proc", ", ".join(f"{name}×{c}" if c > 1 else name for name, c in sorted(counts.items(), key=lambda x: x[0].lower())))

    if engine is not None:
        head("규칙 엔진의 판단 (참고 자료 — 틀릴 수 있다)")
        add("englayer", engine.get("layer", {}))
        for n, c in enumerate((engine.get("candidates") or [])[:8], 1):
            ev = [e["detail"] for e in c.get("evidence", []) if e.get("kind") not in ("prior", "layer")]
            add(f"eng{n}", {"candidate": c.get("label"), "category": c.get("category"), "probability": round(c.get("probability", 0), 3), "evidence": ev})
        for n, w in enumerate(engine.get("warnings") or [], 1):
            add(f"engwarn{n}", w)
        for n, w in enumerate(engine.get("notes") or [], 1):
            add(f"engnote{n}", w)

    return "\n".join(lines).strip(), ids


def load_cases(root: str | Path, replay_dir: str | Path | None = None) -> list[Case]:
    root = Path(root)
    # snapshots rebuilt with the current collector summary (eval/replay.py) live in <root>/_replay; they are used whenever
    # they exist, so that every case is read with the same summary fields whatever collector build ran in the guest
    if replay_dir is None and (root / "_replay").is_dir():
        replay_dir = root / "_replay"
    cases: list[Case] = []
    for case_dir in sorted(p for p in root.iterdir() if p.is_dir() and not p.name.startswith("_")):
        for run in sorted(p for p in case_dir.iterdir() if p.is_dir()):
            sj, rj = run / "scenario.json", run / "result.json"
            if not sj.exists() or not rj.exists():
                continue
            scn = json.loads(sj.read_text(encoding="utf-8-sig"))
            res = json.loads(rj.read_text(encoding="utf-8-sig"))
            if not res.get("ok") or scn.get("usable") is False:
                continue
            # eval/review_cases.py --write: a run whose fault left no trace of the harm it was meant to do is not a case
            rv = json.loads((run / "review.json").read_text(encoding="utf-8-sig")) if (run / "review.json").exists() else {}
            if rv.get("effect_ok") is False or rv.get("leak_hits"):
                continue
            fam = scn["scenario"]
            want = (SCENARIOS.get(fam) or {}).get("expect_dump_kind") or "manual"
            dumps = sorted(d for d in (run / "dumps").iterdir() if d.is_dir()) if (run / "dumps").exists() else []
            # prefer_dump_kind: a real program may or may not leave a Windows crash event. When the collector wrote a
            # crash dump it is the case; otherwise the dump of the user's mark is (review_cases.py does the same).
            prefer = (SCENARIOS.get(fam) or {}).get("prefer_dump_kind")
            pick = (next((d for d in dumps if prefer and d.name.split("-", 2)[-1].startswith(prefer)), None)
                    or next((d for d in dumps if d.name.split("-", 2)[-1].startswith(want)), None))
            if pick is None:
                continue
            replay = None
            if replay_dir:
                rp = Path(replay_dir) / f"{case_dir.name}__{pick.name}.json"
                replay = rp if rp.exists() else None
            truth = scn.get("truth") or {}
            alias = scn.get("alias")
            cases.append(Case(
                id=f"{case_dir.name}/{run.name}", family=fam, clone=scn.get("clone", ""), truth=truth, alias=alias,
                # leaky = the test program ran under its own name (FaultLab). A 'recipe' run names a real program: nothing leaks.
                decoys=list(scn.get("decoys") or []), leaky=bool(truth.get("culprit")) and not alias and scn.get("kind", "fault") == "fault", dump_dir=pick, replay_snapshot=replay,
                meta={"profile": scn.get("profile"), "open": scn.get("open"), "gpu_partition": scn.get("gpu_partition"), "flags": rv.get("flags") or []},
            ))
    return cases


if __name__ == "__main__":
    import sys
    cs = load_cases(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
    for c in cs:
        text, ids = c.bundle(with_engine=False)
        print(f"{c.id:48} family={c.family:13} culprit={c.truth.get('culprit')!s:18} leaky={c.leaky!s:5} items={len(ids):3} chars={len(text):6}")
    if cs:
        print("\n--- bundle of the first case (first 40 lines) ---")
        print("\n".join(cs[0].bundle(with_engine=True)[0].splitlines()[:40]))
