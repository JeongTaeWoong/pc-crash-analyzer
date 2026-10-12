// bench.cpp - 정답(ground_truth)이 적힌 시나리오들로 엔진 성능을 숫자로 잰다 (제안서 4.2 검증 지표).
//   [팀원용] 층 판정 정확도, 1순위 정답률(Top-1), 3순위 안 정답률(Top-3), 그리고
//   '커뮤니티 표준 순서 vs v0 vs v1' 로 점검했을 때 걸리는 시간을 비교한다. 발표 자료의 지표 표가 여기서 나온다.
//   실행: crash_engine bench data/samples/s1_anticheat_crash.json ...

#include "ce/bench.hpp"

#include <cmath>
#include <cstdio>
#include <iostream>
#include <sstream>

namespace ce {

bool simulateToTruth(const Plan& plan, const std::string& truthCandidateId, int* trials,
                     double* cost) {
  double acc = 0;
  int n = 0;
  for (std::size_t i = 0; i < plan.steps.size(); i++) {
    const CheckInstance& c = plan.steps[i].check;
    n++;
    acc += c.cost_seconds;
    for (std::size_t k = 0; k < c.covers.size(); k++) {
      if (c.covers[k] == truthCandidateId) {
        if (trials) *trials = n;
        if (cost) *cost = acc;
        return true;
      }
    }
  }
  if (trials) *trials = n;
  if (cost) *cost = acc;
  return false;
}

BenchSummary runBench(const std::vector<std::string>& paths, const Rules& rules) {
  BenchSummary sum;
  sum.unresolved_minutes = rules.unresolved_cost_minutes;
  int layerOk = 0;
  int top1 = 0;
  int top3 = 0;
  ExecutionPolicy guide;

  for (std::size_t p = 0; p < paths.size(); p++) {
    Snapshot s;
    std::vector<std::string> warn;
    std::string err;
    if (!loadSnapshotFile(paths[p], &s, &warn, &err)) {
      std::cerr << "[bench] 읽기 실패: " << paths[p] << " (" << err << ")\n";
      continue;
    }
    if (!s.has_ground_truth) {
      std::cerr << "[bench] ground_truth 없음, 건너뜀: " << paths[p] << "\n";
      continue;
    }

    const Diagnosis dV1 = diagnose(s, rules, EngineVersion::V1, guide, warn);
    const Diagnosis dV0 = diagnose(s, rules, EngineVersion::V0, guide, warn);

    BenchRow row;
    row.snapshot_id = s.snapshot_id;
    row.truth = toString(s.truth_layer) + "/" + s.truth_category;
    if (!s.truth_subject.empty()) row.truth += "/" + s.truth_subject;
    row.stage = rules.stageOf(s.truth_category);
    row.layer_correct = (dV1.layer.top == s.truth_layer);

    std::string truthId;
    for (std::size_t i = 0; i < dV1.candidates.size(); i++) {
      if (matchesGroundTruth(dV1.candidates[i], s)) {
        row.rank = static_cast<int>(i) + 1;
        truthId = dV1.candidates[i].id;
        break;
      }
    }

    if (!truthId.empty()) {
      row.reached_v1 = simulateToTruth(dV1.plan, truthId, &row.trials_v1, &row.cost_v1);
      row.reached_v0 = simulateToTruth(dV0.plan, truthId, &row.trials_v0, &row.cost_v0);
    }
    {
      const Plan base = buildBaselinePlan(dV1.candidates, rules, dV1.symptom);  // 기준 순서는 층 생략을 모른다
      if (!truthId.empty()) {
        row.reached_base = simulateToTruth(base, truthId, &row.trials_base, &row.cost_base);
      }
      row.exp_base = base.expected_total_seconds;
      row.exp_v0 = dV0.plan.expected_total_seconds;
      row.exp_v1 = dV1.plan.expected_total_seconds;
    }

    if (row.layer_correct) layerOk++;
    if (row.rank == 1) top1++;
    if (row.rank >= 1 && row.rank <= 3) top3++;
    sum.n_stage[row.stage] += 1;
    if (row.rank == 1) sum.top1_stage[row.stage] += 1;

    sum.avg_trials_v0 += row.trials_v0;
    sum.avg_trials_v1 += row.trials_v1;
    sum.avg_trials_base += row.trials_base;
    sum.avg_cost_v0 += row.cost_v0;
    sum.avg_cost_v1 += row.cost_v1;
    sum.avg_cost_base += row.cost_base;
    sum.avg_exp_v0 += row.exp_v0;
    sum.avg_exp_v1 += row.exp_v1;
    sum.avg_exp_base += row.exp_base;
    sum.rows.push_back(row);
    sum.n++;
  }

  if (sum.n > 0) {
    const double n = sum.n;
    sum.layer_accuracy = layerOk / n;
    sum.top1 = top1 / n;
    sum.top3 = top3 / n;
    sum.avg_trials_v0 /= n;
    sum.avg_trials_v1 /= n;
    sum.avg_trials_base /= n;
    sum.avg_cost_v0 /= n;
    sum.avg_cost_v1 /= n;
    sum.avg_cost_base /= n;
    sum.avg_exp_v0 /= n;
    sum.avg_exp_v1 /= n;
    sum.avg_exp_base /= n;
  }
  return sum;
}

namespace {
std::string minutesText(double sec) {
  std::ostringstream os;
  os.precision(1);
  os << std::fixed << sec / 60.0 << "분";
  return os.str();
}
}  // namespace

std::string BenchSummary::toText() const {
  std::ostringstream os;
  os << "=== 검증 지표 (시나리오 " << n << "건) ===\n";
  os << "층 판정 정확도   : " << percentText(layer_accuracy) << "\n";
  os << "원인 Top-1       : " << percentText(top1) << "\n";
  os << "원인 Top-3       : " << percentText(top3) << "\n";
  const char* stageNames[4] = {"범위 밖", "1차 보안 프로그램", "2차 드라이버·시스템", "3차 하드웨어"};
  for (int st = 1; st <= 3; st++) {
    if (n_stage[st] == 0) continue;
    os << "  " << stageNames[st] << " : 1순위 정답 " << top1_stage[st] << "/" << n_stage[st] << "건\n";
  }
  char trials[120];
  std::snprintf(trials, sizeof(trials), "기준 %.1f회 / v0 %.1f회 / v1 %.1f회", avg_trials_base,
                avg_trials_v0, avg_trials_v1);
  os << "평균 시도 횟수   : " << trials << "\n";
  os << "평균 누적 비용   : 기준 " << minutesText(avg_cost_base) << " / v0 "
     << minutesText(avg_cost_v0) << " / v1 " << minutesText(avg_cost_v1);
  if (avg_cost_base > 0) {
    os << "  (v1 감소율 " << percentText(1.0 - avg_cost_v1 / avg_cost_base) << ")";
  }
  os << "\n";
  os << "평균 기대 비용   : 기준 " << minutesText(avg_exp_base) << " / v0 " << minutesText(avg_exp_v0)
     << " / v1 " << minutesText(avg_exp_v1) << "\n"
     << "                   (엔진 후보 확률 기준. 원인을 못 찾는 경우는 " << unresolved_minutes
     << "분으로 계산. v1이 최소화하는 값)";
  os << "\n\n시나리오별\n";
  for (std::size_t i = 0; i < rows.size(); i++) {
    const BenchRow& r = rows[i];
    os << " - " << r.snapshot_id << (r.stage > 0 ? " [" + std::to_string(r.stage) + "차]" : std::string(" [범위 밖]")) << " | 정답 " << r.truth
       << " | 층 " << (r.layer_correct ? "O" : "X") << " | 순위 "
       << (r.rank > 0 ? std::to_string(r.rank) : "-") << " | 시도(기준/v0/v1) " << r.trials_base
       << "/" << r.trials_v0 << "/" << r.trials_v1 << " | 비용(기준/v0/v1) "
       << minutesText(r.cost_base) << "/" << minutesText(r.cost_v0) << "/"
       << minutesText(r.cost_v1) << "\n";
  }
  os << "\n* 시도 횟수·누적 비용은 점검 신뢰도를 1로 가정한 측정값입니다(정답을 덮는 점검에 도달할 때까지).\n";
  os << "* 1순위가 정답인 시나리오에서는 1순위부터 바로 확인하는 v0 가 유리할 수 있습니다. v1 은 1순위가 틀릴 "
        "가능성까지 고려해 '기대' 비용을 줄이므로, 실제 사례가 쌓인 뒤 두 값을 함께 비교해야 합니다.\n";
  return os.str();
}

json::Value BenchSummary::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("n", json::Value(n));
  json::Value stages = json::Value::makeArray();
  for (int st = 0; st <= 3; st++) {
    json::Value x = json::Value::makeObject();
    x.set("stage", json::Value(st));
    x.set("n", json::Value(n_stage[st]));
    x.set("top1", json::Value(top1_stage[st]));
    stages.push(x);
  }
  o.set("by_stage", stages);
  o.set("layer_accuracy", json::Value(layer_accuracy));
  o.set("top1", json::Value(top1));
  o.set("top3", json::Value(top3));
  json::Value t = json::Value::makeObject();
  t.set("baseline", json::Value(avg_trials_base));
  t.set("v0", json::Value(avg_trials_v0));
  t.set("v1", json::Value(avg_trials_v1));
  o.set("avg_trials", t);
  json::Value c = json::Value::makeObject();
  c.set("baseline", json::Value(avg_cost_base));
  c.set("v0", json::Value(avg_cost_v0));
  c.set("v1", json::Value(avg_cost_v1));
  o.set("avg_cost_seconds", c);
  json::Value e = json::Value::makeObject();
  e.set("baseline", json::Value(avg_exp_base));
  e.set("v0", json::Value(avg_exp_v0));
  e.set("v1", json::Value(avg_exp_v1));
  o.set("avg_expected_cost_seconds", e);
  o.set("unresolved_cost_minutes", json::Value(unresolved_minutes));
  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < rows.size(); i++) {
    const BenchRow& r = rows[i];
    json::Value x = json::Value::makeObject();
    x.set("snapshot_id", json::Value(r.snapshot_id));
    x.set("truth", json::Value(r.truth));
    x.set("stage", json::Value(r.stage));
    x.set("layer_correct", json::Value(r.layer_correct));
    x.set("rank", json::Value(r.rank));
    x.set("trials_baseline", json::Value(r.trials_base));
    x.set("trials_v0", json::Value(r.trials_v0));
    x.set("trials_v1", json::Value(r.trials_v1));
    x.set("cost_baseline", json::Value(r.cost_base));
    x.set("cost_v0", json::Value(r.cost_v0));
    x.set("cost_v1", json::Value(r.cost_v1));
    x.set("expected_baseline", json::Value(r.exp_base));
    x.set("expected_v0", json::Value(r.exp_v0));
    x.set("expected_v1", json::Value(r.exp_v1));
    arr.push(x);
  }
  o.set("rows", arr);
  return o;
}

}  // namespace ce
