#include "ce/planner.hpp"

#include <algorithm>
#include <set>

namespace ce {

// ─────────────────────────────────────────────────────────────────────────────
// [3단계] 점검 순서 정하기
//
// 문제 상황: 후보가 여러 개이고, 점검마다 걸리는 시간이 다르다.
//   s1 예) Discord 오버레이 끄기 20초 / Vanguard 중지 + 재부팅 5분 / 클린 부트 8분 ...
//   확률이 제일 높은 Vanguard 부터 하는 게 항상 최선은 아니다. 20초짜리로 다른 후보를 먼저 지우는 게
//   '평균적으로' 더 빨리 끝날 수 있다.
//
// v0 : 후보 확률 순서 그대로 점검 (단순, 비교 기준)
// v1 : 비용까지 고려한 순서 (이 엔진의 핵심 알고리즘, chooseNextCheck)
//      각 점검마다 "이걸 지금 하면, 결과가 '해결됨'일 때와 '아님'일 때 남은 일이 얼마나 걸릴까"를 계산해
//      [이 점검 비용 + 이후 기대 비용]이 가장 작은 점검을 고른다.
//
// 결과를 받으면 bayesUpdate 로 확률을 고친다(베이즈 갱신).
//   '아님'이면 그 점검이 다룬 후보의 확률이 내려가고 나머지가 올라간다.
//   s1 예) 오버레이 2개와 Defender 가 '아님' → Vanguard 43% → 79%
// ─────────────────────────────────────────────────────────────────────────────

std::string toString(EngineVersion v) { return v == EngineVersion::V0 ? "v0" : "v1"; }

EngineVersion engineVersionFromString(const std::string& s, bool* ok) {
  if (ok) *ok = true;
  if (s == "v0") return EngineVersion::V0;
  if (s == "v1") return EngineVersion::V1;
  if (ok) *ok = false;
  return EngineVersion::V1;
}

namespace {

// 기준 정책에서 쓰는 후보별 '가장 싼 개별 점검' 정보
struct BaseItem {
  double p;
  double cost;
  double rel;
};

bool baseItemBetter(const BaseItem& a, const BaseItem& b) {
  const double ca = a.cost < 1.0 ? 1.0 : a.cost;
  const double cb = b.cost < 1.0 ? 1.0 : b.cost;
  return a.p * a.rel / ca > b.p * b.rel / cb;
}

// 기준 정책의 기대 비용: 후보별 가장 싼 개별 점검을 '확률x신뢰도/비용' 순으로 수행할 때
// 원인에 도달할 때까지의 기대 누적 비용.
double basePolicyCost(const std::vector<double>& belief, const std::vector<CheckInstance>& instances,
                      const std::map<std::string, int>& index, const std::vector<char>& used) {
  std::vector<BaseItem> best(belief.size());
  std::vector<char> hasTest(belief.size(), 0);

  for (std::size_t k = 0; k < instances.size(); k++) {
    if (used[k]) continue;
    const CheckInstance& inst = instances[k];
    if (inst.covers.size() != 1) continue;  // 기준 정책은 개별 점검만 쓴다
    std::map<std::string, int>::const_iterator it = index.find(inst.covers[0]);
    if (it == index.end()) continue;
    const int i = it->second;
    if (!hasTest[i] || inst.cost_seconds < best[i].cost) {
      hasTest[i] = 1;
      best[i].p = belief[i];
      best[i].cost = inst.cost_seconds;
      best[i].rel = inst.reliability;
    }
  }

  std::vector<BaseItem> list;
  for (std::size_t i = 0; i < belief.size(); i++) {
    if (hasTest[i] && belief[i] > 1e-6) list.push_back(best[i]);
  }
  std::sort(list.begin(), list.end(), baseItemBetter);

  double expected = 0;
  double survive = 1.0;
  for (std::size_t i = 0; i < list.size(); i++) {
    expected += survive * list[i].cost;
    survive *= (1.0 - list[i].p * list[i].rel);
    if (survive < 1e-4) break;
  }
  return expected;
}

std::string coversLabel(const CheckInstance& inst, const std::vector<Candidate>& cands) {
  if (inst.covers.size() == 1) {
    const int idx = findCandidate(cands, inst.covers[0]);
    if (idx >= 0) return cands[idx].label;
  }
  return "후보 " + std::to_string(inst.covers.size()) + "건";
}

CheckInstance fromTemplate(const CheckTemplate& t) {
  CheckInstance inst;
  inst.check_id = t.id;
  inst.template_id = t.id;
  inst.label = t.label;
  inst.advice = t.advice;
  inst.action_id = t.action;
  inst.cost_seconds = t.cost_seconds;
  inst.reliability = t.reliability;
  inst.group = t.group;
  return inst;
}

std::map<std::string, int> makeIndex(const std::vector<Candidate>& candidates) {
  std::map<std::string, int> index;
  for (std::size_t i = 0; i < candidates.size(); i++) {
    index[candidates[i].id] = static_cast<int>(i);
  }
  return index;
}

}  // namespace

json::Value PlanStep::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("check_id", json::Value(check.check_id));
  o.set("label", json::Value(check.label));
  o.set("advice", json::Value(check.advice));
  o.set("action_id", json::Value(check.action_id));
  o.set("target", json::Value(check.covers_label));
  o.set("cost_seconds", json::Value(check.cost_seconds));
  o.set("reliability", json::Value(check.reliability));
  o.set("group", json::Value(check.group));
  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < check.covers.size(); i++) arr.push(json::Value(check.covers[i]));
  o.set("covers", arr);
  o.set("reach_probability", json::Value(reach_probability));
  o.set("success_probability", json::Value(success_probability));
  o.set("covered_probability", json::Value(covered_probability));
  return o;
}

json::Value Plan::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("engine", json::Value(toString(version)));
  o.set("expected_cost_seconds", json::Value(expected_cost_seconds));
  o.set("expected_cost_minutes", json::Value(expected_cost_seconds / 60.0));
  o.set("expected_trials", json::Value(expected_trials));
  o.set("resolved_probability", json::Value(resolved_probability));
  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < steps.size(); i++) arr.push(steps[i].toJson());
  o.set("steps", arr);
  return o;
}

std::vector<CheckInstance> buildCheckInstances(const std::vector<Candidate>& candidates,
                                               const Rules& r) {
  std::vector<CheckInstance> out;
  std::set<std::string> covered;

  for (std::size_t t = 0; t < r.checks.size(); t++) {
    const CheckTemplate& tpl = r.checks[t];
    std::vector<std::string> matched;
    for (std::size_t i = 0; i < candidates.size(); i++) {
      if (tpl.appliesTo(candidates[i].category, candidates[i].layer)) {
        matched.push_back(candidates[i].id);
      }
    }
    if (matched.empty()) continue;

    if (tpl.group) {
      if (matched.size() < 2) continue;  // 1건이면 개별 점검과 같아 의미가 없다
      CheckInstance inst = fromTemplate(tpl);
      inst.covers = matched;
      inst.covers_label = coversLabel(inst, candidates);
      out.push_back(inst);
    } else {
      for (std::size_t m = 0; m < matched.size(); m++) {
        CheckInstance inst = fromTemplate(tpl);
        inst.check_id = tpl.id + "@" + matched[m];
        inst.covers.push_back(matched[m]);
        inst.covers_label = coversLabel(inst, candidates);
        out.push_back(inst);
        covered.insert(matched[m]);
      }
    }
  }

  // 어떤 점검 템플릿에도 걸리지 않은 후보는 일반 점검으로 덮는다(빠짐 방지).
  for (std::size_t i = 0; i < candidates.size(); i++) {
    const Candidate& c = candidates[i];
    if (covered.count(c.id) > 0) continue;
    CheckInstance inst;
    inst.check_id = "generic_check@" + c.id;
    inst.template_id = "generic_check";
    inst.label = c.label + " 점검";
    inst.advice = "해당 항목을 일시적으로 중지하거나 상태를 확인한 뒤 문제를 재현해 보세요.";
    inst.action_id = "a_stop_process";
    inst.cost_seconds = 120;
    inst.reliability = 0.7;
    inst.covers.push_back(c.id);
    inst.covers_label = c.label;
    out.push_back(inst);
  }
  return out;
}

std::vector<Candidate> selectPlanningCandidates(const std::vector<Candidate>& all,
                                                const std::vector<Layer>& skippedLayers,
                                                double minProbability) {
  std::vector<Candidate> kept;
  for (std::size_t i = 0; i < all.size(); i++) {
    bool skipped = false;
    for (std::size_t k = 0; k < skippedLayers.size(); k++) {
      if (all[i].layer == skippedLayers[k]) skipped = true;
    }
    if (skipped) continue;  // 생략을 권고한 층은 점검 순서에도 넣지 않는다
    if (all[i].probability < minProbability) continue;
    kept.push_back(all[i]);
  }
  if (kept.empty()) kept = all;  // 전부 걸러졌다면 원래 목록을 쓴다

  double total = 0;
  for (std::size_t i = 0; i < kept.size(); i++) total += kept[i].probability;
  if (total > 0) {
    for (std::size_t i = 0; i < kept.size(); i++) kept[i].probability /= total;
  }
  return kept;
}

// [설명] 베이즈 갱신
//   reliability(신뢰도) r = "범인이 이 점검 대상 안에 있을 때, 점검하면 증상이 실제로 사라질 확률".
//   '해결됨' : 대상 안의 후보는 × r,     대상 밖의 후보는 × (1-r)  → 대상 안이 커진다
//   '아님'   : 대상 안의 후보는 × (1-r), 대상 밖의 후보는 × r      → 대상 안이 작아진다
//   곱한 뒤 전체 합으로 나누어 다시 합이 1이 되게 만든다.
std::vector<double> bayesUpdate(const std::vector<double>& belief, const std::vector<char>& inSet,
                                double reliability, bool positive) {
  std::vector<double> out(belief.size(), 0.0);
  double sum = 0;
  for (std::size_t i = 0; i < belief.size(); i++) {
    double like = 0;
    if (positive) {
      like = inSet[i] ? reliability : 1 - reliability;
    } else {
      like = inSet[i] ? 1 - reliability : reliability;
    }
    out[i] = belief[i] * like;
    sum += out[i];
  }
  if (sum <= 1e-12) return belief;
  for (std::size_t i = 0; i < out.size(); i++) out[i] /= sum;
  return out;
}

std::vector<char> coverMask(const CheckInstance& inst, const std::map<std::string, int>& index,
                            std::size_t n) {
  std::vector<char> mask(n, 0);
  for (std::size_t i = 0; i < inst.covers.size(); i++) {
    std::map<std::string, int>::const_iterator it = index.find(inst.covers[i]);
    if (it != index.end()) mask[it->second] = 1;
  }
  return mask;
}

// [설명] chooseNextCheck : 다음 점검 하나 고르기 (1단계 앞을 내다보기)
//   for 모든 점검 k:
//     pPos = 이 점검에서 '해결됨'이 나올 확률, pNeg = 1 - pPos
//     afterPos = '해결됨'이 나온 뒤 남은 기대 비용  (개별 점검이면 범인이 바로 정해지므로 0)
//     afterNeg = '아님'이 나온 뒤 남은 기대 비용
//     total = 이 점검 비용 + pPos × afterPos + pNeg × afterNeg
//   total 이 가장 작은 점검을 고른다.
//   '남은 기대 비용'은 basePolicyCost(확률×신뢰도/비용 이 큰 순서로 하나씩 점검하는 간단한 방법)로 어림한다.
int chooseNextCheck(const std::vector<double>& belief, const std::vector<CheckInstance>& instances,
                    const std::map<std::string, int>& index, const std::vector<char>& used) {
  // 기준 정책(base policy): 후보별 개별 점검을 '확률 x 신뢰도 / 비용' 내림차순으로 수행.
  // 실제 선택: 각 점검을 한 번 해 보고, 그 결과(해결/아님)에 대해 기준 정책으로 끝까지 갔을 때의
  //   기대 비용을 계산해(1단계 전방 탐색) 총 기대 비용이 가장 작은 점검을 고른다.
  //   그룹 점검(예: 클린 부트)은 한 번에 여러 후보를 걷어내 이후 비용을 줄이므로,
  //   그 이득이 자기 비용보다 클 때만 앞으로 나온다.
  std::vector<char> work = used;
  double bestTotal = 0;
  int bestIdx = -1;

  for (std::size_t k = 0; k < instances.size(); k++) {
    if (work[k]) continue;
    const CheckInstance& inst = instances[k];
    const std::vector<char> inSet = coverMask(inst, index, belief.size());
    double mass = 0;
    for (std::size_t i = 0; i < belief.size(); i++) {
      if (inSet[i]) mass += belief[i];
    }
    if (mass < 1e-4) continue;

    const double rel = inst.reliability;
    const double pPos = rel * mass + (1 - rel) * (1 - mass);
    const double pNeg = 1 - pPos;

    work[k] = 1;  // 전방 탐색 동안에는 이 점검을 이미 쓴 것으로 본다
    double afterPos = 0.0;
    if (inst.covers.size() != 1) {  // 개별 점검이 '해결'이면 원인이 바로 특정된다
      afterPos = basePolicyCost(bayesUpdate(belief, inSet, rel, true), instances, index, work);
    }
    const double afterNeg =
        basePolicyCost(bayesUpdate(belief, inSet, rel, false), instances, index, work);
    work[k] = 0;

    const double total = inst.cost_seconds + pPos * afterPos + pNeg * afterNeg;
    if (bestIdx < 0 || total < bestTotal) {
      bestTotal = total;
      bestIdx = static_cast<int>(k);
    }
  }
  return bestIdx;
}

Plan evaluateOrder(const std::vector<CheckInstance>& order, const std::vector<Candidate>& candidates,
                   EngineVersion version, std::size_t maxSteps, double stopResolved) {
  Plan plan;
  plan.version = version;
  if (candidates.empty()) return plan;

  const std::map<std::string, int> index = makeIndex(candidates);
  std::vector<double> p;
  for (std::size_t i = 0; i < candidates.size(); i++) p.push_back(candidates[i].probability);

  // path[i] = 후보 i 가 원인일 때, 지금까지의 점검이 모두 '아님'으로 나올 확률
  std::vector<double> path(p.size(), 1.0);

  for (std::size_t s = 0; s < order.size(); s++) {
    const CheckInstance& inst = order[s];
    const std::vector<char> inSet = coverMask(inst, index, p.size());
    double coveredP = 0;
    for (std::size_t i = 0; i < p.size(); i++) {
      if (inSet[i]) coveredP += p[i];
    }

    double reach = 0;
    double success = 0;
    for (std::size_t i = 0; i < p.size(); i++) {
      reach += p[i] * path[i];
      // '원인이 실제로 드러날' 확률: 원인이 커버 집합 안에 있고 점검이 그것을 잡아낼 때만 센다.
      if (inSet[i]) success += p[i] * path[i] * inst.reliability;
    }
    if (reach < 1e-6) break;  // 사실상 도달하지 않는 단계는 안내하지 않는다

    PlanStep step;
    step.check = inst;
    step.reach_probability = reach;
    step.success_probability = success;
    step.covered_probability = coveredP;
    plan.steps.push_back(step);

    plan.expected_cost_seconds += reach * inst.cost_seconds;
    plan.expected_trials += reach;
    plan.resolved_probability += success;

    for (std::size_t i = 0; i < p.size(); i++) {
      path[i] *= inSet[i] ? (1 - inst.reliability) : inst.reliability;
    }

    if (plan.steps.size() >= maxSteps || plan.resolved_probability >= stopResolved) break;
  }
  return plan;
}

Plan buildPlan(const std::vector<Candidate>& candidates, const Rules& r, EngineVersion version) {
  const std::vector<CheckInstance> instances = buildCheckInstances(candidates, r);
  if (candidates.empty() || instances.empty()) {
    Plan empty;
    empty.version = version;
    return empty;
  }

  std::vector<CheckInstance> order;

  if (version == EngineVersion::V0) {
    // v0: 후보 점수 순서대로, 후보별 개별 점검만 안내한다.
    for (std::size_t c = 0; c < candidates.size(); c++) {
      int best = -1;
      for (std::size_t k = 0; k < instances.size(); k++) {
        const CheckInstance& inst = instances[k];
        if (inst.group || inst.covers.size() != 1 || inst.covers[0] != candidates[c].id) continue;
        if (best < 0 || inst.cost_seconds < instances[best].cost_seconds) {
          best = static_cast<int>(k);
        }
      }
      if (best >= 0) order.push_back(instances[best]);
    }
    return evaluateOrder(order, candidates, version, 8, 0.92);
  }

  // v1: 비용 가중 적응형 그룹 테스팅. 각 단계는 '앞 점검이 아님으로 끝난' 상황을 가정해 고른다.
  const std::map<std::string, int> index = makeIndex(candidates);
  std::vector<double> belief;
  double sum = 0;
  for (std::size_t i = 0; i < candidates.size(); i++) {
    belief.push_back(candidates[i].probability);
    sum += candidates[i].probability;
  }
  if (sum > 0) {
    for (std::size_t i = 0; i < belief.size(); i++) belief[i] /= sum;
  }

  std::vector<char> used(instances.size(), 0);
  const std::size_t maxSteps = instances.size() < 8 ? instances.size() : 8;

  for (std::size_t step = 0; step < maxSteps; step++) {
    const int pick = chooseNextCheck(belief, instances, index, used);
    if (pick < 0) break;
    used[pick] = 1;
    order.push_back(instances[pick]);
    const std::vector<char> inSet = coverMask(instances[pick], index, belief.size());
    belief = bayesUpdate(belief, inSet, instances[pick].reliability, false);
  }

  return evaluateOrder(order, candidates, version, 8, 0.92);
}

Plan buildBaselinePlan(const std::vector<Candidate>& candidates, const Rules& r) {
  // 커뮤니티 표준 순서: 후보 확률과 무관하게 정해진 순서로, 해당 층·카테고리를 통째로 점검한다.
  std::vector<CheckInstance> order;
  for (std::size_t k = 0; k < r.baseline_checklist.size(); k++) {
    const CheckTemplate* t = r.findCheck(r.baseline_checklist[k]);
    if (t == nullptr) continue;
    CheckInstance inst = fromTemplate(*t);
    inst.group = true;
    for (std::size_t i = 0; i < candidates.size(); i++) {
      if (t->appliesTo(candidates[i].category, candidates[i].layer)) {
        inst.covers.push_back(candidates[i].id);
      }
    }
    if (inst.covers.empty()) continue;
    inst.covers_label = coversLabel(inst, candidates);
    order.push_back(inst);
  }
  return evaluateOrder(order, candidates, EngineVersion::V0, 8, 0.92);
}

}  // namespace ce
