"""추론 엔진(crash_engine)을 파이썬에서 '도구'로 부르는 얇은 래퍼.

LangGraph / Google ADK 같은 에이전트 프레임워크에서 엔진을 도구(tool)로 등록할 때 쓴다.
외부 라이브러리 없이 표준 라이브러리만 사용한다.

    from ce_tools import CrashEngine
    eng = CrashEngine("engine/build/Release/crash_engine.exe")
    view = eng.call("session_start", session_path="s.json",
                    snapshot_path="engine/data/samples/s1_anticheat_crash.json")
    print(view["next_check"]["label"])

LangGraph 예시 (정태웅 담당 부분과 연결할 때):

    from langchain_core.tools import StructuredTool
    tools = [StructuredTool.from_function(func=eng.tool_function(spec["name"]),
                                          name=spec["name"],
                                          description=spec["description"],
                                          args_schema=spec["input_schema"])
             for spec in eng.specs()]

엔진은 판단(층 판정·후보 점수·점검 순서·실행 방식 결정)만 하고, 실제 조치 실행은
에이전트 쪽 실행기가 action.execution 값(auto / confirm / manual / guide)을 보고 수행한다.
"""
import json
import os
import subprocess

# Windows 에서 창 있는 프로그램(UI)이 엔진을 부를 때 검은 콘솔 창이 깜빡이지 않게 한다.
NO_WINDOW = 0x08000000 if os.name == "nt" else 0


class EngineError(RuntimeError):
    pass


class CrashEngine:
    def __init__(self, exe_path, timeout=30):
        self.exe_path = exe_path
        self.timeout = timeout

    def call(self, tool_name, **args):
        """도구를 실행하고 result 를 돌려준다. 실패하면 EngineError."""
        proc = subprocess.run(
            [self.exe_path, "tool", tool_name],
            input=json.dumps(args, ensure_ascii=False).encode("utf-8"),
            capture_output=True,
            timeout=self.timeout,
            creationflags=NO_WINDOW,
        )
        text = proc.stdout.decode("utf-8", errors="replace")
        try:
            reply = json.loads(text)
        except json.JSONDecodeError:
            raise EngineError("엔진 출력이 JSON 이 아닙니다: " + text[:200])
        if not reply.get("ok"):
            raise EngineError(reply.get("error", "알 수 없는 오류"))
        return reply["result"]

    def specs(self):
        """도구 목록과 입력 스키마 (crash_engine tool list)."""
        return self.call("list_tools")

    def tool_function(self, tool_name):
        """에이전트 프레임워크에 넘길 함수(키워드 인자 → dict)."""
        def run(**kwargs):
            return self.call(tool_name, **kwargs)
        run.__name__ = tool_name
        return run


if __name__ == "__main__":
    import sys

    if len(sys.argv) < 2:
        print("사용법: python ce_tools.py <crash_engine 실행 파일 경로>")
        sys.exit(2)
    eng = CrashEngine(sys.argv[1])
    for spec in eng.specs():
        print(spec["name"], "-", spec["description"][:60])
