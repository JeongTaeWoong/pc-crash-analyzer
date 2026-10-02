// planner.hpp - 진단의 3단계: 점검(원인 확인) 순서 최적화
//   v0 : 후보 점수 내림차순 (중간발표용 단순 가중합)
//   v1 : 비용 가중 적응형 그룹 테스팅 — 한 번의 점검으로 걸러지는 후보 집합의 크기와
//        점검 비용을 함께 보고, 기대 비용이 가장 적은 순서를 계산한다.
// 세션(session.cpp)은 같은 함수(chooseNextCheck, bayesUpdate)를 써서 '실제 결과'를 반영하며
// 한 단계씩 다음 점검을 고른다.
#pragma once
#include <map>
#include <string>
#include <vector>

#include "ce/candidates.hpp"
#include "ce/rules.hpp"

namespace ce {

enum class EngineVersion { V0, V1 };
std::string toString(EngineVersion v);
EngineVersion engineVersionFromString(const std::string& s, bool* ok);

// 점검 1건의 실체 (템플릿 + 이번 진단에서 실제로 커버하는 후보 집합)
struct CheckInstance {
  std::string check_id;   // 개별 점검이면 "템플릿id@후보id", 그룹이면 템플릿 id
  std::string template_id;
  std::string label;
  std::string advice;
  std::string action_id;  // 수행할 조치 (rules.actions)
  double cost_seconds = 60;
  double reliability = 0.85;
  bool group = false;
  std::vector<std::string> covers;  // 후보 id 목록
  std::string covers_label;         // "Riot Vanguard" 또는 "후보 5건"
};

struct PlanStep {
  CheckInstance check;
  double reach_probability = 1;    // 앞 단계들이 모두 '아님'이어서 여기까지 올 확률
  double success_probability = 0;  // 이 단계에서 원인이 드러날 확률
  double covered_probability = 0;  // 커버 집합이 가진 사전 확률 합
  json::Value toJson() const;
};

struct Plan {
  EngineVersion version = EngineVersion::V1;
  std::vector<PlanStep> steps;
  double expected_cost_seconds = 0;  // 원인에 도달할 때까지의 기대 누적 비용
  double expected_trials = 0;        // 기대 시도 횟수
  double resolved_probability = 0;   // 이 순서로 원인에 도달할 확률
  json::Value toJson() const;
};

// 후보에 적용 가능한 점검 인스턴스를 만든다(그룹 점검 포함).
std::vector<CheckInstance> buildCheckInstances(const std::vector<Candidate>& candidates,
                                               const Rules& r);

// 순서 계산에서 제외할 후보(생략 권고 층, 확률이 지나치게 낮은 후보)를 걸러내고 확률을 재정규화한다.
std::vector<Candidate> selectPlanningCandidates(const std::vector<Candidate>& all,
                                                const std::vector<Layer>& skippedLayers,
                                                double minProbability);

// 점검 결과에 따른 베이즈 갱신.
// 원인이 커버 집합 안에 있으면 확률 r 로 '증상이 사라짐(positive)'이 관측된다고 본다.
std::vector<double> bayesUpdate(const std::vector<double>& belief, const std::vector<char>& inSet,
                                double reliability, bool positive);

// 커버 집합을 belief 인덱스 기준 0/1 배열로 바꾼다.
std::vector<char> coverMask(const CheckInstance& inst, const std::map<std::string, int>& index,
                            std::size_t n);

// v1의 한 단계: 아직 쓰지 않은 점검 중 '이 점검 비용 + 결과별 이후 기대 비용'이 가장 작은 것.
// 고를 점검이 없으면 -1.
int chooseNextCheck(const std::vector<double>& belief, const std::vector<CheckInstance>& instances,
                    const std::map<std::string, int>& index, const std::vector<char>& used);

// 주어진 순서대로 점검할 때의 기대 비용·시도 횟수를 계산한다.
// 누적 해결 확률이 stopResolved 를 넘거나 maxSteps 에 도달하면 안내를 끊는다.
Plan evaluateOrder(const std::vector<CheckInstance>& order, const std::vector<Candidate>& candidates,
                   EngineVersion version, std::size_t maxSteps, double stopResolved);

Plan buildPlan(const std::vector<Candidate>& candidates, const Rules& r, EngineVersion version);

// 비교 기준: 커뮤니티 표준 트러블슈팅 체크리스트 순서 (제안서 4.2)
Plan buildBaselinePlan(const std::vector<Candidate>& candidates, const Rules& r);

}  // namespace ce
