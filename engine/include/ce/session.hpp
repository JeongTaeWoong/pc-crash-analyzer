// session.hpp - 진단 세션: 증상 → 원인 확인 → 해결 안내 → 실제 해결(검증)을 끝까지 이어 준다.
//
// 멘토링 1차 반영: "핀포인트 범위(보안 프로그램 충돌)에서 사례 1건을 끝까지 완성"하기 위한 흐름.
//
//   checking  : 점검을 하나씩 안내하고 결과(해결됨/아님)를 받아 후보 확률을 베이즈 갱신한다.
//   confirmed : 원인이 확인됨. 해결책(remedy)을 기대 해결률 순으로 안내한다.
//   fixing    : 사용자가(또는 자동 모드 실행기가) 해결책을 적용함. 검증을 기다린다.
//   resolved  : 사후 스냅샷(또는 사용자 응답)으로 재발이 없음을 확인. 사례 DB에 '해결됨' 반영.
//   exhausted : 점검을 다 했지만 원인을 특정하지 못함.
//   failed    : 원인은 확인했지만 등록된 해결책이 모두 실패.
//
// 세션 상태는 JSON 파일 하나로 저장한다. 엔진 프로세스는 매번 새로 떠도 되며(도구 호출 방식),
// 상태 파일에 원본 스냅샷과 지금까지의 결과(history)를 담아 두고 호출할 때마다 다시 계산한다.
#pragma once
#include <string>
#include <vector>

#include "ce/engine.hpp"
#include "ce/remedy.hpp"

namespace ce {

enum class SessionStatus { Checking, Confirmed, Fixing, Resolved, Exhausted, Failed };
std::string toString(SessionStatus s);
SessionStatus sessionStatusFromString(const std::string& s);
std::string statusText(SessionStatus s);  // 한국어 표기

struct HistoryItem {
  std::string type;    // check | confirm | fix_applied | verify
  std::string ref;     // check_id / candidate_id / remedy_id
  std::string result;  // solved | not_solved | skipped | resolved | recurred | observing
  std::string note;
  long long time = 0;
  json::Value toJson() const;
  static HistoryItem fromJson(const json::Value& v);
};

// 사후 검증 결과
struct VerifyResult {
  std::string outcome;         // resolved | recurred | observing | need_answer
  int recurrences = 0;         // 해결책 적용 이후 증상 신호 횟수
  double observed_minutes = 0; // 관찰한 시간
  bool target_ran = false;     // 대상 프로그램이 실제로 실행되었는가
  bool cause_still_present = false;  // 원인 대상이 여전히 상주·로드되어 있는가
  std::vector<std::string> details;
  std::string feedback_message;  // 사례 DB 반영 결과
  json::Value toJson() const;
};

class Session {
 public:
  // 새 세션. snapshotJson 은 수집기 스냅샷 원본(JSON 객체).
  static bool start(const json::Value& snapshotJson, const Rules& rules, EngineVersion version,
                    const ExecutionPolicy& policy, const std::string& rulesPath, Session* out,
                    std::string* error);
  // 저장된 상태에서 복원
  static bool fromJson(const json::Value& state, const Rules& rules, Session* out,
                       std::string* error);
  json::Value toJson() const;  // 저장용 상태

  // 지금 상황(다음 점검, 해결책, 확률 등)을 사용자·UI·에이전트가 읽기 좋은 형태로 돌려준다.
  json::Value view() const;
  std::string viewText() const;

  // 현재 안내한 점검의 결과를 받는다. result = solved | not_solved | skipped
  bool reportCheck(const std::string& checkId, const std::string& result, std::string* error);
  // 해결책을 적용했다고 기록한다(appliedAt = 0 이면 지금 시각).
  bool applyFix(const std::string& remedyId, long long appliedAt, std::string* error);
  // 사후 스냅샷으로 검증. rules 를 주면 '해결됨'일 때 사례 DB에 반영한다.
  bool verifyWithSnapshot(const json::Value& afterSnapshotJson, Rules* rulesForFeedback,
                          VerifyResult* out, std::string* error);
  // 사용자 응답으로 검증 (객관 신호가 없는 증상: 입력 누락 등)
  bool verifyWithAnswer(bool recurred, double observedMinutes, Rules* rulesForFeedback,
                        VerifyResult* out, std::string* error);

  SessionStatus status() const { return status_; }
  const Diagnosis& diagnosis() const { return diag_; }
  const std::string& rulesPath() const { return rules_path_; }
  std::string confirmedCandidateId() const { return confirmed_; }
  // 다음에 할 점검의 인덱스(instances_ 기준). 없으면 -1
  int nextCheckIndex() const;
  const std::vector<CheckInstance>& checkInstances() const { return instances_; }
  std::vector<RemedyOption> remedies() const;

 private:
  bool rebuild(std::string* error);  // 스냅샷·history 로부터 diag_/belief_/used_ 를 다시 계산
  void applyCheckResult(int instIdx, const std::string& result);
  int instanceIndexOf(const std::string& checkId) const;
  int topBeliefIndex() const;
  bool finishVerification(VerifyResult* r, Rules* rulesForFeedback, std::string* error);

  // 저장되는 상태
  json::Value snapshot_json_;
  EngineVersion version_ = EngineVersion::V1;
  ExecutionPolicy policy_;
  std::string rules_path_;
  long long created_at_ = 0;
  SessionStatus status_ = SessionStatus::Checking;
  std::vector<HistoryItem> history_;
  std::string confirmed_;    // 확인된 원인 후보 id
  std::string confirm_check_;  // 원인을 확인한 점검 id (되돌리기 안내용)
  int remedy_index_ = 0;     // 지금 안내 중인 해결책 순번
  std::string applied_remedy_;
  long long fix_applied_at_ = 0;
  json::Value last_verify_;

  // 계산되는 상태
  Rules rules_;
  Snapshot snapshot_;
  Diagnosis diag_;
  std::vector<CheckInstance> instances_;
  std::vector<double> belief_;  // diag_.planning 후보 순서
  std::vector<char> used_;
};

// 세션 파일 입출력 도우미
bool loadSessionFile(const std::string& path, const Rules& defaultRules, Session* out,
                     Rules* rulesOut, std::string* error);
bool saveSessionFile(const std::string& path, const Session& s, std::string* error);

}  // namespace ce
