// evidence.cpp - 근거(Evidence) 한 줄을 JSON 으로 바꾼다.
//   [팀원용] 근거 하나 = UI '근거 그래프'의 점 하나. kind(종류), detail(설명 문장), contribution(점수 기여), time(시각).

#include "ce/evidence.hpp"

namespace ce {

json::Value Evidence::toJson(int tzOffsetMinutes) const {
  json::Value o = json::Value::makeObject();
  o.set("kind", json::Value(kind));
  o.set("detail", json::Value(detail));
  o.set("contribution", json::Value(contribution));
  if (time != 0) o.set("time", json::Value(json::formatIso8601(time, tzOffsetMinutes)));
  return o;
}

json::Value evidenceListToJson(const std::vector<Evidence>& list, int tzOffsetMinutes) {
  json::Value arr = json::Value::makeArray();
  for (std::size_t i = 0; i < list.size(); i++) arr.push(list[i].toJson(tzOffsetMinutes));
  return arr;
}

}  // namespace ce
