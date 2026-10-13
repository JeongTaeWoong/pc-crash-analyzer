// remedy.hpp - 조치 결정: '무엇을 할지'와 '자동으로 해도 되는지'를 정한다.
//
// 멘토링 1차 반영:
//   * 기본은 '안내 모드'(guide) — 엔진은 조치를 실행하지 않고 사용자에게 단계를 보여 준다.
//   * '자동 점검 모드'(auto)는 선택 옵션 — 되돌릴 수 있고(reversible) 사용자가 미리 허용한 조치만
//     실행기(수집기/에이전트 쪽)가 자동으로 수행하고, 되돌리기 어려운 조치는 항상 확인을 거친다.
// 엔진은 판단만 하고, 실제 실행과 원상복구 지점 기록은 실행기가 맡는다.
#pragma once
#include <string>
#include <vector>

#include "ce/candidates.hpp"
#include "ce/rules.hpp"

namespace ce {

enum class ExecutionMode { Guide, Auto };
std::string toString(ExecutionMode m);
ExecutionMode executionModeFromString(const std::string& s, bool* ok);

// 사용자 설정: 자동 모드에서 허용할 조치 id 목록. 비어 있으면 규칙의 auto_default 를 따른다.
struct ExecutionPolicy {
  ExecutionMode mode = ExecutionMode::Guide;
  std::vector<std::string> allowed_actions;
  json::Value toJson() const;
  static ExecutionPolicy fromJson(const json::Value& v);
};

struct ActionDecision {
  std::string action_id;
  std::string label;
  std::string kind;
  std::vector<std::string> steps;
  std::string rollback;
  bool reversible = false;
  bool requires_reboot = false;
  std::string risk;
  std::string automation;  // {subject} 등 자리표시자를 채운 실행기용 명령 (비면 사람이 해야 함)
  std::string execution;   // guide | auto | confirm | manual
  std::string reason;      // 왜 그렇게 정했는가
  json::Value toJson() const;
};

struct RemedyOption {
  std::string remedy_id;
  std::string label;
  std::string description;
  double success = 0;  // 기대 해결률(시드값)
  std::vector<ActionDecision> actions;
  json::Value toJson() const;
};

// 조치가 겨냥하는 대상. 실행기 명령의 자리표시자 {subject} {product} {target_process} 를 채운다.
struct ActionTarget {
  std::string subject;         // 파일·드라이버·서비스 이름 (vgk.sys)
  std::string product;         // 제품 이름 (Riot Vanguard)
  std::string target_process;  // 증상이 난 프로그램 (othergame.exe)
};
ActionTarget actionTargetOf(const Candidate& c, const std::string& targetProcess);

ActionDecision decideAction(const Rules& r, const std::string& actionId,
                            const ExecutionPolicy& policy, const ActionTarget& target);

// 원인이 확인된 후보에 대해 적용할 해결책 목록(기대 해결률 높은 순)
std::vector<RemedyOption> remediesFor(const Rules& r, const Candidate& c, const std::string& symptom,
                                      const ExecutionPolicy& policy,
                                      const std::string& targetProcess);

}  // namespace ce
