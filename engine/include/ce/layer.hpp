// layer.hpp - 진단의 1단계: '어느 층의 문제인가'(SW / 드라이버 / HW) 판정
// 제안서 5.3 층 판정 규칙을 가중치 기반 점수로 구현한다.
#pragma once
#include <vector>

#include "ce/evidence.hpp"
#include "ce/rules.hpp"
#include "ce/snapshot.hpp"

namespace ce {

struct LayerResult {
  double raw_sw = 0, raw_driver = 0, raw_hw = 0;
  double p_sw = 0, p_driver = 0, p_hw = 0;  // 합이 1이 되도록 정규화
  Layer top = Layer::Unknown;
  double confidence = 0;  // 1위와 2위의 차이 + 수집 가용성으로 산출
  std::vector<Evidence> evidence;

  double probabilityOf(Layer l) const;
  json::Value toJson(int tzOffsetMinutes) const;
};

// 수집 가용성 계수(0.55~1.0). 근거를 볼 수 없었던 항목이 많을수록 낮다.
double availabilityFactor(const Snapshot& s);

LayerResult classifyLayer(const Snapshot& s, const Rules& r);

}  // namespace ce
