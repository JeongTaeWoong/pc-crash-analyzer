// engine.hpp - 추론 엔진 진입점. 스냅샷 하나를 받아 진단 결과를 만든다.
// 파이프라인: 층 판정 -> 후보 생성·점수화 -> 점검 순서 계산 -> 조치 실행 방식 결정
#pragma once//
// ─────────────────────────────────────────────────────────────────────────────
// [팀원용] 코드 읽는 순서
//   이 엔진은 "스냅샷(JSON) 1개 → 진단 결과"를 만드는 프로그램이다. 아래 순서로 읽으면 흐름이 잡힌다.
//
//   1. snapshot.cpp   수집기가 준 JSON을 C++ 구조체(Snapshot)로 바꾼다.
//   2. layer.cpp      [1단계] 소프트웨어 / 드라이버 / 하드웨어 중 어디 문제인지 점수로 판정한다.
//   3. candidates.cpp [2단계] "범인 후보"(Vanguard, 백신, 오버레이 ...)를 모으고 점수를 매긴다.
//   4. planner.cpp    [3단계] 어떤 순서로 점검하면 가장 빨리 범인을 찾는지 계산한다.
//   5. remedy.cpp     [4단계] 각 조치를 자동으로 해도 되는지(auto/confirm/manual) 정한다.
//   6. engine.cpp     위 1~4단계를 순서대로 부르는 곳(diagnose 함수). 전체 흐름은 여기서 보면 된다.
//   7. session.cpp    점검 결과를 하나씩 받아 "원인 확정 → 해결 안내 → 해결 검증"까지 이어 간다.
//   8. tool.cpp       LLM 에이전트(LangGraph 등)가 엔진을 부를 수 있게 JSON 도구로 감싼다.
//   9. main.cpp       명령줄 프로그램(crash_engine.exe)의 진입점.
//
//   숫자(가중치·비용 등)는 코드에 없고 전부 data/rules.json 에 있다. 값을 바꾸고 싶으면 그 파일을 고친다.
//   예시로 계속 쓰는 시나리오: data/samples/s1_anticheat_crash.json
//     (발로란트용 커널 안티치트 Riot Vanguard 때문에 다른 게임 othergame.exe 가 튕기는 상황)
// ─────────────────────────────────────────────────────────────────────────────

#include <string>
#include <vector>

#include "ce/candidates.hpp"
#include "ce/layer.hpp"
#include "ce/planner.hpp"
#include "ce/remedy.hpp"
#include "ce/rules.hpp"
#include "ce/snapshot.hpp"

namespace ce {

struct Diagnosis {
  std::string snapshot_id;
  std::string symptom;
  std::string target_process;
  int tz_offset_minutes = 0;
  LayerResult layer;
  std::vector<Candidate> candidates;
  std::vector<Candidate> planning;  // 점검 순서 계산에 실제로 쓴 후보(생략 층 제외, 재정규화)
  Plan plan;
  std::vector<ActionDecision> step_actions;  // plan.steps 와 같은 순서
  ExecutionPolicy policy;
  std::vector<std::string> skipped;   // 생략을 안내한 층
  std::vector<Layer> skipped_layers;
  std::vector<std::string> notes;     // 핀포인트 범위 안내 등
  std::vector<std::string> warnings;  // 수집 누락 등 '확인 불가' 안내

  const Candidate* top() const { return candidates.empty() ? nullptr : &candidates[0]; }
  json::Value toJson() const;
  std::string toText() const;  // 콘솔·UI 확인용 한국어 리포트
};

Diagnosis diagnose(const Snapshot& s, const Rules& r, EngineVersion version,
                   const ExecutionPolicy& policy, const std::vector<std::string>& parseWarnings);

// 후보가 정답(ground_truth)과 같은 원인을 가리키는지 판정 (벤치마크용)
bool matchesGroundTruth(const Candidate& c, const Snapshot& s);

// 리포트용 표기 도우미
std::string percentText(double x);
std::string durationText(double seconds);

}  // namespace ce
