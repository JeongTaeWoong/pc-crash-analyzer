#include "ce/remedy.hpp"

#include <algorithm>

namespace ce {

std::string toString(ExecutionMode m) { return m == ExecutionMode::Auto ? "auto" : "guide"; }

ExecutionMode executionModeFromString(const std::string& s, bool* ok) {
  if (ok) *ok = true;
  if (s == "auto") return ExecutionMode::Auto;
  if (s == "guide" || s.empty()) return ExecutionMode::Guide;
  if (ok) *ok = false;
  return ExecutionMode::Guide;
}

json::Value ExecutionPolicy::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("mode", json::Value(toString(mode)));
  json::Value a = json::Value::makeArray();
  for (std::size_t i = 0; i < allowed_actions.size(); i++) a.push(json::Value(allowed_actions[i]));
  o.set("allowed_actions", a);
  return o;
}

ExecutionPolicy ExecutionPolicy::fromJson(const json::Value& v) {
  ExecutionPolicy p;
  p.mode = executionModeFromString(v["mode"].asString("guide"), nullptr);
  p.allowed_actions = v["allowed_actions"].asStringList();
  return p;
}

namespace {

std::string replaceAll(std::string text, const std::string& from, const std::string& to) {
  if (from.empty()) return text;
  std::size_t pos = 0;
  while ((pos = text.find(from, pos)) != std::string::npos) {
    text.replace(pos, from.size(), to);
    pos += to.size();
  }
  return text;
}

bool listHas(const std::vector<std::string>& list, const std::string& x) {
  for (std::size_t i = 0; i < list.size(); i++) {
    if (list[i] == x) return true;
  }
  return false;
}

bool remedyBetter(const RemedyOption& a, const RemedyOption& b) { return a.success > b.success; }

}  // namespace

json::Value ActionDecision::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("action_id", json::Value(action_id));
  o.set("label", json::Value(label));
  o.set("kind", json::Value(kind));
  json::Value st = json::Value::makeArray();
  for (std::size_t i = 0; i < steps.size(); i++) st.push(json::Value(steps[i]));
  o.set("steps", st);
  o.set("rollback", json::Value(rollback));
  o.set("reversible", json::Value(reversible));
  o.set("requires_reboot", json::Value(requires_reboot));
  o.set("risk", json::Value(risk));
  o.set("automation", json::Value(automation));
  o.set("execution", json::Value(execution));
  o.set("reason", json::Value(reason));
  return o;
}

json::Value RemedyOption::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("remedy_id", json::Value(remedy_id));
  o.set("label", json::Value(label));
  o.set("description", json::Value(description));
  o.set("expected_success", json::Value(success));
  json::Value a = json::Value::makeArray();
  for (std::size_t i = 0; i < actions.size(); i++) a.push(actions[i].toJson());
  o.set("actions", a);
  return o;
}

ActionTarget actionTargetOf(const Candidate& c, const std::string& targetProcess) {
  ActionTarget t;
  t.subject = c.subject;
  t.product = c.product.empty() ? c.label : c.product;
  t.target_process = targetProcess;
  return t;
}

// ─────────────────────────────────────────────────────────────────────────────
// decideAction() : 이 조치를 자동으로 해도 되는가 (멘토링 3.4 / 지도교수님 '자동화' 피드백 반영)
//
//   안내 모드(기본)                       → guide   : 엔진은 실행하지 않고 단계만 보여 준다
//   자동 모드
//     자동화 명령이 없음 / 물리 점검       → manual  : 사람이 직접 (예: 냉각 청소)
//     되돌릴 수 없음 (재설치·제거 등)     → confirm : 항상 사용자에게 먼저 묻는다
//     되돌릴 수 있고 허용된 조치           → auto    : 실행기가 원상복구 지점을 남기고 자동 실행
//     되돌릴 수 있지만 허용 안 됨          → confirm
//     재부팅이 필요                        → confirm
//
// 엔진은 '판단만' 한다. 실제 실행은 실행기(수집기/에이전트 쪽)가 execution 값을 보고 한다.
// ─────────────────────────────────────────────────────────────────────────────
ActionDecision decideAction(const Rules& r, const std::string& actionId,
                            const ExecutionPolicy& policy, const ActionTarget& target) {
  ActionDecision d;
  d.action_id = actionId;
  const ActionTemplate* t = r.findAction(actionId);
  if (t == nullptr) {
    d.label = "수동 점검";
    d.kind = "inspect";
    d.steps.push_back("해당 항목의 상태를 직접 확인해 주세요.");
    d.execution = policy.mode == ExecutionMode::Auto ? "manual" : "guide";
    d.reason = "규칙에 없는 조치라 사람이 직접 수행해야 합니다.";
    return d;
  }

  d.label = t->label;
  d.kind = t->kind;
  d.steps = t->steps;
  d.rollback = t->rollback;
  d.reversible = t->reversible;
  d.requires_reboot = t->requires_reboot;
  d.risk = t->risk;
  std::string cmd = replaceAll(t->automation, "{subject}", target.subject);
  cmd = replaceAll(cmd, "{product}", target.product);
  d.automation = replaceAll(cmd, "{target_process}", target.target_process);

  if (policy.mode == ExecutionMode::Guide) {
    d.execution = "guide";
    d.reason = "안내 모드: 엔진은 실행하지 않고 단계만 안내합니다.";
    return d;
  }

  // 자동 점검 모드
  if (d.automation.empty() || t->kind == "physical") {
    d.execution = "manual";
    d.reason = "자동화할 수 없는 조치(물리 점검·수동 확인)라 사람이 직접 해야 합니다.";
    return d;
  }
  if (!t->reversible) {
    d.execution = "confirm";
    d.reason = "되돌리기 어려운 조치라 실행 전에 반드시 사용자 확인을 받습니다.";
    return d;
  }
  bool allowed = t->auto_default;
  if (!policy.allowed_actions.empty()) allowed = listHas(policy.allowed_actions, t->id);
  if (allowed) {
    d.execution = "auto";
    d.reason = "되돌릴 수 있고 사용자가 허용한 조치라 자동으로 수행합니다(실행 전 원상복구 지점 기록).";
  } else {
    d.execution = "confirm";
    d.reason = "되돌릴 수 있지만 허용 목록에 없어 사용자 확인 후 수행합니다.";
  }
  if (t->requires_reboot && d.execution == "auto") {
    d.execution = "confirm";
    d.reason = "재부팅이 필요한 조치라 사용자 확인 후 수행합니다.";
  }
  return d;
}

std::vector<RemedyOption> remediesFor(const Rules& r, const Candidate& c, const std::string& symptom,
                                      const ExecutionPolicy& policy,
                                      const std::string& targetProcess) {
  std::vector<RemedyOption> out;
  for (std::size_t i = 0; i < r.remedies.size(); i++) {
    const RemedyTemplate& t = r.remedies[i];
    if (!t.appliesTo(c.category, c.subject, symptom)) continue;
    RemedyOption opt;
    opt.remedy_id = t.id;
    opt.label = t.label;
    opt.description = t.description;
    opt.success = t.success_seed;
    for (std::size_t k = 0; k < t.actions.size(); k++) {
      opt.actions.push_back(decideAction(r, t.actions[k], policy, actionTargetOf(c, targetProcess)));
    }
    out.push_back(opt);
  }
  std::stable_sort(out.begin(), out.end(), remedyBetter);

  if (out.empty()) {
    RemedyOption opt;
    opt.remedy_id = "r_generic";
    opt.label = c.label + " 업데이트 또는 제조사 문의";
    opt.description = "이 유형에 등록된 해결책이 없어 일반 안내를 보여 줍니다.";
    opt.success = 0.3;
    opt.actions.push_back(
        decideAction(r, "a_program_update", policy, actionTargetOf(c, targetProcess)));
    out.push_back(opt);
  }
  return out;
}

}  // namespace ce
