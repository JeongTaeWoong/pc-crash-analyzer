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

# Version of the evidence bundle (the input of A3 and A4), written to every run's config.json. Answers made from different
# bundle versions did not get the same input and are not put into one comparison.
#   b1             the bundle up to 2026-11-14 (runs without this field in their config)
#   b2-2026-11-15  + [mrgN] triggers merged into the dump, + [exits] / [exitN] processes that ended in the dump window
#   b3-2026-11-15  a case whose event was merged into another event's dump is asked from that event's side (event_view):
#                  [sym] is the merged event, [prev] the event that opened the dump. Every other case reads as in b2.
BUNDLE_VERSION = "b3-2026-11-15"
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
    event: int = 0           # > 0: the case is the n-th trigger merged into this dump, not the trigger that opened it

    # ---- model inputs --------------------------------------------------------------------------------------------
    def snapshot(self) -> dict:
        p = self.replay_snapshot or (self.dump_dir / "engine-snapshot.json")
        s = json.loads(p.read_text(encoding="utf-8-sig"))
        return event_view(s, self.event) if self.event else s

    def input_a0(self) -> str:
        return SYMPTOM_SENTENCES.get(self.truth.get("symptom") or "none", SYMPTOM_SENTENCES["none"])

    def input_a2(self) -> str:
        return ("events.json (수집기가 마킹 전 5분~후 30초 동안 모은 원시 기록):\n" + (self.dump_dir / "events.json").read_text(encoding="utf-8-sig")
                + "\n\nsnapshot.json (마킹 시점의 상태 조회):\n" + (self.dump_dir / "snapshot.json").read_text(encoding="utf-8-sig"))

    def bundle(self, with_engine: bool) -> tuple[str, set[str]]:
        return build_bundle(self.snapshot(), engine_output(self) if with_engine else None)


def event_view(s: dict, n: int) -> dict:
    """The snapshot as seen from its n-th merged trigger (1-based).

    One dump can hold two events: the collector merges an automatic trigger that arrives within 10 s of another one into
    the first one's dump, and the snapshot's symptom is the first event's. Asked about such a dump as it is, every model
    answered about the first event and passed over the second (2026-11-15, two development cases: a GPU clone's own
    LiveKernelEvent, then a program crash 2-6 s later - Haiku 1 of 12, Sonnet 0 of 4, although the bundle listed the
    crash). The two events are two questions, so each is asked on its own: here the merged event becomes the symptom
    and the event that opened the dump is kept as 'earlier_event_in_dump'. Nothing is added that the dump does not hold.
    Only program-level events (crash, hang) get a view; anything else returns the snapshot unchanged."""
    merged = s.get("merged_triggers") or []
    if not 1 <= n <= len(merged):
        return s
    m = merged[n - 1]
    if not str(m.get("kind") or "").startswith(("crash", "hang")) or not m.get("process"):
        return s
    v = dict(s)
    old_sym, old_trig = s.get("symptom") or {}, s.get("trigger") or {}
    v["earlier_event_in_dump"] = {"trigger": old_trig, "symptom": old_sym.get("type"), "scope": old_sym.get("scope"),
                                  "target_process": old_sym.get("target_process") or None, "at": old_sym.get("started_at") or s.get("marked_at")}
    v["trigger"] = {"kind": "auto_app_crash", "event_id": m.get("event_id")}
    v["symptom"] = {"type": m.get("symptom_type") or "unknown", "target_process": m["process"], "scope": "single_app",
                    "started_at": m.get("received_at") or s.get("marked_at"),
                    "faulting_modules": [m["faulting_module"]] if m.get("faulting_module") else []}
    v["merged_triggers"] = merged[: n - 1] + merged[n:]
    if not v["merged_triggers"]:
        del v["merged_triggers"]
    px = s.get("process_exits")
    if px:                                                            # the marked program is now this event's program
        want = m["process"].lower().removesuffix(".exe")
        rows = [{**{k: x for k, x in r.items() if k != "is_target"}, **({"is_target": True} if str(r.get("name", "")).lower().removesuffix(".exe") == want else {})}
                for r in px.get("rows") or []]
        v["process_exits"] = {**px, "rows": rows}
    return v


def engine_output(case: Case) -> dict:
    """The engine's diagnosis of this case's snapshot (crash_engine diagnose --json)."""
    snap = case.replay_snapshot or (case.dump_dir / "engine-snapshot.json")
    with tempfile.TemporaryDirectory() as td:
        if case.event:                                                # the engine reads the same view the model gets
            snap = Path(td) / "view.json"
            snap.write_text(json.dumps(case.snapshot(), ensure_ascii=False), encoding="utf-8")
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
    # other automatic triggers that arrived within 10 s of the one above and were folded into this dump (collector field
    # merged_triggers, 2026-11-15): the symptom above belongs to the first trigger only
    for n, m in enumerate(s.get("merged_triggers") or [], 1):
        add(f"mrg{n}", {"merged_into_this_dump": True, **m})
    # the other way round (event_view): the event above was merged into the dump of this earlier event
    if s.get("earlier_event_in_dump"):
        add("prev", {"earlier_event_in_this_dump": True, **s["earlier_event_in_dump"]})

    if s.get("events"):
        head("이벤트 로그 (Windows 이벤트, 덤프 창 안)")
        for n, e in enumerate(s["events"], 1):
            add(f"ev{n}", e)

    # processes that ended in the dump window with a non-zero exit code, grouped by name and code (collector field
    # process_exits, 2026-11-15). A program that closed without a Windows crash event is found only here: it is no longer in
    # the process list and there is no event for it (Blender 5.1.0 in the lab: exit code 11, nothing in the event log).
    px = s.get("process_exits")
    if px:
        head("덤프 구간에서 끝난 프로세스 (종료 코드가 0 이 아닌 것, 이름·코드별 묶음)")
        lines.append("읽는 법: window_stops = 구간에서 끝난 프로세스 전체 수, nonzero_stops = 그중 종료 코드가 0 이 아닌 수. "
                     "행 하나 = 같은 이름·같은 코드의 종료 묶음(count = 횟수, last_at = 마지막 종료가 기록된 시각, "
                     "sec_before_mark = 마킹 몇 초 전인지 — 기록이 1초쯤 늦게 도착하므로 0 근처 음수는 마킹 직전을 뜻한다, "
                     "lived_sec = 마지막으로 끝난 것이 켜져 있던 시간, 없으면 구간 이전에 켜졌다). is_target = 사용자가 마킹할 때 지목한 프로그램. "
                     "0 이 아닌 코드가 곧 오류는 아니다: 평소에도 0 이 아닌 코드로 끝나는 프로그램이 있다.")
        add("exits", {k: v for k, v in px.items() if not isinstance(v, list)})
        for n, row in enumerate(px.get("rows") or [], 1):
            add(f"exit{n}", row)

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


def test_session_windows(root: Path) -> list[tuple[str, str]]:
    """Run-folder time ranges (first, last) of the sessions that ran a TEST-SET plan (lab/vm/plans/test-*.json), read from
    <vm root>/sessions/<start>/session.log. A session that has not ended yet is open-ended.

    Why: case.ps1 writes every run to <vm root>/cases; test-set runs sit there until eval/split_test.py moves them. On
    2026-11-15 a review of that day's development runs, started while the test session was running, ran the engine on
    seven test-set runs (the files were deleted unread). Development tools must not see such runs at all."""
    out = []
    sessions = root.parent / "sessions"
    if not sessions.is_dir():
        return out
    for s in sorted(p for p in sessions.iterdir() if p.is_dir()):
        log = s / "session.log"
        if not log.exists():
            continue
        lines = log.read_text(encoding="utf-8-sig", errors="replace").splitlines()
        first = next((ln for ln in lines if "session start, plan" in ln), "")
        plan = first.split("plan", 1)[1].split(",")[0].strip().replace("/", "\\").split("\\")[-1].lower() if first else ""
        if not plan.startswith("test-"):
            continue
        end = "99999999"
        last = next((ln for ln in reversed(lines) if "session end" in ln), None)
        if last:
            hhmmss = last[:8].replace(":", "")
            day = s.name[:8]
            if hhmmss < s.name[9:]:                                   # ended after midnight
                import datetime
                day = (datetime.datetime.strptime(day, "%Y%m%d") + datetime.timedelta(days=1)).strftime("%Y%m%d")
            end = f"{day}-{hhmmss}"
        out.append((s.name, end))
    return out


def is_test_run(root: Path, run_name: str, windows: list[tuple[str, str]] | None = None) -> bool:
    """A run made during a test-set session that still sits in the development folder."""
    if root.name.lower() == "cases-test":
        return False
    return any(a <= run_name <= b for a, b in (windows if windows is not None else test_session_windows(root)))


def load_cases(root: str | Path, replay_dir: str | Path | None = None) -> list[Case]:
    root = Path(root)
    windows = test_session_windows(root)
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
            if is_test_run(root, run.name, windows):
                continue                                              # a test-set run not yet moved to cases-test
            scn = json.loads(sj.read_text(encoding="utf-8-sig"))
            res = json.loads(rj.read_text(encoding="utf-8-sig"))
            # eval/review_cases.py --write: a run whose fault left no trace of the harm it was meant to do is not a case
            rv = json.loads((run / "review.json").read_text(encoding="utf-8-sig")) if (run / "review.json").exists() else {}
            # override_ok: the guest called the run failed only because the crash got no dump of its own - it was merged
            # into the dump of another event a few seconds earlier. The review found that dump; it is the case.
            if (not res.get("ok") or scn.get("usable") is False) and not rv.get("override_ok"):
                continue
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
            if rv.get("override_ok") and rv.get("dump"):
                pick = next((d for d in dumps if d.name == rv["dump"]), None)
            if pick is None:
                continue
            replay = None
            if replay_dir:
                rp = Path(replay_dir) / f"{case_dir.name}__{pick.name}.json"
                replay = rp if rp.exists() else None
            truth = scn.get("truth") or {}
            alias = scn.get("alias")
            # A case whose event was merged into another event's dump is asked from that event's side. Which merged
            # trigger: the first of the kind the scenario was meant to produce (crash / hang) - the scenario's kind, not
            # the culprit's name, decides, so nothing of the answer key goes into the input.
            event = 0
            if rv.get("override_ok"):
                src = replay or (pick / "engine-snapshot.json")
                kinds = [str(m.get("kind") or "") for m in (json.loads(src.read_text(encoding="utf-8-sig")).get("merged_triggers") or [])]
                event = next((i for i, k in enumerate(kinds, 1) if k.startswith(want)), 0)
            cases.append(Case(
                id=f"{case_dir.name}/{run.name}", family=fam, clone=scn.get("clone", ""), truth=truth, alias=alias,
                # leaky = the test program ran under its own name (FaultLab). A 'recipe' run names a real program: nothing leaks.
                decoys=list(scn.get("decoys") or []), leaky=bool(truth.get("culprit")) and not alias and scn.get("kind", "fault") == "fault", dump_dir=pick, replay_snapshot=replay,
                meta={"profile": scn.get("profile"), "open": scn.get("open"), "gpu_partition": scn.get("gpu_partition"), "flags": rv.get("flags") or []},
                event=event,
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
