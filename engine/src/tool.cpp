#include "ce/tool.hpp"

#include <algorithm>

#include "ce/engine.hpp"
#include "ce/session.hpp"

// ─────────────────────────────────────────────────────────────────────────────
// [팀원용] 도구(tool) 인터페이스 — LLM 에이전트와 엔진의 경계 (멘토링 2.1 / 3.1)
//
//   에이전트(LangGraph 등)는 엔진 함수를 직접 부르지 않고, 다음처럼 JSON 을 주고받는다.
//     입력:  crash_engine tool session_report   (표준입력으로 {"session_path":"s1.json", ...})
//     출력:  {"ok":true, "tool":"session_report", "result":{...}}
//
//   역할 분리: 확률·순서·자동 실행 여부 같은 '판단'은 엔진이 하고,
//             LLM 은 그 결과를 사용자에게 쉬운 말로 설명하고 다음 도구를 부르는 '조율'만 한다.
//   도구 목록과 입력 형식은 아래 kToolSpecsJson 에 있고, docs/tool_api.md 에 사용 예가 있다.
// ─────────────────────────────────────────────────────────────────────────────

namespace ce {

namespace {

const char* const kToolSpecsJson = R"JSON([
  {
    "name": "diagnose",
    "description": "스냅샷 1건을 진단해 층 판정, 원인 후보 순위와 근거, 원인 확인 순서(점검 계획), 각 점검의 실행 방식(자동/확인/수동/안내)을 돌려준다. 상태를 저장하지 않는 1회성 분석.",
    "input_schema": {
      "type": "object",
      "properties": {
        "snapshot_path": {"type": "string", "description": "수집기가 덤프한 스냅샷 JSON 파일 경로"},
        "snapshot": {"type": "object", "description": "스냅샷 JSON 객체 (snapshot_path 대신)"},
        "engine": {"type": "string", "enum": ["v0", "v1"], "default": "v1"},
        "mode": {"type": "string", "enum": ["guide", "auto"], "default": "guide", "description": "guide=안내 모드, auto=자동 점검 모드"},
        "allowed_actions": {"type": "array", "items": {"type": "string"}, "description": "자동 모드에서 허용할 조치 id 목록 (비우면 규칙 기본값)"},
        "rules_path": {"type": "string", "description": "규칙 파일 경로 (비우면 내장 기본 규칙)"}
      }
    }
  },
  {
    "name": "explain",
    "description": "후보 하나(생략 시 1순위)의 근거를 기여도 순으로 문장 목록으로 돌려준다. LLM이 사용자에게 '왜 이것이 원인인지' 설명할 때 쓴다.",
    "input_schema": {
      "type": "object",
      "properties": {
        "snapshot_path": {"type": "string"},
        "snapshot": {"type": "object"},
        "candidate_id": {"type": "string", "description": "예: anticheat:vgk.sys"},
        "rules_path": {"type": "string"}
      }
    }
  },
  {
    "name": "session_start",
    "description": "진단 세션을 시작한다(증상 → 원인 확인 → 해결 안내 → 검증). 세션 상태는 session_path 파일에 저장되고, 첫 점검(next_check)을 돌려준다.",
    "input_schema": {
      "type": "object",
      "required": ["session_path"],
      "properties": {
        "session_path": {"type": "string", "description": "세션 상태를 저장할 JSON 파일 경로"},
        "snapshot_path": {"type": "string"},
        "snapshot": {"type": "object"},
        "engine": {"type": "string", "enum": ["v0", "v1"], "default": "v1"},
        "mode": {"type": "string", "enum": ["guide", "auto"], "default": "guide"},
        "allowed_actions": {"type": "array", "items": {"type": "string"}},
        "rules_path": {"type": "string"}
      }
    }
  },
  {
    "name": "session_status",
    "description": "세션의 현재 상태, 후보 확률, 다음 점검 또는 해결 안내를 돌려준다.",
    "input_schema": {
      "type": "object",
      "required": ["session_path"],
      "properties": {"session_path": {"type": "string"}}
    }
  },
  {
    "name": "session_report",
    "description": "안내한 점검의 결과를 알린다. solved=증상이 사라짐, not_solved=그대로, skipped=수행할 수 없음. 엔진이 후보 확률을 베이즈 갱신하고 다음 점검 또는 확인된 원인을 돌려준다.",
    "input_schema": {
      "type": "object",
      "required": ["session_path", "check_id", "result"],
      "properties": {
        "session_path": {"type": "string"},
        "check_id": {"type": "string", "description": "next_check.check_id"},
        "result": {"type": "string", "enum": ["solved", "not_solved", "skipped"]}
      }
    }
  },
  {
    "name": "session_apply_fix",
    "description": "원인이 확인된 뒤 해결책을 적용했음을 기록한다. remedy_id 를 생략하면 현재 추천 해결책으로 본다.",
    "input_schema": {
      "type": "object",
      "required": ["session_path"],
      "properties": {
        "session_path": {"type": "string"},
        "remedy_id": {"type": "string"},
        "applied_at": {"type": "string", "description": "적용 시각 ISO-8601 (생략 시 지금)"}
      }
    }
  },
  {
    "name": "session_verify",
    "description": "해결책 적용 후 재발 여부를 검증한다. 사후 스냅샷(after_snapshot_path/after_snapshot)을 주거나, 로그로 셀 수 없는 증상이면 recurred 와 observed_minutes 를 준다. 해결되면 사례 DB에 반영한다.",
    "input_schema": {
      "type": "object",
      "required": ["session_path"],
      "properties": {
        "session_path": {"type": "string"},
        "after_snapshot_path": {"type": "string"},
        "after_snapshot": {"type": "object"},
        "recurred": {"type": "boolean"},
        "observed_minutes": {"type": "number"},
        "save_rules": {"type": "boolean", "default": false, "description": "true면 세션의 rules_path 파일에 사례 DB 갱신을 저장"}
      }
    }
  }
])JSON";

bool loadRulesArg(const json::Value& args, Rules* rules, std::string* rulesPath,
                  std::string* error) {
  *rulesPath = args["rules_path"].asString("");
  if (rulesPath->empty()) {
    *rules = Rules::defaults();
    return true;
  }
  return Rules::load(*rulesPath, rules, error);
}

bool loadSnapshotArg(const json::Value& args, const std::string& pathKey,
                     const std::string& objKey, json::Value* out, std::string* error) {
  if (args[objKey].isObject()) {
    *out = args[objKey];
    return true;
  }
  const std::string path = args[pathKey].asString("");
  if (path.empty()) {
    if (error) *error = pathKey + " 또는 " + objKey + " 가 필요합니다.";
    return false;
  }
  std::string err;
  *out = json::Value::parseFile(path, &err);
  if (!err.empty() || !out->isObject()) {
    if (error) *error = err.empty() ? "스냅샷이 JSON 객체가 아닙니다: " + path : err;
    return false;
  }
  return true;
}

bool readPolicy(const json::Value& args, ExecutionPolicy* p, std::string* error) {
  bool ok = true;
  p->mode = executionModeFromString(args["mode"].asString("guide"), &ok);
  if (!ok) {
    if (error) *error = "mode 는 guide 또는 auto 여야 합니다.";
    return false;
  }
  p->allowed_actions = args["allowed_actions"].asStringList();
  return true;
}

bool readEngine(const json::Value& args, EngineVersion* v, std::string* error) {
  bool ok = true;
  *v = engineVersionFromString(args["engine"].asString("v1"), &ok);
  if (!ok && error) *error = "engine 은 v0 또는 v1 이어야 합니다.";
  return ok;
}

bool requireString(const json::Value& args, const std::string& key, std::string* out,
                   std::string* error) {
  *out = args[key].asString("");
  if (out->empty()) {
    if (error) *error = key + " 가 필요합니다.";
    return false;
  }
  return true;
}

bool openSession(const json::Value& args, Session* s, Rules* rules, std::string* path,
                 std::string* error) {
  if (!requireString(args, "session_path", path, error)) return false;
  return loadSessionFile(*path, Rules::defaults(), s, rules, error);
}

bool evidenceGreater(const Evidence& a, const Evidence& b) { return a.contribution > b.contribution; }

// ---------------------------------------------------------------- 도구 구현

bool toolDiagnose(const json::Value& args, json::Value* result, std::string* error) {
  Rules rules;
  std::string rulesPath;
  if (!loadRulesArg(args, &rules, &rulesPath, error)) return false;
  json::Value snapJson;
  if (!loadSnapshotArg(args, "snapshot_path", "snapshot", &snapJson, error)) return false;
  ExecutionPolicy policy;
  if (!readPolicy(args, &policy, error)) return false;
  EngineVersion version;
  if (!readEngine(args, &version, error)) return false;

  std::vector<std::string> warn;
  const Snapshot s = parseSnapshot(snapJson, &warn);
  *result = diagnose(s, rules, version, policy, warn).toJson();
  return true;
}

bool toolExplain(const json::Value& args, json::Value* result, std::string* error) {
  Rules rules;
  std::string rulesPath;
  if (!loadRulesArg(args, &rules, &rulesPath, error)) return false;
  json::Value snapJson;
  if (!loadSnapshotArg(args, "snapshot_path", "snapshot", &snapJson, error)) return false;

  std::vector<std::string> warn;
  const Snapshot s = parseSnapshot(snapJson, &warn);
  const Diagnosis d = diagnose(s, rules, EngineVersion::V1, ExecutionPolicy(), warn);
  if (d.candidates.empty()) {
    if (error) *error = "후보가 없습니다.";
    return false;
  }
  int idx = 0;
  const std::string want = args["candidate_id"].asString("");
  if (!want.empty()) {
    idx = findCandidate(d.candidates, want);
    if (idx < 0) {
      if (error) *error = "후보 id 를 찾을 수 없습니다: " + want;
      return false;
    }
  }
  const Candidate& c = d.candidates[idx];
  std::vector<Evidence> ev = c.evidence;
  std::stable_sort(ev.begin(), ev.end(), evidenceGreater);

  json::Value o = json::Value::makeObject();
  o.set("candidate_id", json::Value(c.id));
  o.set("label", json::Value(c.label));
  o.set("rank", json::Value(idx + 1));
  o.set("probability", json::Value(c.probability));
  o.set("confidence", json::Value(c.confidence));
  o.set("in_focus", json::Value(c.in_focus));
  o.set("layer", json::Value(toString(d.layer.top)));
  o.set("layer_probability", json::Value(d.layer.probabilityOf(c.layer)));
  json::Value reasons = json::Value::makeArray();
  for (std::size_t i = 0; i < ev.size(); i++) {
    if (ev[i].kind == "layer") continue;  // 층 보정 계수는 근거 문장이 아니라 layer_probability 로 따로 준다
    json::Value r = json::Value::makeObject();
    r.set("kind", json::Value(ev[i].kind));
    r.set("sentence", json::Value(ev[i].detail));
    r.set("weight", json::Value(ev[i].contribution));
    reasons.push(r);
  }
  o.set("reasons", reasons);
  json::Value warnings = json::Value::makeArray();
  for (std::size_t i = 0; i < d.warnings.size(); i++) warnings.push(json::Value(d.warnings[i]));
  o.set("warnings", warnings);
  *result = o;
  return true;
}

bool toolSessionStart(const json::Value& args, json::Value* result, std::string* error) {
  std::string path;
  if (!requireString(args, "session_path", &path, error)) return false;
  Rules rules;
  std::string rulesPath;
  if (!loadRulesArg(args, &rules, &rulesPath, error)) return false;
  json::Value snapJson;
  if (!loadSnapshotArg(args, "snapshot_path", "snapshot", &snapJson, error)) return false;
  ExecutionPolicy policy;
  if (!readPolicy(args, &policy, error)) return false;
  EngineVersion version;
  if (!readEngine(args, &version, error)) return false;

  Session s;
  if (!Session::start(snapJson, rules, version, policy, rulesPath, &s, error)) return false;
  if (!saveSessionFile(path, s, error)) return false;
  *result = s.view();
  return true;
}

bool toolSessionStatus(const json::Value& args, json::Value* result, std::string* error) {
  Session s;
  Rules rules;
  std::string path;
  if (!openSession(args, &s, &rules, &path, error)) return false;
  *result = s.view();
  return true;
}

bool toolSessionReport(const json::Value& args, json::Value* result, std::string* error) {
  Session s;
  Rules rules;
  std::string path;
  if (!openSession(args, &s, &rules, &path, error)) return false;
  std::string checkId;
  std::string res;
  if (!requireString(args, "check_id", &checkId, error)) return false;
  if (!requireString(args, "result", &res, error)) return false;
  if (!s.reportCheck(checkId, res, error)) return false;
  if (!saveSessionFile(path, s, error)) return false;
  *result = s.view();
  return true;
}

bool toolSessionApplyFix(const json::Value& args, json::Value* result, std::string* error) {
  Session s;
  Rules rules;
  std::string path;
  if (!openSession(args, &s, &rules, &path, error)) return false;
  long long at = 0;
  const std::string atText = args["applied_at"].asString("");
  if (!atText.empty() && !json::parseIso8601(atText, &at)) {
    if (error) *error = "applied_at 형식이 ISO-8601 이 아닙니다: " + atText;
    return false;
  }
  if (!s.applyFix(args["remedy_id"].asString(""), at, error)) return false;
  if (!saveSessionFile(path, s, error)) return false;
  *result = s.view();
  return true;
}

bool toolSessionVerify(const json::Value& args, json::Value* result, std::string* error) {
  Session s;
  Rules rules;
  std::string path;
  if (!openSession(args, &s, &rules, &path, error)) return false;

  const bool save = args["save_rules"].asBool(false);
  VerifyResult vr;
  if (args["after_snapshot"].isObject() || !args["after_snapshot_path"].asString("").empty()) {
    json::Value after;
    if (!loadSnapshotArg(args, "after_snapshot_path", "after_snapshot", &after, error)) return false;
    if (!s.verifyWithSnapshot(after, &rules, &vr, error)) return false;
  } else if (args.has("recurred")) {
    if (!s.verifyWithAnswer(args["recurred"].asBool(false), args["observed_minutes"].asNumber(0),
                            &rules, &vr, error)) {
      return false;
    }
  } else {
    if (error) *error = "after_snapshot_path(또는 after_snapshot) 또는 recurred 가 필요합니다.";
    return false;
  }

  if (vr.outcome == "resolved" && save) {
    if (s.rulesPath().empty()) {
      vr.details.push_back("rules_path 가 없는 세션이라 사례 DB를 파일로 저장하지 않았습니다.");
    } else if (!rules.save(s.rulesPath(), error)) {
      return false;
    }
  }
  if (!saveSessionFile(path, s, error)) return false;
  json::Value o = json::Value::makeObject();
  o.set("verify", vr.toJson());
  o.set("session", s.view());
  *result = o;
  return true;
}

}  // namespace

json::Value toolSpecs() {
  std::string err;
  return json::Value::parse(kToolSpecsJson, &err);
}

bool runTool(const std::string& name, const json::Value& args, json::Value* result,
             std::string* error) {
  if (name == "list_tools") {
    *result = toolSpecs();
    return true;
  }
  if (name == "diagnose") return toolDiagnose(args, result, error);
  if (name == "explain") return toolExplain(args, result, error);
  if (name == "session_start") return toolSessionStart(args, result, error);
  if (name == "session_status") return toolSessionStatus(args, result, error);
  if (name == "session_report") return toolSessionReport(args, result, error);
  if (name == "session_apply_fix") return toolSessionApplyFix(args, result, error);
  if (name == "session_verify") return toolSessionVerify(args, result, error);
  if (error) *error = "알 수 없는 도구입니다: " + name + " (list_tools 로 목록 확인)";
  return false;
}

json::Value toolResponse(const std::string& name, bool ok, const json::Value& result,
                         const std::string& error) {
  json::Value o = json::Value::makeObject();
  o.set("ok", json::Value(ok));
  o.set("tool", json::Value(name));
  if (ok) {
    o.set("result", result);
  } else {
    o.set("error", json::Value(error));
  }
  return o;
}

}  // namespace ce
