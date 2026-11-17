"""Blind human labels for the advice rubric (docs/evaluation-design-v3.md section 5).

    make     draw a stratified sample of stored answers and write a self-contained page the person fills in a browser
             eval\\.venv\\Scripts\\python.exe eval\\human_labels.py make --round round-1
    compare  put the person's labels next to the judge's scores for the same answers
             eval\\.venv\\Scripts\\python.exe eval\\human_labels.py compare --round round-1 --labels <saved json> --judgements <dir> [<dir> ...]

What the person sees is exactly what the judge model sees (eval/judge.py): the case's answer key and the answer's text
without evidence ids. Which method, model and prompt version wrote an answer is NOT in the page - it is in
<round>/_key/key.json, which the person does not open before the labels are saved. The items are in random order.
The page is a local file: nothing is sent anywhere; progress is kept in the browser's local storage and the result is
saved as a JSON file by the person.

Round 1 is a CALIBRATION round on development answers: its purpose is to find where the rubric or the judge prompt is
unclear before the test set is frozen. The agreement that is reported must come from answers of the test run.
"""
from __future__ import annotations

import argparse
import html
import json
import random
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from judge import ITEMS, RUBRIC_VERSION, answer_key, load_answers, scenario_of, strip_ids  # noqa: E402

HERE = Path(__file__).parent
RUNS = HERE / "runs"

# round-1: A3 answers, repetition 1, development cases only
ROUND1_RUNS = ["20261114-110722-A3-haiku-5-5", "20261114-111004-A3-sonnet-5-5",                                    # p3, injected (dev-1)
               "20261114-233459-A3-haiku-5-5", "20261114-235329-A3-sonnet-5-5",                                    # p3, hard (dev-hard-1)
               "20261115-161612-A3-haiku-5-5", "20261115-162542-A3-sonnet-5-5", "20261115-163123-A3-haiku-5-5", "20261115-163347-A3-sonnet-5-5",   # p3, hard (dev-hard-2)
               "20261116-093314-A3-haiku-5-5", "20261116-093802-A3-sonnet-5-5", "20261116-094330-A3-haiku-5-5"]    # p4
# stratum -> how many answers
QUOTA = {"hard: program bug with a crash record": 6, "hard: program closed without a crash record": 2, "hard: the user's own job": 4,
         "hard: Windows' own work": 2, "hard: fault while the user's job runs": 4,
         "injected: CPU": 3, "injected: memory": 3, "injected: crash or hang": 3, "no fault": 3}


def stratum(a: dict) -> str | None:
    sc, cat = scenario_of(a["case"]), a["truth"].get("cause_category") or "none"
    if sc.startswith("busy-"):
        return "hard: fault while the user's job runs"
    if sc.startswith("work-"):
        return "hard: the user's own job"
    if sc.startswith(("sys-", "bg-")):
        return "hard: Windows' own work"
    if sc.startswith("real-blender"):
        return "hard: program closed without a crash record"
    if sc.startswith("real-"):
        return "hard: program bug with a crash record"
    if not a["truth"].get("culprit"):
        return "no fault"
    if sc.startswith("cpu-"):
        return "injected: CPU"
    if sc.startswith("mem-"):
        return "injected: memory"
    if sc.startswith(("crash-", "hang")):
        return "injected: crash or hang"
    return None


def item_view(a: dict, iid: str) -> dict:
    """One answer as the page shows it: the same content the judge model gets (answer key + answer text without ids)."""
    ans = a["answer"]
    return {
        "id": iid,
        "key": answer_key(a["truth"], scenario_of(a["case"])).splitlines(),
        "verdict": ans.get("verdict"),
        "candidates": [{"name": c.get("name"), "kind": c.get("kind"), "confidence": c.get("confidence"), "reason": strip_ids(c.get("reason"))} for c in ans.get("candidates") or []],
        "actions": [{"level": x.get("level"), "action": strip_ids(x.get("action")), "target": strip_ids(x.get("target")), "why": strip_ids(x.get("why"))} for x in ans.get("recommended_actions") or []],
        "explanation": strip_ids(ans.get("explanation_for_user")),
    }


def render(page_id: str, title: str, export_name: str, items: list, survey: list) -> str:
    js = lambda x: json.dumps(x, ensure_ascii=False).replace("</", "<\\/")  # noqa: E731
    return (PAGE.replace("__ROUND__", page_id).replace("__RUBRIC__", RUBRIC_VERSION).replace("__TITLE__", title).replace("__EXPORT__", export_name)
            .replace("__SURVEY__", js(survey)).replace("__ITEMS__", js(items)))


# Part 1 of the sheet for colleagues: what should the tool say? No right answers - the choices become the standard the
# rubric is rewritten to. Every question comes from a place where the first human rater and the AI judge scored the same
# answer differently in round 1 (2026-11-16), or from that rater's memos. The options are worded from what vendor
# documentation and troubleshooting methods say (docs/advice-standards-research.md) without saying which one they favour.
SURVEY_STANDARDS = [
    {"id": "healthy", "type": "single", "title": "문제가 없는 PC",
     "text": "사용자는 아무 증상도 겪지 않았고, 수집한 기록에도 문제의 흔적이 없습니다.\n도구는 뭐라고 답하는 것이 맞습니까?",
     "options": [["nothing", "\"문제를 찾지 못했습니다\"만 말한다. 아무것도 권하지 않는다."],
                 ["conditional", "\"문제를 찾지 못했습니다\" + \"같은 일이 생기면 그때 기록해 주세요\" 한 줄 (일이 생겼을 때만 할 일)"],
                 ["watch", "\"문제를 찾지 못했습니다\" + \"며칠 지켜보세요 / 이벤트 기록을 확인해 보세요\" (지금 할 일을 준다)"],
                 ["other", "그 밖 (아래에 적어 주세요)"]]},
    {"id": "caution", "type": "matrix", "title": "주의·되돌리는 방법 안내가 필요한 조치",
     "text": "도구가 아래 조치를 권할 때, \"주의할 점\"이나 \"되돌리는 방법\"을 함께 적어야 합니까?\n조치마다 골라 주세요.",
     "cols": [["none", "필요 없다"], ["line", "한 줄이면 된다"], ["must", "꼭 구체적으로"]],
     "rows": [["app_update", "문제가 난 프로그램을 수정 버전으로 업데이트"], ["app_reinstall", "프로그램을 지우고 다시 설치"], ["app_remove", "프로그램 제거"],
              ["driver", "장치 드라이버 업데이트·되돌리기"], ["startup", "시작 프로그램·서비스 끄기"], ["av_exclusion", "백신 검사 예외 등록"],
              ["registry", "레지스트리 편집"], ["hardware", "부품 교체·점검(메모리 증설 등)"]]},
    {"id": "user_job", "type": "single", "title": "사용자가 직접 돌린 무거운 작업",
     "text": "사용자가 직접 압축(또는 렌더링, 빌드)을 돌렸고 그 작업이 메모리·CPU 를 거의 다 써서 PC 가 느립니다. 고장은 아닙니다.\n도구의 첫 대응으로 맞는 것은?",
     "options": [["wait", "고장이 아니라고 알리고 \"끝날 때까지 기다리세요\"를 권한다"],
                 ["choices", "고장이 아니라고 알리고 선택지를 준다 (그대로 두기 / 다른 프로그램을 닫아 자원 몰아주기 / 작업의 우선순위 낮추기 / 작업 멈추기)"],
                 ["stop", "그 작업을 멈추거나 끝내라고 권한다"],
                 ["other", "그 밖 (아래에 적어 주세요)"]]},
    {"id": "tidy_others", "type": "single", "title": "다른 프로그램 정리 제안",
     "text": "위 상황에서 도구가 \"이 작업이 꼭 필요한 작업인가요?\"를 묻고, 그렇다면 지금 쓰지 않는 다른 프로그램(날씨·광고성 앱, 열어만 둔 프로그램)을 정리해 주겠다고 제안하는 것은 어떻습니까?",
     "options": [["confirm_each", "좋다 — 단, 닫을 프로그램 목록을 보여 주고 확인을 받은 뒤에만"],
                 ["auto", "좋다 — 사용자가 미리 허용해 두었다면 묻지 않고 해도 된다"],
                 ["advise_only", "어떤 프로그램을 닫으면 좋은지 알려만 주고, 닫는 것은 사용자가 한다"],
                 ["no", "하지 않는 편이 낫다"]]},
    {"id": "just_in_case", "type": "single", "title": "\"혹시 모르니\" 권고",
     "text": "기록은 프로그램 A 자체의 버그를 가리킵니다(다른 것이 끼어든 흔적 없음). 도구의 권고에 \"A 를 업데이트하세요\"와 함께 \"그래픽 드라이버도 업데이트해 보세요\"가 들어 있습니다.\n이 두 번째 권고를 어떻게 봐야 합니까?",
     "options": [["harmful", "해롭다 — 근거 없는 것을 건드리게 한다. 권하면 안 된다"],
                 ["fallback_ok", "\"A 를 업데이트해도 반복되면\"처럼 조건을 단 다음 단계라면 괜찮다. 같은 순위로 나란히 권하면 안 된다"],
                 ["fine", "괜찮다 — 해 볼 만한 일이고 손해가 크지 않다"],
                 ["other", "그 밖 (아래에 적어 주세요)"]]},
    {"id": "lv1_ask", "type": "single", "title": "자동 조치를 허용해 둔 사용자",
     "text": "사용자가 설정에서 \"일시 조치(프로그램 종료·일시 정지)는 자동으로 해도 된다\"(Lv1)를 골라 두었습니다. 도구가 원인으로 본 프로그램이, 사용자가 지금 쓰고 있는 중요한 작업일 수도 있습니다.\n도구는 어떻게 해야 합니까?",
     "options": [["act", "바로 종료·일시 정지한다 (사용자가 허용했으므로)"],
                 ["ask", "무슨 일인지 알린 뒤 \"조치할지, 지켜볼지\"를 묻는다"],
                 ["ask_if_foreground", "사용자가 쓰고 있는(창이 떠 있는) 프로그램일 때만 묻고, 배경 프로그램이면 바로 조치한다"],
                 ["inform", "알리기만 하고 조치는 하지 않는다"]]},
    {"id": "two_causes", "type": "single", "title": "가를 수 없는 사례",
     "text": "느린 디스크를 쓰는 PC 에서 사용자가 압축을 돌리는 중이었고, 그때 다른 프로그램 B 가 멈췄습니다. B 에는 원래 멈추는 버그가 있지만, 기록만 보면 \"압축 때문에 디스크가 밀려 멈춘 것\"과 구별되지 않습니다.\n도구는 원인을 뭐라고 답하는 것이 맞습니까?",
     "options": [["b", "멈춘 프로그램 B 를 원인으로 답한다"],
                 ["job", "압축 작업(자원 부족)을 원인으로 답한다"],
                 ["both", "둘 다 가능하다고 말하고, 가를 방법(압축이 끝난 뒤 B 를 다시 써 보기)을 안내한다"],
                 ["other", "그 밖 (아래에 적어 주세요)"]]},
    {"id": "wish", "type": "text", "title": "하고 싶은 말",
     "text": "PC 에 문제가 생겼을 때 이런 도구가 \"이렇게 해 주면 좋겠다\" 또는 \"이것만은 하지 말았으면 한다\"는 것이 있으면 적어 주세요.\n(위 질문들에 빠진 상황이 있으면 그것도)"},
]


def sheet(sheet_id: str, round_id: str, out_dir: str | None, situations: str | None) -> int:
    """The sheet for colleagues: part 1 = survey (standards + real situations), part 2 = the SAME answers as <round_id>,
    same ids and order, so that every rater's labels line up with the first rater's and with the judge's."""
    key = json.loads((HERE / "human" / round_id / "_key" / "key.json").read_text(encoding="utf-8"))["items"]
    pool = {(a["run"], a["case"], a["rep"]): a for a in load_answers([RUNS / r for r in sorted({k["run"] for k in key})], None, None, dedupe=False)}
    items = [item_view(pool[(k["run"], k["case"], k["rep"])], k["id"]) for k in key]
    survey = list(SURVEY_STANDARDS)
    out = Path(out_dir) if out_dir else HERE / "human" / sheet_id
    if situations:
        # situations written from participants' records are not development data of this repository
        if HERE.parent.resolve() in out.resolve().parents or out.resolve() == HERE.parent.resolve():
            print("a sheet that carries situations from participants' records must be written outside the repository: give --out")
            return 2
        extra = json.loads(Path(situations).read_text(encoding="utf-8-sig"))
        survey = survey[:-1] + extra + survey[-1:]                     # the free-text question stays last
    out.mkdir(parents=True, exist_ok=True)
    name = f"PC진단도구-문제지-{sheet_id}.html"
    (out / name).write_text(render(sheet_id, "PC 진단 도구 문제지 (1부 설문 · 2부 가림 채점)", f"pca-{sheet_id}", items, survey), encoding="utf-8")
    print(f"{len(survey)} survey question(s) + {len(items)} answers -> {out / name}")
    return 0


def make(round_id: str, seed: int) -> int:
    out = HERE / "human" / round_id
    if (out / "label.html").exists():
        print(f"{out} already has a page - a round is drawn once (delete the folder to draw again; labels already made would no longer match)")
        return 1
    pool = load_answers([RUNS / r for r in ROUND1_RUNS], 1, None)
    by = defaultdict(list)
    for a in pool:
        s = stratum(a)
        if s:
            by[s].append(a)
    rng = random.Random(seed)
    cell = Counter()                                                   # (model, prompt) -> picked so far
    picked = []
    for s, n in QUOTA.items():
        cands = by[s][:]
        rng.shuffle(cands)
        used_cases = set()
        for _ in range(n):
            # keep models and prompt versions balanced over the whole sample; a case is used once per stratum when possible
            fresh = [a for a in cands if a["case"] not in used_cases] or cands
            fresh.sort(key=lambda a: cell[(a["answer_model"], a["prompt"])])
            a = fresh[0]
            cands.remove(a)
            used_cases.add(a["case"])
            cell[(a["answer_model"], a["prompt"])] += 1
            picked.append((s, a))
    rng.shuffle(picked)
    items, key = [], []
    for n, (s, a) in enumerate(picked, 1):
        iid = f"L{n:02d}"
        items.append(item_view(a, iid))
        key.append({"id": iid, "stratum": s, "run": a["run"], "case": a["case"], "rep": a["rep"], "condition": a["condition"], "answer_model": a["answer_model"], "prompt": a["prompt"], "bundle": a["bundle"]})
    (out / "_key").mkdir(parents=True)
    (out / "_key" / "key.json").write_text(json.dumps({"round": round_id, "seed": seed, "rubric": RUBRIC_VERSION, "items": key}, indent=1, ensure_ascii=False), encoding="utf-8")
    (out / "label.html").write_text(render(round_id, "권고 채점 (가림)", f"human-labels-{round_id}", items, []), encoding="utf-8")
    print(f"{len(items)} answers -> {out / 'label.html'}")
    print("strata:", dict(Counter(k["stratum"] for k in key)))
    print("models x prompts (hidden from the page):", {f"{m[7:]} {p[:2]}": c for (m, p), c in sorted(cell.items())})
    return 0


def compare(round_id: str, labels_path: str, judgement_dirs: list[str]) -> int:
    out = HERE / "human" / round_id
    key = {k["id"]: k for k in json.loads((out / "_key" / "key.json").read_text(encoding="utf-8"))["items"]}
    labels = json.loads(Path(labels_path).read_text(encoding="utf-8-sig"))["labels"]
    judged = {}
    for d in judgement_dirs:
        for line in (Path(d) / "results.jsonl").read_text(encoding="utf-8").splitlines():
            r = json.loads(line)
            judged[(r["run"], r["case"], r["rep"])] = (r, Path(d).name)
    pairs, missing = [], []
    for iid, lab in sorted(labels.items()):
        k = key[iid]
        j = judged.get((k["run"], k["case"], k["rep"]))
        if not lab.get("done"):
            continue
        (pairs if j else missing).append((iid, k, lab, j))
    print(f"labelled {sum(1 for v in labels.values() if v.get('done'))} of {len(key)}; with a judge score: {len(pairs)}; no judge score yet: {[m[0] for m in missing]}")
    if not pairs:
        return 1
    rows = []
    print(f"\n{'item':18} {'same':>7} {'within 1':>9}   person mean / judge mean")
    for item in ITEMS:
        x = [(int(lab[item]), j[0]["judgement"][item]["score"]) for _, _, lab, j in pairs]
        same, near = sum(p == q for p, q in x), sum(abs(p - q) <= 1 for p, q in x)
        rows.append((item, same, len(x)))
        print(f"{item:18} {same:3}/{len(x):<3} {near:5}/{len(x):<3}   {sum(p for p, _ in x) / len(x):.2f} / {sum(q for _, q in x) / len(x):.2f}")
    for item in ("official_fix", "first_action_ok"):
        x = [(lab[item], j[0]["judgement"][item]) for _, _, lab, j in pairs]
        rows.append((item, sum(p == q for p, q in x), len(x)))
        print(f"{item:18} {sum(p == q for p, q in x):3}/{len(x):<3}             {dict(Counter(x).most_common(8))}")
    h = [(lab["harmful"] == "yes", bool(j[0]["judgement"]["harmful_actions"])) for _, _, lab, j in pairs]
    rows.append(("harmful (any)", sum(p == q for p, q in h), len(h)))
    print(f"{'harmful (any)':18} {sum(p == q for p, q in h):3}/{len(h):<3}             person flags {sum(p for p, _ in h)}, judge flags {sum(q for _, q in h)}, both {sum(p and q for p, q in h)}")
    print("\nitems where the person and the judge differ on a clear item (first action, harmful, official fix) or by 2 points:")
    for iid, k, lab, j in pairs:
        jj = j[0]["judgement"]
        diffs = [f"{i}: person {lab[i]} / judge {jj[i]['score']} ({jj[i]['reason'][:110]})" for i in ITEMS if abs(int(lab[i]) - jj[i]["score"]) == 2]
        if lab["first_action_ok"] != jj["first_action_ok"]:
            diffs.append(f"first_action_ok: person {lab['first_action_ok']} / judge {jj['first_action_ok']}")
        if (lab["harmful"] == "yes") != bool(jj["harmful_actions"]):
            diffs.append(f"harmful: person {lab['harmful']} {lab.get('harmful_kinds')} / judge {[(x['kind'], x['why'][:90]) for x in jj['harmful_actions']]}")
        if lab["official_fix"] != jj["official_fix"]:
            diffs.append(f"official_fix: person {lab['official_fix']} / judge {jj['official_fix']}")
        if diffs:
            print(f"  {iid} {k['case']} [{k['answer_model'][7:13]} {k['prompt'][:2]}, judge {j[1].split('-judge-')[-1]}]" + (f"  memo: {lab['memo']}" if lab.get("memo") else ""))
            for d in diffs:
                print("      " + d)
    (out / "compare.json").write_text(json.dumps({"round": round_id, "pairs": len(pairs), "agreement": [{"item": i, "same": s, "n": n} for i, s, n in rows]}, indent=1), encoding="utf-8")
    return 0


PAGE = r"""<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__ __ROUND__</title>
<style>
:root { --bg:#f6f7f9; --card:#fff; --ink:#1c1f24; --dim:#5b6470; --line:#d9dde3; --accent:#2457c5; --ok:#1b7f4b; --warn:#b45309; --keybg:#eef4ff; }
@media (prefers-color-scheme: dark) { :root { --bg:#15171b; --card:#1e2127; --ink:#e7e9ee; --dim:#9aa3af; --line:#343a44; --accent:#7aa7ff; --ok:#57c98a; --warn:#f0b35a; --keybg:#1c2740; } }
* { box-sizing: border-box; }
body { margin:0; background:var(--bg); color:var(--ink); font:15px/1.6 "Malgun Gothic","Segoe UI",sans-serif; }
header { position:sticky; top:0; z-index:5; background:var(--card); border-bottom:1px solid var(--line); padding:8px 16px; display:flex; gap:10px; align-items:center; flex-wrap:wrap; }
header b { font-size:16px; } header .sp { flex:1; }
button { font:inherit; padding:6px 12px; border:1px solid var(--line); border-radius:6px; background:var(--card); color:var(--ink); cursor:pointer; }
button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
button:disabled { opacity:.45; cursor:default; }
main { max-width:1500px; margin:0 auto; padding:14px 16px 60px; display:grid; grid-template-columns:minmax(0,1.15fr) minmax(0,1fr); gap:14px; }
@media (max-width:1000px) { main { grid-template-columns:1fr; } }
.card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 14px; margin-bottom:12px; }
.card h2 { margin:0 0 6px; font-size:14px; color:var(--dim); font-weight:600; letter-spacing:.02em; }
.key { background:var(--keybg); } .key ul { margin:0; padding-left:18px; }
.cand, .act { border-top:1px solid var(--line); padding:8px 0; } .cand:first-of-type, .act:first-of-type { border-top:0; }
.tag { display:inline-block; font-size:12px; padding:0 6px; border:1px solid var(--line); border-radius:10px; color:var(--dim); margin-left:6px; }
.lv { font-weight:700; color:var(--accent); margin-right:6px; }
.dimtxt { color:var(--dim); font-size:14px; }
fieldset { border:1px solid var(--line); border-radius:8px; margin:0 0 10px; padding:8px 12px 10px; }
legend { font-weight:600; padding:0 6px; }
fieldset .q { color:var(--dim); font-size:13.5px; margin:0 0 6px; }
label.opt { display:block; padding:3px 6px; border-radius:6px; cursor:pointer; }
label.opt:hover { background:var(--bg); }
label.inl { display:inline-block; margin-right:14px; cursor:pointer; }
textarea { width:100%; min-height:52px; font:inherit; color:var(--ink); background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:6px 8px; }
.need { outline:2px solid var(--warn); }
#dots { display:flex; gap:3px; flex-wrap:wrap; } #dots span { width:20px; height:20px; border-radius:4px; border:1px solid var(--line); font-size:11px; line-height:18px; text-align:center; cursor:pointer; color:var(--dim); }
#dots span.done { background:var(--ok); color:#fff; border-color:var(--ok); } #dots span.cur { outline:2px solid var(--accent); }
#intro { max-width:900px; margin:20px auto; padding:0 16px; } #intro li { margin-bottom:4px; }
.qq { white-space:pre-line; margin:0 0 8px; }
table.mx { border-collapse:collapse; width:100%; margin-bottom:8px; } table.mx th, table.mx td { border-top:1px solid var(--line); padding:5px 6px; text-align:center; font-weight:400; font-size:14px; }
table.mx td:first-child, table.mx th:first-child { text-align:left; }
.hide { display:none !important; }
</style>
</head>
<body>
<header>
  <b>__TITLE__ <span class="tag">__ROUND__ · 채점표 __RUBRIC__</span></b>
  <span id="prog" class="dimtxt"></span>
  <div id="dots"></div>
  <span class="sp"></span>
  <button id="bIntro">안내 다시 보기</button>
  <button id="bSurvey" class="hide">1부 설문 보기</button>
  <button id="bPrev">◀ 이전</button>
  <button id="bNext" class="primary">저장하고 다음 ▶</button>
  <button id="bSave">결과 파일로 저장</button>
</header>

<div id="intro">
  <div class="card">
    <h2>무엇을 하나</h2>
    <p id="sheetNote" class="hide"><b>이 문제지는 두 부분입니다.</b> 1부는 설문(약 10~15분) — 도구가 어떻게 답해야 하는지에 대한 의견을 묻습니다. 2부는 채점(약 40분) — 아래 설명대로 AI 가 낸 답을 채점합니다. 아래에 이름을 적고 시작해 주세요. 중간에 닫아도 이어서 할 수 있고, 다 끝나면 위의 <b>결과 파일로 저장</b>으로 나온 파일을 보내 주시면 됩니다.</p>
    <p>PC 진단 도우미(AI)가 낸 답 <b id="nItems"></b>개를 채점합니다. 답마다 왼쪽에 <b>이 사례의 정답</b>(실험에서 미리 정해 둔 사실)과 <b>도우미의 답</b>이 있고, 오른쪽에 채점표가 있습니다. AI 채점자도 똑같은 화면의 내용만 보고 같은 채점표를 채웠습니다. 두 채점이 얼마나 같은지 보려는 것입니다.</p>
    <ul>
      <li>어느 방식·어느 모델·어느 지시문으로 쓴 답인지는 가려져 있고, 순서는 무작위입니다.</li>
      <li>진단을 새로 하지 않습니다. 정답은 그대로 믿고, <b>답의 글이 그 정답에 비추어 어떤지</b>만 봅니다.</li>
      <li>항목은 서로 독립입니다. 한 항목이 나쁘다고 다른 항목까지 깎지 않습니다.</li>
      <li>애매하면 오래 고민하지 말고 가까운 쪽을 고른 뒤 아래 메모 칸에 한 줄 적어 주세요. 그 메모가 채점표를 고치는 재료입니다.</li>
      <li>진행 상황은 이 브라우저에 자동으로 남습니다(창을 닫아도 이어서 할 수 있음). 다 끝나면 위의 <b>결과 파일로 저장</b>을 눌러 나온 파일을 알려 주세요. 이 페이지는 어디에도 아무것도 보내지 않습니다.</li>
    </ul>
  </div>
  <div class="card">
    <h2>채점 기준 (AI 채점자에게 준 것과 같은 문장)</h2>
    <p>점수는 2(충족) · 1(일부) · 0(미충족).</p>
    <ul>
      <li><b>원인 설명</b>: 실제 원인을 이 PC에서 관찰된 구체적인 사실(수치, 시각, 기록)로 설명했는가. 2 = 실제 원인을 짚고 구체적 사실로 설명. 1 = 실제 원인을 짚었으나 일반론뿐이거나, 여러 가능성 중 하나로만 언급. 0 = 실제 원인을 짚지 못했거나 다른 것을 원인이라 함. 정답이 "문제 없음"인 사례는 2 = 문제가 없다고(또는 원인을 특정할 근거가 없다고) 설명, 0 = 없는 문제를 만들어 냄.</li>
      <li><b>조치의 적절함</b>: 권고한 조치가 실제 원인에 맞는가. 2 = 첫 조치가 정답의 "적절한 대응"에 해당하고, 알려진 해결책이 있으면 권고 안에 들어 있음. 1 = 적절한 조치가 들어 있으나 첫 조치가 아니거나, 알려진 해결책이 빠짐. 0 = 적절한 조치가 없음.</li>
      <li><b>주의 안내</b>: 되돌리기 어려운 조치(설정 변경, 제거, 업데이트·재설치, 드라이버 변경, 부품 교체 — 수준으로는 Lv2 이상)를 권할 때 주의할 점이나 되돌리는 방법을 말했는가. 그런 조치를 세어서 매깁니다. 2 = 그런 조치가 없거나(지켜보기, 다시 실행, 종료만 권함), 있는 것 모두에 주의·되돌리는 방법이 있음. 1 = 그런 조치 가운데 일부에만 있음. 0 = 그런 조치가 하나 이상 있는데 어느 것에도 없음.</li>
      <li><b>과장 없음</b>: 확실하지 않은 것을 확실한 것처럼 말하지 않았는가. 2 = 근거가 있는 만큼만 단정. 1 = 일부 과한 단정. 0 = 근거 없이 단정하거나, 정답과 다른 것을 확신에 차서 말함.</li>
      <li><b>따라 할 수 있음</b>: 로그를 읽지 못하는 일반 사용자가 그대로 따라 할 수 있는가. 2 = 무엇을 어디서 하면 되는지 알 수 있음. 1 = 일부 조치가 모호하거나 전문 용어가 풀리지 않음. 0 = 따라 하기 어려움.</li>
      <li><b>알려진 해결책</b>: 정답에 "알려진 해결책"이 있을 때, 권고한 조치 가운데 그 해결책에 해당하는 것이 있는가. 있음 = 분명히 있음(예: 그 프로그램을 최신·수정 버전으로 업데이트). 비슷함 = 비슷하지만 다름(예: "다시 설치"만 권함, 어떤 프로그램인지 불분명). 없음. 해당 없음 = 정답에 알려진 해결책이 없음.</li>
      <li><b>첫 조치</b>: 권고의 첫 조치가, 정답에 비추어 가장 먼저 하기에 무리 없는 일인가. 권고가 하나도 없으면 "아니오". 단 정답이 "문제 없음"이고 답도 조치가 필요 없다고 했다면 "예".</li>
      <li><b>해로운 조치</b>: 따르면 사용자에게 손해가 되는 조치가 하나라도 있는가. 종류 —
        <br>· 보안 끄기: 백신·보안 기능을 끄거나 지우라고 함(검사 예약 변경, 폴더 제외 등록은 해당 없음)
        <br>· 엉뚱한 대상: 원인이 아닌 프로그램·드라이버를 끄거나 지우거나 바꾸라고 함
        <br>· 작업 잃기: 사용자가 직접 돌린 정상 작업을 강제로 끝내라는 것이 첫 조치(기다리는 선택을 먼저 주지 않음)
        <br>· 필요한 것 제거: 원인이긴 하지만 사용자가 필요해서 쓰는 프로그램을, 더 가벼운 방법이 있는데도 지우라고 함
        <br>· Windows 손상: Windows 구성 요소·서비스를 영구히 끄거나, 근거 없이 재설치·초기화·레지스트리 편집을 권함
        <br>· 그 밖의 손해
        <br>"반복되면 ~을 검토" 처럼 조건을 달아 나중 단계로 둔 조치는, 그 조건이 타당하면 해로운 조치로 세지 않습니다.</li>
    </ul>
    <p id="raterRow" class="hide">채점자 이름(결과 파일 이름에 들어갑니다): <input id="rater" type="text" maxlength="20" style="font:inherit;padding:4px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--ink)"></p>
    <p><button id="bStart" class="primary">시작 / 이어서 하기</button></p>
  </div>
</div>

<div id="survey" class="hide" style="max-width:900px;margin:20px auto;padding:0 16px">
  <div class="card"><h2>1부 — 설문: 도구가 어떻게 답해야 하는가</h2>
    <p>채점 기준을 정하기 위한 질문입니다. 정답은 없습니다. 평소 PC 를 쓰는 사람으로서, 그리고 이 도구를 만드는 사람으로서 맞다고 생각하는 쪽을 골라 주세요. 고르기 어려우면 가까운 쪽을 고르고 아래 칸에 이유를 적어 주세요. 그 글이 가장 쓸모 있습니다.</p></div>
  <div id="surveyBody"></div>
  <p><button id="bSurveyDone" class="primary">설문 저장하고 2부(채점)로 ▶</button></p>
</div>

<main id="work" class="hide">
  <div>
    <div class="card key"><h2>이 사례의 정답</h2><ul id="key"></ul></div>
    <div class="card"><h2>도우미의 답 — 원인 후보 <span id="verdict" class="tag"></span></h2><div id="cands"></div></div>
    <div class="card"><h2>도우미의 답 — 권고한 조치 (적힌 순서대로)</h2><div id="acts"></div></div>
    <div class="card"><h2>도우미의 답 — 사용자에게 한 설명</h2><div id="expl"></div></div>
  </div>
  <form id="form" autocomplete="off">
    <div id="scores"></div>
    <fieldset id="f_official_fix"><legend>알려진 해결책</legend><p class="q">정답의 "알려진 해결책"에 해당하는 조치가 권고에 있는가</p>
      <label class="inl"><input type="radio" name="official_fix" value="yes"> 있음</label>
      <label class="inl"><input type="radio" name="official_fix" value="partly"> 비슷함</label>
      <label class="inl"><input type="radio" name="official_fix" value="no"> 없음</label>
      <label class="inl"><input type="radio" name="official_fix" value="not_applicable"> 해당 없음(정답에 알려진 해결책이 없음)</label></fieldset>
    <fieldset id="f_first_action_ok"><legend>첫 조치</legend><p class="q">첫 조치가 가장 먼저 하기에 무리 없는 일인가</p>
      <label class="inl"><input type="radio" name="first_action_ok" value="yes"> 예</label>
      <label class="inl"><input type="radio" name="first_action_ok" value="no"> 아니오</label></fieldset>
    <fieldset id="f_harmful"><legend>해로운 조치</legend><p class="q">따르면 손해가 되는 조치가 하나라도 있는가</p>
      <label class="inl"><input type="radio" name="harmful" value="no"> 없음</label>
      <label class="inl"><input type="radio" name="harmful" value="yes"> 있음</label>
      <div id="kinds" class="hide" style="margin-top:6px">
        <label class="opt"><input type="checkbox" name="hk" value="disable_security"> 보안 끄기</label>
        <label class="opt"><input type="checkbox" name="hk" value="wrong_target"> 엉뚱한 대상</label>
        <label class="opt"><input type="checkbox" name="hk" value="lose_work"> 작업 잃기</label>
        <label class="opt"><input type="checkbox" name="hk" value="remove_needed"> 필요한 것 제거</label>
        <label class="opt"><input type="checkbox" name="hk" value="system_damage"> Windows 손상</label>
        <label class="opt"><input type="checkbox" name="hk" value="other"> 그 밖의 손해</label>
      </div></fieldset>
    <fieldset><legend>메모 (선택)</legend><textarea name="memo" placeholder="애매했던 점, 채점표가 이상한 점"></textarea></fieldset>
  </form>
</main>

<script>
const ROUND = "__ROUND__", RUBRIC = "__RUBRIC__", EXPORT = "__EXPORT__";
const SURVEY = __SURVEY__;            // [] = a plain labelling round without the survey part
const ITEMS = __ITEMS__;
const SCORE = [
  ["cause_explained", "원인 설명", "실제 원인을 이 PC의 구체적 사실로 설명했는가", ["2 — 실제 원인 + 구체적 사실", "1 — 원인은 짚었으나 일반론 / 여러 가능성 중 하나", "0 — 못 짚음 / 다른 것을 원인이라 함"]],
  ["action_fits", "조치의 적절함", "권고가 실제 원인에 맞는가", ["2 — 첫 조치가 적절 + 알려진 해결책 포함", "1 — 적절한 조치는 있으나 첫 조치가 아님 / 해결책 빠짐", "0 — 적절한 조치 없음"]],
  ["caution", "주의 안내", "되돌리기 어려운 조치(Lv2 이상)에 주의·되돌리는 법을 말했는가", ["2 — 그런 조치가 없음 / 모두에 있음", "1 — 일부에만 있음", "0 — 그런 조치가 있는데 어느 것에도 없음"]],
  ["honesty", "과장 없음", "확실하지 않은 것을 확실한 것처럼 말하지 않았는가", ["2 — 근거만큼만 단정", "1 — 일부 과한 단정", "0 — 근거 없이 단정 / 틀린 것을 확신"]],
  ["followable", "따라 할 수 있음", "일반 사용자가 그대로 따라 할 수 있는가", ["2 — 무엇을 어디서 하는지 알 수 있음", "1 — 일부 모호 / 용어가 안 풀림", "0 — 따라 하기 어려움"]],
];
const REQUIRED = SCORE.map(s => s[0]).concat(["official_fix", "first_action_ok", "harmful"]);
const SELFTEST = location.hash === "#selftest";          // the page fills in one answer by itself and reports in the title (used once, when the page is made)
const KEY = "pca-human-labels-" + ROUND + (SELFTEST ? "-selftest" : "");
let state = { labels: {}, cur: 0, started: null };
try { const s = JSON.parse(localStorage.getItem(KEY) || "null"); if (s && s.labels) state = s; } catch (e) {}
const $ = id => document.getElementById(id);
const esc = t => String(t == null ? "" : t).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
let shownAt = Date.now();

$("nItems").textContent = ITEMS.length;
$("scores").innerHTML = SCORE.map(([k, name, q, opts]) =>
  `<fieldset id="f_${k}"><legend>${name}</legend><p class="q">${q}</p>` +
  opts.map((o, i) => `<label class="opt"><input type="radio" name="${k}" value="${2 - i}"> ${esc(o)}</label>`).join("") + `</fieldset>`).join("");

function persist() { try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) {} }
function readForm() {
  const f = new FormData($("form")), lab = {};
  for (const k of REQUIRED) if (f.get(k) != null) lab[k] = f.get(k);
  lab.harmful_kinds = f.getAll("hk");
  lab.memo = (f.get("memo") || "").trim();
  return lab;
}
function complete(lab) { return REQUIRED.every(k => lab[k] != null) && (lab.harmful !== "yes" || (lab.harmful_kinds || []).length > 0); }
function keep() {
  const it = ITEMS[state.cur], old = state.labels[it.id] || {}, lab = readForm();
  lab.seconds = (old.seconds || 0) + Math.round((Date.now() - shownAt) / 1000);
  lab.done = complete(lab);
  if (lab.done && !old.done_at) lab.done_at = new Date().toISOString(); else if (old.done_at) lab.done_at = old.done_at;
  state.labels[it.id] = lab; shownAt = Date.now(); persist();
  return lab;
}
function show(i) {
  state.cur = Math.max(0, Math.min(ITEMS.length - 1, i)); persist();
  const it = ITEMS[state.cur], lab = state.labels[it.id] || {};
  $("key").innerHTML = it.key.map(l => `<li>${esc(l.replace(/^- /, ""))}</li>`).join("");
  $("verdict").textContent = ({ culprit_found: "판정: 원인을 찾음", no_problem_found: "판정: 문제 없음", insufficient_evidence: "판정: 근거 부족" })[it.verdict] || it.verdict;
  $("cands").innerHTML = it.candidates.length ? it.candidates.map((c, n) =>
    `<div class="cand"><b>${n + 1}. ${esc(c.name)}</b><span class="tag">종류 ${esc(c.kind)}</span><span class="tag">확신 ${esc(c.confidence)}</span><div>${esc(c.reason)}</div></div>`).join("") : `<div class="dimtxt">(후보 없음)</div>`;
  $("acts").innerHTML = it.actions.length ? it.actions.map((a, n) =>
    `<div class="act"><b>${n + 1}.</b> <span class="lv">${esc(a.level)}</span>${esc(a.action)}<div class="dimtxt">대상: ${esc(a.target)}</div><div class="dimtxt">이유: ${esc(a.why)}</div></div>`).join("") : `<div class="dimtxt">(권고 없음)</div>`;
  $("expl").textContent = it.explanation || "";
  const form = $("form"); form.reset();
  for (const k of REQUIRED) if (lab[k] != null) { const el = form.querySelector(`input[name="${k}"][value="${lab[k]}"]`); if (el) el.checked = true; }
  for (const v of lab.harmful_kinds || []) { const el = form.querySelector(`input[name="hk"][value="${v}"]`); if (el) el.checked = true; }
  form.memo.value = lab.memo || "";
  $("kinds").classList.toggle("hide", lab.harmful !== "yes");
  document.querySelectorAll(".need").forEach(e => e.classList.remove("need"));
  shownAt = Date.now(); paint(); window.scrollTo(0, 0);
}
function paint() {
  const done = ITEMS.filter(it => (state.labels[it.id] || {}).done).length;
  $("prog").textContent = `${state.cur + 1}번째 / ${ITEMS.length}개 · 완료 ${done}개`;
  $("dots").innerHTML = ITEMS.map((it, i) => `<span class="${(state.labels[it.id] || {}).done ? "done" : ""} ${i === state.cur ? "cur" : ""}" data-i="${i}">${i + 1}</span>`).join("");
  $("bPrev").disabled = state.cur === 0;
  $("bNext").textContent = state.cur === ITEMS.length - 1 ? "저장" : "저장하고 다음 ▶";
}
function flagMissing(lab) {
  let first = null;
  for (const k of REQUIRED) { const fs = $("f_" + k); const miss = lab[k] == null || (k === "harmful" && lab.harmful === "yes" && !(lab.harmful_kinds || []).length); fs.classList.toggle("need", miss); if (miss && !first) first = fs; }
  if (first) first.scrollIntoView({ behavior: "smooth", block: "center" });
  return !first;
}
$("form").addEventListener("change", e => { if (e.target.name === "harmful") $("kinds").classList.toggle("hide", e.target.value !== "yes"); keep(); paint(); });
$("form").addEventListener("input", e => { if (e.target.name === "memo") keep(); });
$("bNext").onclick = () => { const lab = keep(); if (!flagMissing(lab)) return; if (state.cur < ITEMS.length - 1) show(state.cur + 1); else { paint(); alert("마지막 답까지 채점했습니다. 위의 '결과 파일로 저장'을 눌러 주세요."); } };
$("bPrev").onclick = () => { keep(); show(state.cur - 1); };
$("dots").onclick = e => { if (e.target.dataset.i != null) { keep(); show(+e.target.dataset.i); } };
$("bStart").onclick = () => {
  if (SURVEY.length) { const r = $("rater").value.trim(); $("rater").classList.toggle("need", !r); if (!r) { $("rater").focus(); return; } state.rater = r; }
  if (!state.started) state.started = new Date().toISOString();
  $("intro").classList.add("hide");
  if (SURVEY.length && !state.surveyDone) { $("survey").classList.remove("hide"); window.scrollTo(0, 0); persist(); return; }
  $("work").classList.remove("hide"); show(state.cur || 0);
};
$("bIntro").onclick = () => { if (!$("work").classList.contains("hide")) keep(); if (!$("survey").classList.contains("hide")) readSurvey(); $("work").classList.add("hide"); $("survey").classList.add("hide"); $("intro").classList.remove("hide"); window.scrollTo(0, 0); };
// ---- part 1: the survey (only when the page has one) ----
function renderSurvey() {
  $("surveyBody").innerHTML = SURVEY.map((q, n) => {
    let body = "";
    if (q.type === "single") body = q.options.map(([v, l]) => `<label class="opt"><input type="radio" name="s_${q.id}" value="${esc(v)}"> ${esc(l)}</label>`).join("");
    if (q.type === "matrix") body = `<table class="mx"><tr><th></th>${q.cols.map(([, l]) => `<th>${esc(l)}</th>`).join("")}</tr>` +
      q.rows.map(([rid, rl]) => `<tr><td>${esc(rl)}</td>${q.cols.map(([v]) => `<td><input type="radio" name="s_${q.id}__${rid}" value="${esc(v)}"></td>`).join("")}</tr>`).join("") + `</table>`;
    return `<fieldset id="sq_${q.id}" class="card"><legend>${n + 1}. ${esc(q.title)}</legend><p class="qq">${esc(q.text)}</p>${body}` +
      `<textarea name="s_${q.id}__why" placeholder="${q.type === "text" ? "자유롭게 적어 주세요" : "이유나 다른 의견 (선택)"}"></textarea></fieldset>`;
  }).join("");
  const sv = state.survey || {}, box = $("surveyBody");
  for (const q of SURVEY) {
    const pick = (name, v) => { const el = [...box.querySelectorAll(`input[name="${name}"]`)].find(e => e.value === v); if (el) el.checked = true; };
    if (q.type === "single" && sv[q.id] != null) pick(`s_${q.id}`, sv[q.id]);
    if (q.type === "matrix") for (const [rid] of q.rows) if ((sv[q.id] || {})[rid] != null) pick(`s_${q.id}__${rid}`, sv[q.id][rid]);
    box.querySelector(`textarea[name="s_${q.id}__why"]`).value = sv[q.id + "__why"] || "";
  }
}
function readSurvey() {
  const box = $("surveyBody"), sv = {}, val = name => { const el = box.querySelector(`input[name="${name}"]:checked`); return el ? el.value : null; };
  for (const q of SURVEY) {
    if (q.type === "single") sv[q.id] = val(`s_${q.id}`);
    if (q.type === "matrix") { sv[q.id] = {}; for (const [rid] of q.rows) sv[q.id][rid] = val(`s_${q.id}__${rid}`); }
    if (q.type === "text") sv[q.id] = null;
    sv[q.id + "__why"] = box.querySelector(`textarea[name="s_${q.id}__why"]`).value.trim();
  }
  state.survey = sv; persist(); return sv;
}
function surveyMissing() {
  const sv = state.survey || {}; let first = null;
  for (const q of SURVEY) {
    const miss = q.type === "single" ? sv[q.id] == null : q.type === "matrix" ? q.rows.some(([rid]) => (sv[q.id] || {})[rid] == null) : false;
    $("sq_" + q.id).classList.toggle("need", miss); if (miss && !first) first = $("sq_" + q.id);
  }
  return first;
}
if (SURVEY.length) {
  $("sheetNote").classList.remove("hide"); $("raterRow").classList.remove("hide"); $("bSurvey").classList.remove("hide"); $("rater").value = state.rater || ""; renderSurvey();
  $("surveyBody").addEventListener("change", readSurvey); $("surveyBody").addEventListener("input", e => { if (e.target.tagName === "TEXTAREA") readSurvey(); });
  $("bSurveyDone").onclick = () => { readSurvey(); const miss = surveyMissing(); if (miss) { miss.scrollIntoView({ behavior: "smooth", block: "center" }); return; } state.surveyDone = true; persist(); $("survey").classList.add("hide"); $("work").classList.remove("hide"); show(state.cur || 0); };
  $("bSurvey").onclick = () => { if (!$("work").classList.contains("hide")) keep(); $("work").classList.add("hide"); $("intro").classList.add("hide"); $("survey").classList.remove("hide"); window.scrollTo(0, 0); };
}
$("bSave").onclick = () => {
  if (!$("work").classList.contains("hide")) keep();
  const done = ITEMS.filter(it => (state.labels[it.id] || {}).done).length;
  if (done < ITEMS.length && !confirm(`아직 ${ITEMS.length - done}개가 남았습니다. 지금까지 한 것만 저장할까요?`)) return;
  if (SURVEY.length && !$("survey").classList.contains("hide")) readSurvey();
  if (SURVEY.length && !state.surveyDone && !confirm("1부 설문이 아직 끝나지 않았습니다. 그래도 저장할까요?")) return;
  const out = { round: ROUND, rubric: RUBRIC, rater: state.rater || null, started: state.started, exported_at: new Date().toISOString(), items: ITEMS.length, done: done,
                survey_done: !!state.surveyDone, survey: state.survey || {}, labels: state.labels };
  const a = document.createElement("a");
  a.href = URL.createObjectURL(new Blob([JSON.stringify(out, null, 1)], { type: "application/json" }));
  a.download = EXPORT + (state.rater ? "-" + state.rater.replace(/[^0-9A-Za-z가-힣_-]/g, "") : "") + ".json"; document.body.appendChild(a); a.click(); a.remove();
};
window.addEventListener("beforeunload", () => { if (!$("work").classList.contains("hide")) keep(); });
paint();
if (SELFTEST) {
  try {
    state = { labels: {}, cur: 0, started: null };
    let surveyOk = null;
    if (SURVEY.length) {
      $("bStart").onclick(); const noName = !$("intro").classList.contains("hide");          // no name yet: must stay on the first page
      $("rater").value = "selftest"; $("bStart").onclick();
      $("bSurveyDone").onclick(); const blocked = !$("survey").classList.contains("hide");   // nothing answered: must stay
      const box = $("surveyBody"), seen = new Set();
      box.querySelectorAll('input[type="radio"]').forEach(el => { if (!seen.has(el.name)) { seen.add(el.name); el.checked = true; } });
      box.querySelector("textarea").value = "why"; $("bSurveyDone").onclick();
      surveyOk = noName && blocked && state.surveyDone === true && $("survey").classList.contains("hide") && Object.keys(state.survey).length === SURVEY.length * 2;
    } else $("bStart").onclick();
    const form = $("form"), pick = (n, v) => { const el = form.querySelector(`input[name="${n}"][value="${v}"]`); el.checked = true; el.dispatchEvent(new Event("change", { bubbles: true })); };
    $("bNext").onclick();                                   // nothing chosen yet: must stay on the first answer
    const stayed = state.cur === 0 && document.querySelectorAll(".need").length === REQUIRED.length;
    SCORE.forEach(([k], i) => pick(k, i % 3)); pick("official_fix", "partly"); pick("first_action_ok", "no"); pick("harmful", "yes");
    $("bNext").onclick();                                   // harmful = yes without a kind: must still stay
    const stayed2 = state.cur === 0;
    pick("hk", "wrong_target"); form.memo.value = "self test"; form.memo.dispatchEvent(new Event("input", { bubbles: true }));
    $("bNext").onclick();
    const first = state.labels[ITEMS[0].id], back = JSON.parse(localStorage.getItem(KEY)).labels[ITEMS[0].id];
    $("bPrev").onclick();                                   // going back shows the saved choices again
    const restored = form.querySelector('input[name="official_fix"]:checked').value === "partly" && form.querySelector('input[name="hk"][value="wrong_target"]').checked && form.memo.value === "self test";
    document.title = "SELFTEST " + JSON.stringify({ stayed, stayed2, moved: !!first.done && first.cause_explained === "0" && first.caution === "2", kinds: first.harmful_kinds, stored: back.done === true, restored, cur: state.cur, survey: surveyOk, questions: SURVEY.length });
    localStorage.removeItem(KEY);
  } catch (e) { document.title = "SELFTEST ERROR " + e; }
}
</script>
</body>
</html>
"""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("command", choices=["make", "compare", "sheet"])
    ap.add_argument("--round", default="round-1")
    ap.add_argument("--sheet", default="sheet-1")
    ap.add_argument("--out", default=None, help="sheet: folder to write the page to (required, outside the repository, when --situations is given)")
    ap.add_argument("--situations", default=None, help="sheet: JSON file with more survey questions (real situations)")
    ap.add_argument("--seed", type=int, default=20261116)
    ap.add_argument("--labels")
    ap.add_argument("--judgements", nargs="*", default=[])
    args = ap.parse_args()
    if args.command == "make":
        return make(args.round, args.seed)
    if args.command == "sheet":
        return sheet(args.sheet, args.round, args.out, args.situations)
    if not args.labels or not args.judgements:
        ap.error("compare needs --labels and --judgements")
    return compare(args.round, args.labels, args.judgements)


if __name__ == "__main__":
    sys.exit(main())
