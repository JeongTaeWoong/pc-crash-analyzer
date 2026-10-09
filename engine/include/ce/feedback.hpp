// feedback.hpp - '해결됨 / 아님' 피드백을 사례 DB에 반영한다 (제안서 5.3 피드백 학습)
#pragma once
#include <string>

#include "ce/candidates.hpp"
#include "ce/rules.hpp"

namespace ce {

struct FeedbackResult {
  bool ok = false;
  std::string message;
  double weight_before = 0;
  double weight_after = 0;
};

// target 후보에 대해 (증상, 카테고리, 대상) 사례 항목을 찾아(없으면 만들어) 가중치를 갱신한다.
FeedbackResult applyFeedback(Rules* rules, const std::string& symptom, const Candidate& target,
                             bool solved);

}  // namespace ce
