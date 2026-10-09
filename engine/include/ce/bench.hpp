// bench.hpp - 제안서 4.2 검증 지표 측정: 층 판정 정확도, Top-1/Top-3, 시도 횟수·비용 감소율
// 정답(ground_truth)이 들어 있는 시나리오 스냅샷에만 적용한다.
#pragma once
#include <string>
#include <vector>

#include "ce/engine.hpp"

namespace ce {

struct BenchRow {
  std::string snapshot_id;
  std::string truth;
  bool in_focus = false;  // 정답이 1차 핀포인트 범위(보안 프로그램) 안인가
  bool layer_correct = false;
  int rank = -1;  // 정답 후보의 순위(1부터). 못 찾으면 -1

  int trials_v0 = 0, trials_v1 = 0, trials_base = 0;
  double cost_v0 = 0, cost_v1 = 0, cost_base = 0;
  bool reached_v0 = false, reached_v1 = false, reached_base = false;
  // 엔진이 계산한 기대 비용(후보 확률 기준). v1 이 최소화하려는 값이다.
  double exp_v0 = 0, exp_v1 = 0, exp_base = 0;
};

struct BenchSummary {
  int n = 0;
  int n_focus = 0;
  double layer_accuracy = 0;
  double top1 = 0, top3 = 0;
  double focus_top1 = 0;  // 1차 범위(보안 프로그램) 시나리오만의 Top-1
  double avg_trials_v0 = 0, avg_trials_v1 = 0, avg_trials_base = 0;
  double avg_cost_v0 = 0, avg_cost_v1 = 0, avg_cost_base = 0;
  double avg_exp_v0 = 0, avg_exp_v1 = 0, avg_exp_base = 0;
  std::vector<BenchRow> rows;
  std::string toText() const;
  json::Value toJson() const;
};

// 순서대로 점검했을 때 '정답을 덮는 점검'에 몇 번째로 도달하는지 세어 본다.
// (측정 목적이므로 점검 신뢰도는 1로 가정하고, 그 가정을 리포트에 명시한다.)
bool simulateToTruth(const Plan& plan, const std::string& truthCandidateId, int* trials,
                     double* cost);

BenchSummary runBench(const std::vector<std::string>& snapshotPaths, const Rules& rules);

}  // namespace ce
