#include "ce/engine.hpp"

#include <cmath>
#include <sstream>

#include "ce/signals.hpp"

namespace ce {

std::string percentText(double x) {
  return std::to_string(static_cast<int>(std::lround(x * 100))) + "%";
}

std::string durationText(double seconds) {
  if (seconds < 90) return std::to_string(static_cast<int>(std::lround(seconds))) + "초";
  if (seconds < 5400) return std::to_string(static_cast<int>(std::lround(seconds / 60))) + "분";
  std::ostringstream os;
  os.precision(1);
  os << std::fixed << seconds / 3600.0 << "시간";
  return os.str();
}

namespace {

std::string executionText(const std::string& execution) {
  if (execution == "auto") return "자동 실행";
  if (execution == "confirm") return "확인 후 실행";
  if (execution == "manual") return "직접 수행";
  return "안내";
}

json::Value stringsToJson(const std::vector<std::string>& list) {
  json::Value a = json::Value::makeArray();
  for (std::size_t i = 0; i < list.size(); i++) a.push(json::Value(list[i]));
  return a;
}

}  // namespace

bool matchesGroundTruth(const Candidate& c, const Snapshot& s) {
  if (!s.has_ground_truth) return false;
  if (!s.truth_category.empty() && c.category != s.truth_category) return false;
  if (!s.truth_subject.empty()) {
    // 표기가 달라도(모듈 파일명 vs 제품명) 같은 대상이면 정답으로 본다.
    if (!sameSubject(c.subject, s.truth_subject) && !sameSubject(c.label, s.truth_subject)) {
      return false;
    }
  }
  return true;
}

// ─────────────────────────────────────────────────────────────────────────────
// diagnose() : 엔진의 전체 흐름이 한눈에 보이는 함수
//
//   ① classifyLayer      → "SW 69% / DRIVER 22% / HW 10%" 같은 층 확률
//   ② buildCandidates    → 후보 목록과 확률 (s1 예: Vanguard 43%, Discord 오버레이 21% ...)
//   ③ 층 생략 판단        → 확률 15% 미만인 층(s1 에서는 HW)은 점검하지 말라고 안내
//   ④ buildPlan          → 점검 순서 (s1 예: 오버레이 끄기 → ... → 안티치트 중지)
//   ⑤ decideAction       → 각 점검을 자동으로 해도 되는지(안내 모드면 전부 guide)
//   ⑥ 범위·주의 문구      → 1차 범위(보안 프로그램) 안인지, 수집이 빠진 항목은 없는지
//
// 이 함수는 상태를 저장하지 않는다(같은 입력이면 항상 같은 결과).
// 점검 결과를 받아 이어 가는 일은 session.cpp 가 맡는다.
// ─────────────────────────────────────────────────────────────────────────────
Diagnosis diagnose(const Snapshot& s, const Rules& r, EngineVersion version,
                   const ExecutionPolicy& policy, const std::vector<std::string>& parseWarnings) {
  Diagnosis d;
  d.snapshot_id = s.snapshot_id;
  d.symptom = toString(s.symptom);
  d.target_process = s.target_process;
  d.tz_offset_minutes = s.tz_offset_minutes;
  d.warnings = parseWarnings;
  d.policy = policy;

  d.layer = classifyLayer(s, r);
  d.candidates = buildCandidates(s, r, d.layer);

  // 층 확률이 충분히 낮으면 그 층의 점검을 생략하도록 안내하고, 점검 순서에서도 빼 둔다
  // (제안서 시나리오 3: HW로 판정되면 소프트웨어 점검을 건너뛰게 한다).
  const Layer layers[3] = {Layer::Software, Layer::Driver, Layer::Hardware};
  const char* layerNames[3] = {"소프트웨어", "드라이버", "하드웨어"};
  for (int k = 0; k < 3; k++) {
    if (d.layer.probabilityOf(layers[k]) >= r.skip_layer_threshold) continue;
    bool hadCandidate = false;
    for (std::size_t i = 0; i < d.candidates.size(); i++) {
      if (d.candidates[i].layer == layers[k]) {
        hadCandidate = true;
        break;
      }
    }
    if (hadCandidate) {
      d.skipped_layers.push_back(layers[k]);
      d.skipped.push_back(std::string(layerNames[k]) + " 점검은 생략을 권합니다 (" +
                          toString(layers[k]) + " 확률 " +
                          percentText(d.layer.probabilityOf(layers[k])) + ").");
    }
  }

  d.planning = selectPlanningCandidates(d.candidates, d.skipped_layers, 0.02);
  d.plan = buildPlan(d.planning, r, version, d.symptom);

  // 점검 단계마다 '자동으로 해도 되는가'를 결정한다.
  for (std::size_t i = 0; i < d.plan.steps.size(); i++) {
    const CheckInstance& inst = d.plan.steps[i].check;
    ActionTarget target;
    target.subject = inst.covers_label;
    target.product = inst.covers_label;
    target.target_process = s.target_process;
    if (inst.covers.size() == 1) {
      const int idx = findCandidate(d.candidates, inst.covers[0]);
      if (idx >= 0) target = actionTargetOf(d.candidates[idx], s.target_process);
    }
    d.step_actions.push_back(decideAction(r, inst.action_id, policy, target));
  }

  // 지원 범위 안내 (1차 보안 → 2차 드라이버·시스템 → 3차 하드웨어)
  if (!r.scopes.empty() && d.top() != nullptr) {
    if (d.top()->stage > 0) {
      d.notes.push_back("1순위 후보는 " + std::to_string(d.top()->stage) + "차 지원 범위(" +
                        r.stageName(d.top()->stage) + ")입니다.");
    } else {
      d.notes.push_back("1순위 후보는 지원 범위 밖(일반 소프트웨어)입니다. 원인 판정은 그대로 제공하지만 "
                        "해결 안내는 일반 안내 수준입니다.");
    }
  }

  // 대상 프로그램 자신이 증상 직전에 바뀌었다면 함께 알려 준다(업데이트 직후 오탐·호환 문제).
  for (std::size_t i = 0; i < s.changes.size(); i++) {
    const ChangeItem& ch = s.changes[i];
    if (s.target_process.empty() || !sameSubject(ch.subject, s.target_process)) continue;
    if (signals::timeCorrelation(ch.time, signals::referenceTime(s), r) < 0.3) continue;
    d.notes.push_back("대상 프로그램 자체가 증상 직전에 바뀌었습니다(" + ch.subject + " " + ch.version +
                      "). 실행 파일이 바뀐 직후에는 백신 오탐이나 보안 프로그램과의 호환 문제가 "
                      "생기기 쉽습니다.");
  }

  if (!s.available.sensors) {
    d.warnings.push_back("센서 값 확인 불가: 하드웨어 판정 신뢰도를 낮춰 표시했습니다.");
  }
  if (!s.available.baseline) {
    d.warnings.push_back("베이스라인 스냅샷 없음: '무엇이 바뀌었는가'는 설치 이력만으로 추정했습니다.");
  }
  if (d.candidates.empty()) {
    d.warnings.push_back("근거가 될 신호를 찾지 못했습니다. 재현 모드로 다시 수집해 주세요.");
  }
  return d;
}

json::Value Diagnosis::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("snapshot_id", json::Value(snapshot_id));
  o.set("symptom", json::Value(symptom));
  o.set("target_process", json::Value(target_process));
  o.set("policy", policy.toJson());
  o.set("layer", layer.toJson(tz_offset_minutes));

  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < candidates.size(); i++) arr.push(candidates[i].toJson(tz_offset_minutes));
  o.set("candidates", arr);

  json::Value plan_ = plan.toJson();
  // 각 단계에 실행 방식(자동/확인/수동/안내)을 붙인다.
  json::Value steps = json::Value::makeArray();
  for (std::size_t i = 0; i < plan.steps.size(); i++) {
    json::Value st = plan.steps[i].toJson();
    if (i < step_actions.size()) st.set("action", step_actions[i].toJson());
    steps.push(st);
  }
  plan_.set("steps", steps);
  o.set("plan", plan_);

  o.set("skipped", stringsToJson(skipped));
  o.set("notes", stringsToJson(notes));
  o.set("warnings", stringsToJson(warnings));
  return o;
}

std::string Diagnosis::toText() const {
  std::ostringstream os;
  os << "=== 진단 결과: " << snapshot_id << " ===\n";
  os << "증상: " << symptom;
  if (!target_process.empty()) os << " / 대상: " << target_process;
  os << " / 실행 방식: " << (policy.mode == ExecutionMode::Auto ? "자동 점검 모드" : "안내 모드")
     << "\n\n";

  os << "[1] 층 판정: " << toString(layer.top) << "  (SW " << percentText(layer.p_sw)
     << " · DRIVER " << percentText(layer.p_driver) << " · HW " << percentText(layer.p_hw)
     << ")  신뢰도 " << percentText(layer.confidence) << "\n";
  for (std::size_t i = 0; i < layer.evidence.size(); i++) {
    if (layer.evidence[i].contribution < 0.05) continue;
    os << "   - " << layer.evidence[i].detail << "\n";
  }

  os << "\n[2] 원인 후보\n";
  for (std::size_t i = 0; i < candidates.size() && i < 5; i++) {
    const Candidate& c = candidates[i];
    os << " " << (i + 1) << ". " << c.label << "  [" << toString(c.layer) << "] "
       << percentText(c.probability) << "  신뢰도 " << percentText(c.confidence);
    if (c.stage > 0) {
      os << "  (" << c.stage << "차 범위)";
    } else {
      os << "  (범위 밖)";
    }
    os << "\n";
    for (std::size_t k = 0; k < c.evidence.size(); k++) {
      const Evidence& e = c.evidence[k];
      if (e.kind == "prior" || e.kind == "layer") continue;
      os << "      · " << e.detail << "\n";
    }
  }
  if (candidates.empty()) os << " (후보 없음)\n";

  os << "\n[3] 원인 확인 순서 (" << toString(plan.version) << ", 기대 시도 "
     << (std::lround(plan.expected_trials * 10) / 10.0) << "회 · 기대 비용 "
     << durationText(plan.expected_cost_seconds) << " · 원인 찾을 확률 "
     << percentText(plan.resolved_probability) << ")\n";
  for (std::size_t i = 0; i < plan.steps.size(); i++) {
    const PlanStep& s = plan.steps[i];
    os << " " << (i + 1) << ") " << s.check.label << " → " << s.check.covers_label << "  ("
       << durationText(s.check.cost_seconds) << ", 이 단계에서 원인이 드러날 확률 "
       << percentText(s.success_probability);
    if (i < step_actions.size()) os << ", " << executionText(step_actions[i].execution);
    os << ")\n";
    if (!s.check.advice.empty()) os << "      " << s.check.advice << "\n";
  }
  if (plan.steps.empty()) os << " (안내할 점검 없음)\n";

  if (!skipped.empty()) {
    os << "\n[생략 안내]\n";
    for (std::size_t i = 0; i < skipped.size(); i++) os << " - " << skipped[i] << "\n";
  }
  if (!notes.empty()) {
    os << "\n[범위]\n";
    for (std::size_t i = 0; i < notes.size(); i++) os << " - " << notes[i] << "\n";
  }
  if (!warnings.empty()) {
    os << "\n[확인 불가·주의]\n";
    for (std::size_t i = 0; i < warnings.size(); i++) os << " - " << warnings[i] << "\n";
  }
  return os.str();
}

}  // namespace ce
