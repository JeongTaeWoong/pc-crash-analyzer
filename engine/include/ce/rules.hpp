// rules.hpp - 추론에 쓰이는 모든 상수(사전 가중치·상관 가중치·점검 비용·조치·사례 DB)
// 코드에 숫자를 박지 않고 JSON으로 분리해, 실험으로 값을 갱신하고 근거 표를 공개할 수 있게 한다.
#pragma once
#include <map>
#include <string>
#include <vector>

#include "ce/json.hpp"
#include "ce/snapshot.hpp"

namespace ce {

// 프로세스/드라이버 이름 -> 원인 카테고리 매핑 규칙
struct CategoryHint {
  std::string match;     // 소문자 부분 일치 문자열
  std::string category;  // anticheat / antivirus / fin_security / overlay ...
  std::string label;     // 사용자에게 보여줄 이름 (비면 원래 이름 사용)
};

// 조치 1건. 점검(원인 확인)과 해결 안내가 모두 이 단위를 쓴다.
// 엔진은 조치를 '직접 실행하지 않는다'. 무엇을, 어떤 순서로, 자동으로 해도 되는지만 판단하고
// 실제 실행은 수집기/에이전트 쪽 실행기가 맡는다(자동 점검 모드).
struct ActionTemplate {
  std::string id;
  std::string label;
  std::string kind;  // toggle_setting | stop_process | stop_service | exclude_path |
                     // firewall_allow | update_program | reinstall | uninstall |
                     // driver_rollback | os_update_remove | physical | inspect
  std::vector<std::string> steps;  // 사용자 안내 단계(수동 모드에서 그대로 보여 줌)
  std::string rollback;            // 되돌리는 방법 (비면 되돌릴 수 없음)
  bool reversible = false;         // 원상복구가 가능한가
  bool requires_reboot = false;
  std::string risk = "low";        // low | medium | high
  std::string automation;          // 실행기용 명령 템플릿 예: "stop_service:{subject}"
  bool auto_default = false;       // 사용자가 허용 목록을 따로 주지 않았을 때 자동 실행 허용 여부
};

// 점검 행위 1개. 커버 집합(어떤 후보를 걸러내는가)과 비용·신뢰도를 갖는다.
struct CheckTemplate {
  std::string id;
  std::string label;
  std::string advice;                   // 사용자 안내 문구
  std::string action;                   // 실제로 수행할 조치 id (actions 목록)
  double cost_seconds = 60;             // 점검 비용(시간). 비용 가중 그룹 테스팅의 핵심 입력
  double reliability = 0.85;            // 이 점검이 원인을 제대로 드러낼 확률
  bool group = false;                   // true면 조건에 맞는 후보를 한 번에 묶어 검사
  std::vector<std::string> categories;  // 적용 카테고리(비면 layers로 판단)
  std::vector<std::string> layers;      // 적용 층(SW/DRIVER/HW)
  std::vector<std::string> symptoms;    // 이 증상일 때만 쓴다 (비면 모든 증상)
  bool appliesTo(const std::string& category, Layer layer) const;
  bool usableFor(const std::string& symptom) const;
};

// 원인이 확인된 뒤 보여 줄 '해결' 방법. 점검(일시 중지)과 달리 문제를 없애는 조치다.
struct RemedyTemplate {
  std::string id;
  std::string label;
  std::string description;              // 왜 이 방법이 해결이 되는가
  std::vector<std::string> categories;  // 적용 카테고리
  std::vector<std::string> subjects;    // 특정 대상에만 적용 (비면 카테고리 전체)
  std::vector<std::string> symptoms;    // 특정 증상에만 적용 (비면 전체)
  std::vector<std::string> actions;     // 수행할 조치 id 순서
  double success_seed = 0.6;            // 이 해결책이 통하는 비율(시드값, 사례로 갱신)
  bool appliesTo(const std::string& category, const std::string& subject,
                 const std::string& symptom) const;
};

// 지원 범위 단계. 멘토링 1차 3.3 의 확장 순서를 그대로 옮긴 것:
//   1차 보안 프로그램 충돌 → 2차 드라이버·시스템 → 3차 하드웨어
// 어느 단계에도 속하지 않는 카테고리(오버레이·입력 유틸 등 일반 소프트웨어)는 stage 0(이후 확장)이다.
struct ScopeStage {
  int stage = 0;
  std::string name;
  std::vector<std::string> categories;
};

// 사례 DB 항목. 피드백으로 갱신되며 집단 통계의 자리를 차지한다(본 학기: 시드 + 로컬 피드백).
struct CaseEntry {
  std::string symptom;   // app_crash ...
  std::string category;  // anticheat ...
  std::string subject;   // vgk.sys (비면 카테고리 전체에 적용)
  double weight = 0.5;   // 0~1
  int solved = 0;        // '해결됨' 피드백 수
  int rejected = 0;      // '아님' 피드백 수
};

struct Rules {
  int version = 2;

  // 층 판정 가중치 (이름 -> 값). layer.cpp 가 이름으로 꺼내 쓴다.
  std::map<std::string, double> layer_weights;
  // 증상 -> 카테고리 -> 사전 확률(상대 가중치)
  std::map<std::string, std::map<std::string, double> > priors;

  double w_prior = 1.0;                   // 사전 가중치 반영 비율
  double w_time = 0.9;                    // 시간 상관
  double w_coexist = 1.0;                 // 공존 상관
  double w_case = 0.6;                    // 사례 DB 일치
  double w_layer = 1.1;                   // 층 판정 결과 반영
  double time_window_minutes = 4320;      // 변경-증상 시간 상관 창(기본 3일)
  double time_full_credit_minutes = 120;  // 이 안쪽은 만점

  // 지원 범위 단계 (1차 보안 → 2차 드라이버·시스템 → 3차 하드웨어)
  std::vector<ScopeStage> scopes;

  // 세션(확인 → 안내 → 해결 → 검증)
  double skip_layer_threshold = 0.15;  // 이 확률 미만인 층은 점검 생략 권고
  double confirm_threshold = 0.85;     // 그룹 점검 후 한 후보의 확률이 이 값을 넘으면 원인 확정
  double verify_min_observe_minutes = 60;  // 해결 후 이만큼 재발이 없어야 '해결됨'
  // 점검을 다 해도 원인을 못 찾았을 때 드는 비용(수리점·전문가 점검 등, 팀 추정).
  // 순서 계산과 기대 비용 비교에서 '못 찾은 경우'를 공평하게 세려고 쓴다.
  double unresolved_cost_minutes = 120;

  std::vector<CategoryHint> hints;
  std::vector<ActionTemplate> actions;
  std::vector<CheckTemplate> checks;
  std::vector<RemedyTemplate> remedies;
  std::vector<CaseEntry> cases;
  std::vector<std::string> baseline_checklist;  // 비교 기준(커뮤니티 표준 순서)

  double layerWeight(const std::string& name, double def) const;
  double prior(const std::string& symptom, const std::string& category) const;
  int findCaseIndex(const std::string& symptom, const std::string& category,
                    const std::string& subject) const;  // 없으면 -1
  const CheckTemplate* findCheck(const std::string& id) const;
  const ActionTemplate* findAction(const std::string& id) const;
  int stageOf(const std::string& category) const;  // 범위 밖이면 0
  std::string stageName(int stage) const;           // "보안 프로그램 충돌" 등
  bool inFocus(const std::string& category) const;  // stageOf() > 0
  // haystack(이름+제조사 등 검색 대상) 에서 카테고리를 찾고, display 를 바탕으로 표시명을 만든다.
  // 매칭 실패 시 fallback 카테고리와 display 를 그대로 돌려준다.
  void classifyName(const std::string& haystack, const std::string& display,
                    const std::string& fallback, std::string* category, std::string* label) const;

  static Rules defaults();
  static Rules fromJson(const json::Value& v);
  json::Value toJson() const;
  static bool load(const std::string& path, Rules* out, std::string* error);
  bool save(const std::string& path, std::string* error) const;
};

// 카테고리가 속한 층
Layer layerOfCategory(const std::string& category);

// 이름 비교용 정규화: 소문자 + 확장자/공백/기호 제거.
// "vgk.sys", "Riot Vanguard" 처럼 표기가 달라도 같은 대상인지 확인할 때 쓴다.
std::string normalizeName(const std::string& s);
bool sameSubject(const std::string& a, const std::string& b);

}  // namespace ce
