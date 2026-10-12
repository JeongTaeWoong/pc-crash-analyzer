"""PC Crash Analyzer - LLM 에이전트 연결 **Python 시안** (멘토링 1차 2.1 / 3.1)
※ 최종 버전은 C# UI 안으로 옮긴다. 이 파일은 시안 UI(crash_ui.py)의 상담 창용.

역할 분담
  * 추론 엔진(crash_engine) : 층 판정, 원인 후보 확률, 점검 순서, 실행 방식(auto/confirm/manual/guide), 검증
  * LLM 에이전트(이 파일)    : 사용자의 말을 알아듣고 → 엔진 도구를 부르고 → 결과를 쉬운 말로 설명

LLM 이 원인이나 순서를 '스스로 추측'하지 못하게, 아래 두 가지로 막는다.
  1) 시스템 프롬프트: "원인·확률·순서·실행 방식은 반드시 도구 결과만 쓴다"
  2) LLM 에게는 세션 파일·스냅샷 경로를 주지 않는다. 경로는 이 파일이 넣는다(LLM 은 check_id 같은 값만 고른다).

두 가지 모드
  * ClaudeAgent  : Claude API(tool use)로 동작. ANTHROPIC_API_KEY 환경 변수가 필요하다.
  * OfflineAgent : LLM 없이 정해진 말("그대로예요", "왜요?" 등)을 알아듣는 규칙 기반 대역.
                   API 키 없이 시연·테스트할 때 쓴다. 같은 도구를 같은 방식으로 부른다.

사용 예
    from ce_agent import make_agent, find_engine
    agent = make_agent(find_engine(), "s1_session.json")
    print(agent.ask("발로란트 깔고 나서 다른 게임이 자꾸 튕겨요"))

명령줄 (세션이 이미 시작되어 있어야 함. 없으면 --snapshot 으로 시작)
    python ui/prototype-python/ce_agent.py --session s.json --snapshot engine/data/samples/s1_anticheat_crash.json
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))  # ui/prototype-python → 저장소 최상위
sys.path.insert(0, os.path.join(REPO, "engine", "tools"))

from ce_tools import CrashEngine, EngineError  # noqa: E402

DEFAULT_MODEL = os.environ.get("CE_MODEL", "claude-sonnet-5-5")
MAX_TOOL_ROUNDS = 8          # 한 번의 질문에서 도구를 최대 몇 번까지 부를지
MAX_RESULT_CHARS = 12000     # LLM 에게 넘기는 도구 결과 길이 상한


def find_engine():
    """crash_engine 실행 파일 위치를 찾는다. CE_ENGINE 환경 변수가 있으면 그것을 쓴다."""
    env = os.environ.get("CE_ENGINE")
    if env:
        return env
    candidates = [
        os.path.join(REPO, "engine", "build", "Release", "crash_engine.exe"),
        os.path.join(REPO, "engine", "build", "Debug", "crash_engine.exe"),
        os.path.join(REPO, "engine", "build", "crash_engine.exe"),
        os.path.join(REPO, "engine", "build", "crash_engine"),
    ]
    for i in range(len(candidates)):
        if os.path.exists(candidates[i]):
            return candidates[i]
    raise EngineError("crash_engine 실행 파일을 찾지 못했습니다. engine 폴더에서 먼저 빌드하거나 "
                      "CE_ENGINE 환경 변수로 경로를 알려 주세요.")


# ─────────────────────────────────────────────────────────────────────────────
# LLM 에게 보여 줄 도구 (엔진 도구 7종 중 대화에 필요한 것만, 경로 인자는 뺀 형태)
# ─────────────────────────────────────────────────────────────────────────────
AGENT_TOOLS = [
    {
        "name": "get_status",
        "description": "현재 진단 세션 상태를 가져온다. 원인 후보 확률(belief), 다음 점검(next_check), "
                       "확정된 원인(confirmed), 해결책(remedies), 최근 검증(last_verify), 다음 할 일(next_action)이 들어 있다. "
                       "사용자에게 무엇이든 안내하기 전에 먼저 부른다.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "report_check",
        "description": "사용자가 안내받은 점검을 해 본 결과를 엔진에 알린다. check_id 는 get_status 의 next_check.check_id 를 "
                       "그대로 쓴다. result: solved=증상이 사라짐, not_solved=그대로, skipped=할 수 없음. "
                       "사용자가 결과를 분명히 말했을 때만 부른다.",
        "input_schema": {
            "type": "object",
            "properties": {
                "check_id": {"type": "string"},
                "result": {"type": "string", "enum": ["solved", "not_solved", "skipped"]},
            },
            "required": ["check_id", "result"],
        },
    },
    {
        "name": "explain",
        "description": "원인 후보 하나의 근거를 기여도 순 문장 목록으로 가져온다. 사용자가 '왜 이게 원인이냐'고 물을 때 쓴다. "
                       "candidate_id 를 비우면 1순위 후보.",
        "input_schema": {"type": "object", "properties": {"candidate_id": {"type": "string"}}},
    },
    {
        "name": "apply_fix",
        "description": "사용자가 해결책을 실제로 적용했다고 말했을 때 기록한다. remedy_id 를 비우면 현재 추천 해결책. "
                       "적용했다는 말이 없으면 부르지 않는다.",
        "input_schema": {"type": "object", "properties": {"remedy_id": {"type": "string"}}},
    },
    {
        "name": "verify_with_answer",
        "description": "해결책 적용 뒤 재발 여부를 사용자 답변으로 검증한다. recurred=다시 증상이 났는지, "
                       "observed_minutes=적용 후 대상 프로그램을 써 본 시간(분). 사후 스냅샷 검증은 앱 화면에서 따로 한다.",
        "input_schema": {
            "type": "object",
            "properties": {
                "recurred": {"type": "boolean"},
                "observed_minutes": {"type": "number"},
            },
            "required": ["recurred", "observed_minutes"],
        },
    },
]

SYSTEM_PROMPT = """너는 Windows PC 크래시 진단 도구 'PC Crash Analyzer'의 상담 도우미다.
대학생도 이해할 수 있는 쉬운 한국어로 짧게 답한다.

반드시 지킬 것
1. 원인 후보, 확률, 점검 순서, 해결책, 실행 방식은 오직 도구 결과만 근거로 말한다. 스스로 원인을 추측하거나 순서를 바꾸지 않는다.
2. 안내하기 전에 get_status 로 현재 상태를 확인한다.
3. 점검이나 해결책을 안내할 때 action.execution 값을 지킨다.
   - guide: 사용자가 직접 할 단계를 안내 / confirm: 실행 전에 사용자 동의를 받으라고 말함
   - manual: 사람이 직접 해야 하는 물리 작업 / auto: 자동 실행 가능하지만 되돌리는 방법을 함께 알림
4. 점검을 안내할 때는 왜 이 점검이 먼저인지(걸리는 시간, 원인이 드러날 확률)와 되돌리는 방법(rollback)을 함께 말한다.
5. 사용자가 결과를 분명히 말했을 때만 report_check / apply_fix / verify_with_answer 를 부른다. 애매하면 되묻는다.
6. 원인이 확정되면 restore_after_check(점검 때 바꾼 설정 되돌리기)를 먼저 알려 준 뒤 해결책을 안내한다.
7. 백신 실시간 감시 끄기처럼 보안 공백이 생기는 조치는 '잠깐만, 확인 후 바로 다시 켜기'를 강조한다.
"""


def _short_json(obj):
    text = json.dumps(obj, ensure_ascii=False)
    if len(text) > MAX_RESULT_CHARS:
        text = text[:MAX_RESULT_CHARS] + " …(생략)"
    return text


class ToolRunner:
    """LLM 이 고른 도구를 실제 엔진 호출로 바꾼다. 세션·스냅샷 경로는 여기서만 넣는다."""

    def __init__(self, engine, session_path):
        self.engine = engine
        self.session_path = session_path

    def status(self):
        return self.engine.call("session_status", session_path=self.session_path)

    def run(self, name, args):
        if name == "get_status":
            return self.status()
        if name == "report_check":
            return self.engine.call("session_report", session_path=self.session_path,
                                    check_id=args.get("check_id", ""), result=args.get("result", ""))
        if name == "explain":
            extra = {}
            if args.get("candidate_id"):
                extra["candidate_id"] = args["candidate_id"]
            return self.engine.call("explain", snapshot=self.snapshot(), **extra)
        if name == "apply_fix":
            extra = {}
            if args.get("remedy_id"):
                extra["remedy_id"] = args["remedy_id"]
            return self.engine.call("session_apply_fix", session_path=self.session_path, **extra)
        if name == "verify_with_answer":
            return self.engine.call("session_verify", session_path=self.session_path,
                                    recurred=bool(args.get("recurred")),
                                    observed_minutes=float(args.get("observed_minutes", 0)))
        raise EngineError("알 수 없는 도구: " + name)

    def snapshot(self):
        """세션 파일에 저장된 원본 스냅샷 (explain 은 세션이 아니라 스냅샷을 받는다)."""
        with open(self.session_path, "r", encoding="utf-8") as f:
            state = json.load(f)
        return state.get("snapshot", {})


class ClaudeAgent:
    """Claude API tool use 반복: 질문 → (도구 호출 → 결과 전달)* → 최종 답변"""

    name = "Claude"

    def __init__(self, runner, model=DEFAULT_MODEL, client=None):
        import anthropic  # 필요할 때만 불러온다 (오프라인 모드는 설치 없이 동작)
        self.client = client if client is not None else anthropic.Anthropic()
        self.model = model
        self.runner = runner
        self.messages = []
        self.trace = []  # (도구 이름, 인자) 기록: UI 에서 '엔진을 어떻게 불렀는지' 보여 줄 때 사용

    def ask(self, user_text):
        self.messages.append({"role": "user", "content": user_text})
        for round_no in range(MAX_TOOL_ROUNDS):
            resp = self.client.messages.create(
                model=self.model,
                max_tokens=1500,
                system=SYSTEM_PROMPT,
                tools=AGENT_TOOLS,
                messages=self.messages,
            )
            # 응답 블록을 그대로 대화 기록에 남긴다 (다음 요청에 tool_use 가 있어야 tool_result 를 붙일 수 있음)
            content = []
            for i in range(len(resp.content)):
                b = resp.content[i]
                if b.type == "text" and b.text:
                    content.append({"type": "text", "text": b.text})
                elif b.type == "tool_use":
                    content.append({"type": "tool_use", "id": b.id, "name": b.name, "input": b.input})
            self.messages.append({"role": "assistant", "content": content})

            if resp.stop_reason != "tool_use":
                texts = []
                for i in range(len(resp.content)):
                    if resp.content[i].type == "text":
                        texts.append(resp.content[i].text)
                return "\n".join(texts).strip()

            results = []
            for i in range(len(resp.content)):
                block = resp.content[i]
                if block.type != "tool_use":
                    continue
                self.trace.append((block.name, block.input))
                try:
                    out = self.runner.run(block.name, block.input)
                    results.append({"type": "tool_result", "tool_use_id": block.id,
                                    "content": _short_json(out)})
                except EngineError as e:
                    results.append({"type": "tool_result", "tool_use_id": block.id,
                                    "content": str(e), "is_error": True})
            self.messages.append({"role": "user", "content": results})
        return "도구 호출이 너무 많아 여기서 멈췄습니다. 질문을 조금 더 구체적으로 해 주세요."


# ─────────────────────────────────────────────────────────────────────────────
# 오프라인 대역: LLM 없이 정해진 표현을 알아듣고 같은 도구를 부른다
# ─────────────────────────────────────────────────────────────────────────────
WORDS_SOLVED = ["사라졌", "해결됐", "해결 됐", "안 튕", "안튕", "괜찮아", "멀쩡", "됐어요", "정상"]
WORDS_NOT_SOLVED = ["그대로", "똑같", "여전히", "또 튕", "계속", "안 돼", "안돼", "아니요", "아니에요"]
WORDS_SKIPPED = ["못 하", "못하", "할 수 없", "모르겠", "건너"]
WORDS_WHY = ["왜", "근거", "이유"]
WORDS_APPLIED = ["적용했", "바꿨", "설정했", "했어요", "했습니다"]
WORDS_NO_RECUR = ["재발 없", "안 났", "안났", "문제없", "문제 없"]
WORDS_RECUR = ["다시 났", "재발", "또 났"]


def _has(text, words):
    for i in range(len(words)):
        if words[i] in text:
            return True
    return False


def _minutes(text):
    """'2시간', '90분' 같은 표현에서 분을 뽑는다. 없으면 0."""
    total = 0.0
    m = re.search(r"(\d+(?:\.\d+)?)\s*시간", text)
    if m:
        total += float(m.group(1)) * 60
    m = re.search(r"(\d+(?:\.\d+)?)\s*분", text)
    if m:
        total += float(m.group(1))
    return total


OUTCOME_TEXT = {"resolved": "해결됨", "recurred": "재발", "observing": "관찰 중(시간이 더 필요)",
                "need_answer": "사용자 응답 필요"}


def _percent(p):
    return str(int(round(p * 100))) + "%"


def describe_check(nc):
    """다음 점검을 사람이 읽을 문장으로."""
    act = nc.get("action", {})
    lines = []
    minutes = nc.get("cost_seconds", 0) / 60.0
    took = (str(int(round(minutes))) + "분") if minutes >= 1 else (str(int(nc.get("cost_seconds", 0))) + "초")
    lines.append("다음 점검: " + nc.get("label", "") + " → " + nc.get("target", ""))
    lines.append("  (약 " + took + ", 이 점검으로 원인이 드러날 확률 " + _percent(nc.get("chance_to_reveal_cause", 0)) + ")")
    steps = act.get("steps", [])
    for i in range(len(steps)):
        lines.append("  " + str(i + 1) + ") " + steps[i])
    if act.get("rollback"):
        lines.append("  되돌리기: " + act["rollback"])
    exe = act.get("execution", "guide")
    if exe == "confirm":
        lines.append("  ※ 실행 전에 동의가 필요한 조치입니다.")
    elif exe == "manual":
        lines.append("  ※ 직접 해야 하는 작업입니다.")
    lines.append("해 보시고 '증상이 사라졌어요' / '그대로예요' / '못 하겠어요' 중 하나로 알려 주세요.")
    return "\n".join(lines)


def describe_status(v):
    status = v.get("status")
    out = []
    belief = v.get("belief", [])
    if belief:
        top = []
        for i in range(min(3, len(belief))):
            top.append(belief[i]["label"] + " " + _percent(belief[i]["probability"]))
        out.append("지금 원인 후보: " + ", ".join(top))
    if status == "checking" and v.get("next_check"):
        out.append(describe_check(v["next_check"]))
    elif status == "confirmed":
        c = v.get("confirmed", {})
        out.append("원인이 확인되었습니다: " + c.get("label", ""))
        if v.get("restore_after_check"):
            out.append("먼저: " + v["restore_after_check"])
        rem = v.get("remedies", [])
        cur = v.get("current_remedy")
        for i in range(len(rem)):
            if rem[i]["remedy_id"] == cur:
                out.append("추천 해결책: " + rem[i]["label"] + " (기대 해결률 " + _percent(rem[i]["expected_success"]) + ")")
                acts = rem[i].get("actions", [])
                for k in range(len(acts)):
                    steps = acts[k].get("steps", [])
                    for s in range(len(steps)):
                        out.append("  - " + steps[s])
        out.append("적용하셨으면 '적용했어요'라고 알려 주세요.")
    elif status == "fixing":
        out.append("해결책을 적용한 상태입니다. 한동안 써 본 뒤 '2시간 동안 재발 없었어요'처럼 알려 주세요.")
    elif status == "resolved":
        out.append("해결이 확인되었습니다.")
    elif status in ("failed", "exhausted"):
        out.append(v.get("status_text", "") + " — " + v.get("next_action", ""))
    return "\n".join(out)


class OfflineAgent:
    """API 키 없이 시연할 때 쓰는 규칙 기반 대역. 판단은 똑같이 엔진이 한다."""

    name = "오프라인"

    def __init__(self, runner):
        self.runner = runner
        self.trace = []

    def call(self, name, args):
        self.trace.append((name, args))
        return self.runner.run(name, args)

    def ask(self, user_text):
        text = user_text.strip()
        v = self.call("get_status", {})
        status = v.get("status")
        try:
            if _has(text, WORDS_WHY):
                target = v.get("confirmed", {}).get("id", "") if status != "checking" else ""
                ex = self.call("explain", {"candidate_id": target} if target else {})
                lines = [ex.get("label", "1순위 후보") + "을(를) 의심하는 근거:"]
                ev = ex.get("reasons", [])
                for i in range(min(5, len(ev))):
                    lines.append("  · " + ev[i].get("sentence", ""))
                return "\n".join(lines)

            if status == "checking" and v.get("next_check"):
                check_id = v["next_check"]["check_id"]
                result = None
                if _has(text, WORDS_SKIPPED):
                    result = "skipped"
                elif _has(text, WORDS_NOT_SOLVED):
                    result = "not_solved"
                elif _has(text, WORDS_SOLVED):
                    result = "solved"
                if result is None:
                    return "증상을 이해했습니다. 엔진이 고른 순서대로 확인해 볼게요.\n" + describe_status(v)
                after = self.call("report_check", {"check_id": check_id, "result": result})
                head = {"solved": "좋아요, 결과를 반영했습니다.",
                        "not_solved": "알겠습니다. 이 후보의 확률을 낮추고 다음 점검을 골랐어요.",
                        "skipped": "건너뛰고 다음 점검으로 넘어갈게요."}[result]
                return head + "\n" + describe_status(after)

            if status == "confirmed" and _has(text, WORDS_APPLIED):
                after = self.call("apply_fix", {})
                return "적용을 기록했습니다.\n" + describe_status(after)

            if status == "fixing" and (_has(text, WORDS_NO_RECUR) or _has(text, WORDS_RECUR)):
                recurred = _has(text, WORDS_RECUR) and not _has(text, WORDS_NO_RECUR)
                mins = _minutes(text)
                res = self.call("verify_with_answer", {"recurred": recurred, "observed_minutes": mins})
                ver = res.get("verify", {})
                lines = ["검증 결과: " + OUTCOME_TEXT.get(ver.get("outcome", ""), ver.get("outcome", ""))]
                det = ver.get("details", [])
                for i in range(len(det)):
                    lines.append("  · " + det[i])
                return "\n".join(lines) + "\n" + describe_status(res.get("session", {}))
        except EngineError as e:
            return "엔진 오류: " + str(e)
        return describe_status(v)


def make_agent(engine_path, session_path, prefer_llm=True):
    """API 키가 있으면 Claude, 없으면 오프라인 대역을 돌려준다."""
    runner = ToolRunner(CrashEngine(engine_path), session_path)
    if prefer_llm and os.environ.get("ANTHROPIC_API_KEY"):
        try:
            return ClaudeAgent(runner)
        except ImportError:
            pass  # anthropic 패키지가 없으면 오프라인으로
    return OfflineAgent(runner)


def main():
    import argparse
    ap = argparse.ArgumentParser(description="PC Crash Analyzer 상담 에이전트 (명령줄)")
    ap.add_argument("--session", required=True, help="세션 상태 파일")
    ap.add_argument("--snapshot", help="세션이 없으면 이 스냅샷으로 새로 시작")
    ap.add_argument("--engine", help="crash_engine 경로 (생략 시 자동 탐색)")
    ap.add_argument("--offline", action="store_true", help="API 키가 있어도 오프라인 대역 사용")
    a = ap.parse_args()

    engine_path = a.engine or find_engine()
    if not os.path.exists(a.session):
        if not a.snapshot:
            print("세션 파일이 없습니다. --snapshot 으로 새 세션을 시작하세요.")
            return 2
        CrashEngine(engine_path).call("session_start", session_path=a.session, snapshot_path=a.snapshot)

    agent = make_agent(engine_path, a.session, prefer_llm=not a.offline)
    print("[" + agent.name + " 모드] 종료하려면 빈 줄에서 Enter")
    print(agent.ask("현재 상태를 알려 주세요"))
    while True:
        try:
            line = input("\n나> ")
        except EOFError:
            break
        if not line.strip():
            break
        print("\n도우미> " + agent.ask(line))
    return 0


if __name__ == "__main__":
    sys.exit(main())
