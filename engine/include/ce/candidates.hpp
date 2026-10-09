// candidates.hpp - 진단의 2단계: 원인 후보 생성과 점수화
// 점수 = 사전 가중치(증상별) + 시간 상관 + 공존 상관 + 사례 DB 일치, 층 판정 결과로 보정
#pragma once
#include <string>
#include <vector>

#include "ce/evidence.hpp"
#include "ce/layer.hpp"
#include "ce/rules.hpp"
#include "ce/snapshot.hpp"

namespace ce {

struct Candidate {
  std::string id;        // "anticheat:vgk.sys" 형태의 안정적인 키(피드백·세션에서 사용)
  std::string subject;   // vgk.sys
  std::string label;     // 사용자에게 보여줄 이름
  std::string product;   // 제품 이름 (Riot Vanguard / AhnLab V3). 실행기 명령에 쓴다
  std::string category;  // anticheat / thermal / ...
  Layer layer = Layer::Unknown;
  bool in_focus = true;  // 1차 핀포인트 범위(보안 프로그램 충돌) 안의 후보인가

  double s_prior = 0, s_time = 0, s_coexist = 0, s_case = 0;  // 성분별 원점수
  double layer_gate = 1;   // 층 판정 보정 계수
  double score = 0;        // 최종 점수
  double probability = 0;  // 후보들 사이에서 정규화한 확률
  double confidence = 0;   // 근거의 양과 수집 가용성으로 산출

  std::vector<Evidence> evidence;
  json::Value toJson(int tzOffsetMinutes) const;
};

// 스냅샷에서 후보를 만들고(공존·시간 상관 근거 포함) 점수를 매겨 내림차순으로 돌려준다.
std::vector<Candidate> buildCandidates(const Snapshot& s, const Rules& r, const LayerResult& layer);

// id 로 후보를 찾는다. 없으면 -1
int findCandidate(const std::vector<Candidate>& list, const std::string& id);

}  // namespace ce
