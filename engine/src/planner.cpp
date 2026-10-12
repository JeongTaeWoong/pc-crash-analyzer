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
//   s1 예) 오버레이 2개가 '아님' → Vanguard 43% → 69%
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

// [설명] basePolicyCost : '남은 기대 비용'을 빠르게 어림하는 기준 정책
//   남은 개별 점검을 하나씩 고르되, 매번 '그 후보의 남은 확률 x 신뢰도 / 비용' 이 가장 큰 점검을 한다.
//   점검이 '아님'으로 끝나면 그 후보의 남은 확률은 (1 - 신뢰도) 배로 줄어든다.
//   한 후보에 점검이 여러 개면(예: 메모리 = XMP 끄기 10분 → 메모리 검사 90분) 모두 차례로 쓸 수 있다.
//   기대 비용 = Σ (그 단계까지 원인이 아직 안 드러났을 확률) x (그 점검 비용)
//   (예전에는 후보마다 가장 싼 점검 하나만 셌는데, 그러면 비싼 후속 점검이 빠져
//    '싼 점검을 뒤로 미루는' 쪽이 실제보다 싸게 계산되는 문제가 있었다)
//   점검 비용이 '찾을 확률 x 못 찾았을 때 비용'보다 크면 그 점검은 하지 않고 포기 비용으로 센다.
double basePolicyCost(const std::vector<double>& belief, const std::vector<CheckInstance>& instances,
                      const std::map<std::string, int>& index, const std::vector<char>& used,
                      double unresolvedCost) {
  // 쓸 수 있는 개별 점검 목록 (후보 번호, 비용, 신뢰도)
  std::vector<int> who;
  std::vector<double> cost;
  std::vector<double> rel;
  for (std::size_t k = 0; k < instances.size(); k++) {
    if (used[k]) continue;
    const CheckInstance& inst = instances[k];
    if (inst.covers.size() != 1) continue;  // 기준 정책은 개별 점검만 쓴다
    std::map<std::string, int>::const_iterator it = index.find(inst.covers[0]);
    if (it == index.end()) continue;
    who.push_back(it->second);
    cost.push_back(inst.cost_seconds < 1.0 ? 1.0 : inst.cost_seconds);
    rel.push_back(inst.reliability);
  }

  std::vector<double> remain = belief;  // 후보별 '아직 드러나지 않은' 확률
  std::vector<char> done(who.size(), 0);
  double expected = 0;
  for (std::size_t step = 0; step < who.size(); step++) {
    double alive = 0;
    for (std::size_t i = 0; i < remain.size(); i++) alive += remain[i];
    if (alive < 1e-4) break;

    int pick = -1;
    double bestScore = 0;
    for (std::size_t t = 0; t < who.size(); t++) {
      if (done[t]) continue;
      const double score = remain[who[t]] * rel[t] / cost[t];
      if (pick < 0 || score > bestScore) {
        pick = static_cast<int>(t);
        bestScore = score;
      }
    }
    if (pick < 0 || remain[who[pick]] <= 1e-6) break;  // 남은 점검이 남은 후보를 못 다룬다
    // 이 점검으로 원인을 찾을 확률 x 포기 비용 < 점검 비용 → 더 해 봐야 손해
    if (remain[who[pick]] * rel[pick] * unresolvedCost < alive * cost[pick]) break;
    done[pick] = 1;
    expected += alive * cost[pick];
    remain[who[pick]] *= (1.0 - rel[pick]);
  }
  double left = 0;  // 끝까지 못 찾은 확률
  for (std::size_t i = 0; i < remain.size(); i++) left += remain[i];
  return expected + left * unresolvedCost;
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
  o.set("expected_total_seconds", json::Value(expected_total_seconds));
  o.set("expected_trials", json::Value(expected_trials));
  o.set("resolved_probability", json::Value(resolved_probability));
  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < steps.size(); i++) arr.push(steps[i].toJson());
  o.set("steps", arr);
  return o;
}

std::vector<CheckInstance> buildCheckInstances(const std::vector<Candidate>& candidates,
                                               const Rules& r, const std::string& symptom) {
  std::vector<CheckInstance> out;
  std::set<std::string> covered;

  for (std::size_t t = 0; t < r.checks.size(); t++) {
    const CheckTemplate& tpl = r.checks[t];
    if (!tpl.usableFor(symptom)) continue;
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

// [설명] 계획용 확률 모델
//   순서를 '미리' 짤 때는 이렇게 본다: 점검은 원인을 놓칠 수는 있지만(신뢰도 r),
//   원인이 아닌 것을 원인으로 착각하지는 않는다. (착각은 해결 후 검증 단계에서 '재발'로 잡힌다)
//   그래서 '아님'이 나오면 점검 대상 안의 후보만 x (1-r) 로 줄고, 대상 밖은 그대로다.
//   evaluateOrder(기대 비용 계산)와 chooseNextCheck(다음 점검 고르기)가 같은 모델을 써야
//   v1 이 계산하는 값과 우리가 비교하는 값이 일치한다.
//   (실제 결과를 받은 뒤의 갱신은 더 보수적인 bayesUpdate 를 쓴다 — 세션 쪽)
std::vector<double> missUpdate(const std::vector<double>& belief, const std::vector<char>& inSet,
                               double reliability) {
  std::vector<double> out(belief.size(), 0.0);
  double sum = 0;
  for (std::size_t i = 0; i < belief.size(); i++) {
    out[i] = inSet[i] ? belief[i] * (1 - reliability) : belief[i];
    sum += out[i];
  }
  if (sum <= 1e-12) return belief;
  for (std::size_t i = 0; i < out.size(); i++) out[i] /= sum;
  return out;
}

// [설명] chooseNextCheck : 다음 점검 하나 고르기 (1단계 앞을 내다보기)
//   for 모든 점검 k:
//     pPos = 이 점검에서 원인이 드러날 확률 = r x (대상 후보들의 확률 합)
//     afterPos = 드러난 뒤 남은 기대 비용  (개별 점검이면 원인이 바로 정해지므로 0,
//                그룹 점검이면 묶음 안에서 다시 골라야 하므로 그 비용)
//     afterNeg = '아님'이 나온 뒤 남은 기대 비용
//     total = 이 점검 비용 + pPos × afterPos + (1 - pPos) × afterNeg
//   total 이 가장 작은 점검을 고른다.
//   '남은 기대 비용'은 basePolicyCost(확률×신뢰도/비용 이 큰 순서로 하나씩 점검하는 간단한 방법)로 어림한다.
int chooseNextCheck(const std::vector<double>& belief, const std::vector<CheckInstance>& instances,
                    const std::map<std::string, int>& index, const std::vector<char>& used,
                    double unresolvedCost, double* totalOut) {
  // 그룹 점검(예: 클린 부트)은 한 번에 여러 후보를 걷어내 이후 비용을 줄이므로,
  // 그 이득이 자기 비용보다 클 때만 앞으로 나온다.
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
    const double pPos = rel * mass;

    work[k] = 1;  // 전방 탐색 동안에는 이 점검을 이미 쓴 것으로 본다
    double afterPos = 0.0;
    if (inst.covers.size() != 1) {
      // 그룹 점검이 '해결'이면 원인은 묶음 안에 있다 → 묶음 안 후보만 남겨 이어서 점검
      std::vector<double> inside(belief.size(), 0.0);
      for (std::size_t i = 0; i < belief.size(); i++) {
        if (inSet[i]) inside[i] = belief[i] / mass;
      }
      afterPos = basePolicyCost(inside, instances, index, work, unresolvedCost);
    }
    const double afterNeg =
        basePolicyCost(missUpdate(belief, inSet, rel), instances, index, work, unresolvedCost);
    work[k] = 0;

    const double total = inst.cost_seconds + pPos * afterPos + (1 - pPos) * afterNeg;
    if (bestIdx < 0 || total < bestTotal) {
      bestTotal = total;
      bestIdx = static_cast<int>(k);
    }
  }
  if (totalOut) *totalOut = bestTotal;
  return bestIdx;
}

Plan evaluateOrder(const std::vector<CheckInstance>& order, const std::vector<Candidate>& candidates,
                   EngineVersion version, std::size_t maxSteps, double stopResolved,
                   double unresolvedCost) {
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

    // 계획용 모델: 대상 안의 후보만 '놓칠 확률(1-r)'만큼 남는다 (missUpdate 와 같은 가정)
    for (std::size_t i = 0; i < p.size(); i++) {
      if (inSet[i]) path[i] *= (1 - inst.reliability);
    }

    if (plan.steps.size() >= maxSteps || plan.resolved_probability >= stopResolved) break;
  }
  double left = 1.0 - plan.resolved_probability;
  if (left < 0) left = 0;
  plan.expected_total_seconds = plan.expected_cost_seconds + left * unresolvedCost;
  return plan;
}

Plan buildPlan(const std::vector<Candidate>& candidates, const Rules& r, EngineVersion version,
               const std::string& symptom) {
  const std::vector<CheckInstance> instances = buildCheckInstances(candidates, r, symptom);
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
    return evaluateOrder(order, candidates, version, 8, 0.92, r.unresolved_cost_minutes * 60.0);
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

  const double giveUp = r.unresolved_cost_minutes * 60.0;
  for (std::size_t step = 0; step < maxSteps; step++) {
    double total = 0;
    const int pick = chooseNextCheck(belief, instances, index, used, giveUp, &total);
    if (pick < 0) break;
    // 여기까지 와서 남은 점검을 하는 기대 비용이 '포기(수리점 등)' 비용보다 크면 더 안내하지 않는다.
    // (첫 단계는 항상 안내한다)
    if (step > 0 && total >= giveUp) break;
    used[pick] = 1;
    order.push_back(instances[pick]);
    const std::vector<char> inSet = coverMask(instances[pick], index, belief.size());
    belief = missUpdate(belief, inSet, instances[pick].reliability);
  }

  return evaluateOrder(order, candidates, version, 8, 0.92, giveUp);
}

Plan buildBaselinePlan(const std::vector<Candidate>& candidates, const Rules& r,
                       const std::string& symptom) {
  (void)symptom;  // 커뮤니티 표준 순서는 증상과 상관없이 같은 순서다
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
  return evaluateOrder(order, candidates, EngineVersion::V0, 8, 0.92,
                       r.unresolved_cost_minutes * 60.0);
}

}  // namespace ce
