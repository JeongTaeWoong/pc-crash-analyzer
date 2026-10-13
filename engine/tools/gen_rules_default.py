"""data/rules.json -> src/rules_default.cpp 변환 스크립트.

기본 규칙은 data/rules.json 하나만 고치고, 이 스크립트로 C++ 소스에 다시 넣는다.
MSVC는 문자열 리터럴 하나가 약 16KB를 넘으면 컴파일 오류(C2026)가 나므로 여러 조각으로 나눈다.

사용법:  python tools/gen_rules_default.py
"""
import json
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "data", "rules.json")
DST = os.path.join(ROOT, "src", "rules_default.cpp")
CHUNK_BYTES = 12000

with open(SRC, encoding="utf-8") as f:
    text = f.read()
json.loads(text)  # 문법 오류가 있으면 여기서 멈춘다

chunks = []
cur = ""
for line in text.splitlines(keepends=True):
    if len((cur + line).encode("utf-8")) > CHUNK_BYTES and cur:
        chunks.append(cur)
        cur = ""
    cur += line
if cur:
    chunks.append(cur)

out = []
out.append("// rules_default.cpp - 기본 규칙(가중치·점검·조치·해결책·사례 시드)의 내장본.\n")
out.append("// 직접 고치지 말고 data/rules.json 을 고친 뒤 tools/gen_rules_default.py 로 다시 만든다.\n")
out.append("// 값의 근거는 docs/rules_rationale.md 에 정리한다.\n\n")
out.append("namespace ce {\nnamespace internal {\n\n")
out.append("extern const char* const kDefaultRulesParts[];\n")
out.append("const char* const kDefaultRulesParts[] = {\n")
for c in chunks:
    if ")JSON\"" in c:
        raise SystemExit("rules.json 에 )JSON\" 문자열이 있으면 안 됩니다")
    out.append('    R"JSON(' + c + ')JSON",\n')
out.append("    nullptr};\n\n}  // namespace internal\n}  // namespace ce\n")

with open(DST, "w", encoding="utf-8", newline="\n") as f:
    f.write("".join(out))
print("생성:", DST, "조각", len(chunks), "개")
