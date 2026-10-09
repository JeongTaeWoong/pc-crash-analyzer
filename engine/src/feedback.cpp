// feedback.cpp - '해결됨 / 아님' 결과로 사례 DB(rules.json 의 cases)를 갱신한다.
//   [팀원용] 해결됨이면 그 (증상, 종류, 대상)의 가중치를 올리고, 아님이면 내린다.
//   다음 진단부터는 이 값이 후보 점수의 '사례 DB' 성분으로 들어간다(사용할수록 정확해지는 부분).

#include "ce/feedback.hpp"

#include <algorithm>
#include <cstdio>

namespace ce {

FeedbackResult applyFeedback(Rules* rules, const std::string& symptom, const Candidate& target,
                             bool solved) {
  FeedbackResult out;

  int entryIdx = -1;
  for (std::size_t i = 0; i < rules->cases.size(); i++) {
    const CaseEntry& c = rules->cases[i];
    if (c.symptom == symptom && c.category == target.category &&
        equalsNoCase(c.subject, target.subject)) {
      entryIdx = static_cast<int>(i);
      break;
    }
  }
  if (entryIdx < 0) {
    CaseEntry fresh;
    fresh.symptom = symptom;
    fresh.category = target.category;
    fresh.subject = target.subject;
    // 시작값은 같은 카테고리의 일반 항목이 있으면 그 값을, 없으면 중립(0.5)
    const int generic = rules->findCaseIndex(symptom, target.category, "");
    if (generic >= 0) fresh.weight = rules->cases[generic].weight;
    rules->cases.push_back(fresh);
    entryIdx = static_cast<int>(rules->cases.size()) - 1;
  }

  CaseEntry& entry = rules->cases[entryIdx];
  out.weight_before = entry.weight;
  if (solved) {
    entry.solved += 1;
    entry.weight = std::min(0.95, entry.weight + 0.10 * (1.0 - entry.weight) * 2.0);
  } else {
    entry.rejected += 1;
    entry.weight = std::max(0.05, entry.weight - 0.12 * entry.weight);
  }
  out.weight_after = entry.weight;
  out.ok = true;

  char buf[64];
  std::snprintf(buf, sizeof(buf), "%.3f → %.3f", out.weight_before, out.weight_after);
  out.message = std::string(solved ? "해결됨" : "아님") + " 피드백을 반영했습니다: " + target.label +
                " (" + symptom + "/" + target.category + ") 가중치 " + buf;
  return out;
}

}  // namespace ce
