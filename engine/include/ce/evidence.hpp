// evidence.hpp - 판단의 근거 1건. UI의 '근거 그래프' 노드와 1:1로 대응한다.
#pragma once
#include <string>
#include <vector>

#include "ce/json.hpp"

namespace ce {

struct Evidence {
  std::string kind;         // prior | time_corr | coexist | case_db | event | sensor | layer | detection
  std::string detail;       // 사용자에게 보여줄 근거 문장
  double contribution = 0;  // 점수 기여분
  long long time = 0;       // 근거가 가리키는 시각(0이면 시각 없음)

  Evidence() {}
  Evidence(const std::string& k, const std::string& d, double c, long long t)
      : kind(k), detail(d), contribution(c), time(t) {}
  json::Value toJson(int tzOffsetMinutes) const;
};

json::Value evidenceListToJson(const std::vector<Evidence>& list, int tzOffsetMinutes);

}  // namespace ce
