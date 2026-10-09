#include "ce/session.hpp"

#include <cmath>
#include <ctime>
#include <map>
#include <sstream>

#include "ce/feedback.hpp"
#include "ce/signals.hpp"

namespace ce {

namespace {

const int kSessionVersion = 1;

long long nowEpoch() { return static_cast<long long>(std::time(nullptr)); }

std::string timeOrEmpty(long long t, int tz) {
  if (t == 0) return "";
  return json::formatIso8601(t, tz);
}

long long readTimeField(const json::Value& v) {
  if (v.isNumber()) return static_cast<long long>(v.asNumber());
  long long t = 0;
  if (v.isString() && json::parseIso8601(v.asString(), &t)) return t;
  return 0;
}

std::map<std::string, int> indexOf(const std::vector<Candidate>& list) {
  std::map<std::string, int> m;
  for (std::size_t i = 0; i < list.size(); i++) m[list[i].id] = static_cast<int>(i);
  return m;
}

// 원인 대상이 사후 스냅샷에도 여전히 상주·로드되어 있는가
bool subjectPresent(const Snapshot& s, const Candidate& c) {
  for (std::size_t i = 0; i < s.modules.size(); i++) {
    if (sameSubject(s.modules[i].name, c.subject)) return true;
  }
  for (std::size_t i = 0; i < s.drivers.size(); i++) {
    if (sameSubject(s.drivers[i].name, c.subject) || sameSubject(s.drivers[i].display, c.subject)) {
      return true;
    }
  }
  for (std::size_t i = 0; i < s.realtime_av.size(); i++) {
    if (sameSubject(s.realtime_av[i], c.subject)) return true;
  }
  const std::vector<std::string>* lists[3] = {&s.device_filters.keyboard_upper,
                                              &s.device_filters.mouse_upper,
                                              &s.device_filters.ndis_filters};
  for (int k = 0; k < 3; k++) {
    for (std::size_t i = 0; i < lists[k]->size(); i++) {
      if (sameSubject((*lists[k])[i], c.subject)) return true;
    }
  }
  return false;
}

bool targetRan(const Snapshot& s) {
  if (s.target_process.empty()) return true;  // 대상이 없는 증상(시스템 전체)은 실행 여부를 따지지 않는다
  if (s.processRunning(s.target_process)) return true;
  if (!s.moduleIndexesOf(s.target_process).empty()) return true;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    if (equalsNoCase(s.events[i].subject, s.target_process)) return true;
  }
  return false;
}

}  // namespace

// ─────────────────────────────────────────────────────────────────────────────
// [팀원용] 세션 = 진단을 '대화처럼' 한 단계씩 이어 가는 기능
//
//   상태 흐름
//     checking ──(개별 점검 '해결됨')──▶ confirmed ──(해결책 적용)──▶ fixing ──(검증: 재발 없음)──▶ resolved
//        │                                  ▲                            │
//        │(더 할 점검 없음)                  └──── (검증: 재발) 다음 해결책 ┘ (해결책을 다 써도 재발) ▶ failed
//        ▼
//     exhausted
//
//   s1 시연 순서
//     session start  → 첫 점검: Discord 오버레이 끄기
//     report ×3      → 오버레이 2개, Defender 모두 '아님' → Vanguard 79%
//     report         → Vanguard 중지 '해결됨' → confirmed (원인 확정)
//     apply-fix      → "안티치트를 해당 게임 할 때만 켜기" 적용 → fixing
//     verify         → 사후 스냅샷: 120분 동안 크래시 0건 → resolved, 사례 DB 갱신
//
//   저장 방식: 세션 파일에는 '원본 스냅샷 + 지금까지의 결과 목록(history)'만 저장한다.
//   불러올 때마다 rebuild() 가 진단을 다시 하고 history 를 처음부터 다시 적용해서 확률을 복원한다.
//   그래서 프로그램을 껐다 켜도, 에이전트가 매번 새 프로세스로 불러도 이어서 진행된다.
// ─────────────────────────────────────────────────────────────────────────────

// ---------------------------------------------------------------- 표기

std::string toString(SessionStatus s) {
  if (s == SessionStatus::Checking) return "checking";
  if (s == SessionStatus::Confirmed) return "confirmed";
  if (s == SessionStatus::Fixing) return "fixing";
  if (s == SessionStatus::Resolved) return "resolved";
  if (s == SessionStatus::Exhausted) return "exhausted";
  return "failed";
}

SessionStatus sessionStatusFromString(const std::string& s) {
  if (s == "confirmed") return SessionStatus::Confirmed;
  if (s == "fixing") return SessionStatus::Fixing;
  if (s == "resolved") return SessionStatus::Resolved;
  if (s == "exhausted") return SessionStatus::Exhausted;
  if (s == "failed") return SessionStatus::Failed;
  return SessionStatus::Checking;
}

std::string statusText(SessionStatus s) {
  if (s == SessionStatus::Checking) return "원인 확인 중";
  if (s == SessionStatus::Confirmed) return "원인 확인됨 · 해결 안내";
  if (s == SessionStatus::Fixing) return "해결책 적용됨 · 검증 대기";
  if (s == SessionStatus::Resolved) return "해결 완료";
  if (s == SessionStatus::Exhausted) return "원인 미특정";
  return "해결책 모두 실패";
}

json::Value HistoryItem::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("type", json::Value(type));
  o.set("ref", json::Value(ref));
  o.set("result", json::Value(result));
  if (!note.empty()) o.set("note", json::Value(note));
  if (time != 0) o.set("time", json::Value(json::formatIso8601(time, 0)));
  return o;
}

HistoryItem HistoryItem::fromJson(const json::Value& v) {
  HistoryItem h;
  h.type = v["type"].asString("");
  h.ref = v["ref"].asString("");
  h.result = v["result"].asString("");
  h.note = v["note"].asString("");
  h.time = readTimeField(v["time"]);
  return h;
}

json::Value VerifyResult::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("outcome", json::Value(outcome));
  o.set("recurrences", json::Value(recurrences));
  o.set("observed_minutes", json::Value(observed_minutes));
  o.set("target_ran", json::Value(target_ran));
  o.set("cause_still_present", json::Value(cause_still_present));
  json::Value d = json::Value::makeArray();
  for (std::size_t i = 0; i < details.size(); i++) d.push(json::Value(details[i]));
  o.set("details", d);
  if (!feedback_message.empty()) o.set("feedback", json::Value(feedback_message));
  return o;
}

// ---------------------------------------------------------------- 생성·복원

bool Session::start(const json::Value& snapshotJson, const Rules& rules, EngineVersion version,
                    const ExecutionPolicy& policy, const std::string& rulesPath, Session* out,
                    std::string* error) {
  if (!snapshotJson.isObject()) {
    if (error) *error = "스냅샷이 JSON 객체가 아닙니다.";
    return false;
  }
  Session s;
  s.snapshot_json_ = snapshotJson;
  s.version_ = version;
  s.policy_ = policy;
  s.rules_path_ = rulesPath;
  s.rules_ = rules;
  s.created_at_ = nowEpoch();
  s.status_ = SessionStatus::Checking;
  if (!s.rebuild(error)) return false;
  if (s.nextCheckIndex() < 0) s.status_ = SessionStatus::Exhausted;
  *out = s;
  return true;
}

bool Session::fromJson(const json::Value& state, const Rules& rules, Session* out,
                       std::string* error) {
  if (state["session_version"].asInt(0) != kSessionVersion) {
    if (error) *error = "세션 파일 형식이 다릅니다(session_version).";
    return false;
  }
  Session s;
  s.snapshot_json_ = state["snapshot"];
  s.version_ = engineVersionFromString(state["engine"].asString("v1"), nullptr);
  s.policy_ = ExecutionPolicy::fromJson(state["policy"]);
  s.rules_path_ = state["rules_path"].asString("");
  s.created_at_ = readTimeField(state["created_at"]);
  s.status_ = sessionStatusFromString(state["status"].asString("checking"));
  const json::Array& hist = state["history"].asArray();
  for (std::size_t i = 0; i < hist.size(); i++) s.history_.push_back(HistoryItem::fromJson(hist[i]));
  s.confirmed_ = state["confirmed"].asString("");
  s.confirm_check_ = state["confirm_check"].asString("");
  s.remedy_index_ = state["remedy_index"].asInt(0);
  s.applied_remedy_ = state["applied_remedy"].asString("");
  s.fix_applied_at_ = readTimeField(state["fix_applied_at"]);
  s.last_verify_ = state["last_verify"];
  s.rules_ = rules;
  if (!s.rebuild(error)) return false;
  *out = s;
  return true;
}

json::Value Session::toJson() const {
  json::Value o = json::Value::makeObject();
  o.set("session_version", json::Value(kSessionVersion));
  o.set("created_at", json::Value(json::formatIso8601(created_at_, snapshot_.tz_offset_minutes)));
  o.set("engine", json::Value(toString(version_)));
  o.set("policy", policy_.toJson());
  o.set("rules_path", json::Value(rules_path_));
  o.set("status", json::Value(toString(status_)));
  json::Value h = json::Value::makeArray();
  for (std::size_t i = 0; i < history_.size(); i++) h.push(history_[i].toJson());
  o.set("history", h);
  o.set("confirmed", json::Value(confirmed_));
  o.set("confirm_check", json::Value(confirm_check_));
  o.set("remedy_index", json::Value(remedy_index_));
  o.set("applied_remedy", json::Value(applied_remedy_));
  o.set("fix_applied_at", json::Value(timeOrEmpty(fix_applied_at_, snapshot_.tz_offset_minutes)));
  if (!last_verify_.isNull()) o.set("last_verify", last_verify_);
  o.set("snapshot", snapshot_json_);
  return o;
}

bool Session::rebuild(std::string* error) {
  std::vector<std::string> warn;
  snapshot_ = parseSnapshot(snapshot_json_, &warn);
  if (snapshot_.symptom == Symptom::Unknown) {
    if (error) *error = "스냅샷에 증상 유형(symptom.type)이 없어 세션을 시작할 수 없습니다.";
    return false;
  }
  diag_ = diagnose(snapshot_, rules_, version_, policy_, warn);
  instances_ = buildCheckInstances(diag_.planning, rules_);

  belief_.clear();
  double sum = 0;
  for (std::size_t i = 0; i < diag_.planning.size(); i++) {
    belief_.push_back(diag_.planning[i].probability);
    sum += diag_.planning[i].probability;
  }
  if (sum > 0) {
    for (std::size_t i = 0; i < belief_.size(); i++) belief_[i] /= sum;
  }
  used_.assign(instances_.size(), 0);

  // 지금까지 받은 점검 결과를 순서대로 다시 반영한다.
  for (std::size_t i = 0; i < history_.size(); i++) {
    if (history_[i].type != "check") continue;
    const int idx = instanceIndexOf(history_[i].ref);
    if (idx >= 0) applyCheckResult(idx, history_[i].result);
  }
  return true;
}

void Session::applyCheckResult(int instIdx, const std::string& result) {
  used_[instIdx] = 1;
  if (result != "solved" && result != "not_solved") return;  // skipped: 확률은 그대로
  const std::map<std::string, int> index = indexOf(diag_.planning);
  const std::vector<char> inSet = coverMask(instances_[instIdx], index, belief_.size());
  belief_ = bayesUpdate(belief_, inSet, instances_[instIdx].reliability, result == "solved");
}

int Session::instanceIndexOf(const std::string& checkId) const {
  for (std::size_t i = 0; i < instances_.size(); i++) {
    if (instances_[i].check_id == checkId) return static_cast<int>(i);
  }
  return -1;
}

int Session::topBeliefIndex() const {
  int best = -1;
  for (std::size_t i = 0; i < belief_.size(); i++) {
    if (best < 0 || belief_[i] > belief_[best]) best = static_cast<int>(i);
  }
  return best;
}

int Session::nextCheckIndex() const {
  if (status_ != SessionStatus::Checking) return -1;
  return chooseNextCheck(belief_, instances_, indexOf(diag_.planning), used_);
}

std::vector<RemedyOption> Session::remedies() const {
  std::vector<RemedyOption> out;
  if (confirmed_.empty()) return out;
  const int idx = findCandidate(diag_.candidates, confirmed_);
  if (idx < 0) return out;
  return remediesFor(rules_, diag_.candidates[idx], diag_.symptom, policy_, snapshot_.target_process);
}

// ---------------------------------------------------------------- 진행

// [설명] reportCheck : 점검 결과 받기
//   1) 입력 검사(상태가 checking 인지, 있는 점검인지, 이미 받은 점검이 아닌지)
//   2) history 에 기록하고 베이즈 갱신
//   3) '해결됨'이면 원인 확정 시도
//      - 후보 1개만 다루는 점검(예: Vanguard 중지) → 그 후보로 바로 확정
//      - 여러 후보를 한 번에 다루는 점검(예: 클린 부트) → 한 후보 확률이 85% 이상일 때만 확정
//   4) 더 할 점검이 없으면 exhausted
bool Session::reportCheck(const std::string& checkId, const std::string& result,
                          std::string* error) {
  if (status_ != SessionStatus::Checking) {
    if (error) *error = "지금은 점검 결과를 받는 단계가 아닙니다 (상태: " + statusText(status_) + ").";
    return false;
  }
  if (result != "solved" && result != "not_solved" && result != "skipped") {
    if (error) *error = "result 는 solved / not_solved / skipped 중 하나여야 합니다.";
    return false;
  }
  const int idx = instanceIndexOf(checkId);
  if (idx < 0) {
    if (error) *error = "이 세션에 없는 점검 id 입니다: " + checkId;
    return false;
  }
  if (used_[idx]) {
    if (error) *error = "이미 결과를 받은 점검입니다: " + checkId;
    return false;
  }

  HistoryItem h;
  h.type = "check";
  h.ref = checkId;
  h.result = result;
  h.time = nowEpoch();
  history_.push_back(h);
  applyCheckResult(idx, result);

  const CheckInstance& inst = instances_[idx];
  if (result == "solved") {
    std::string confirmedId;
    std::string note;
    if (inst.covers.size() == 1) {
      confirmedId = inst.covers[0];
      note = "개별 점검에서 증상이 사라져 원인으로 확인했습니다.";
    } else {
      const int top = topBeliefIndex();
      if (top >= 0 && belief_[top] >= rules_.confirm_threshold) {
        confirmedId = diag_.planning[top].id;
        note = "그룹 점검 후 확률이 " + percentText(belief_[top]) + "로 높아져 원인으로 확인했습니다.";
      }
    }
    if (!confirmedId.empty()) {
      confirmed_ = confirmedId;
      confirm_check_ = checkId;
      remedy_index_ = 0;
      status_ = SessionStatus::Confirmed;
      HistoryItem c;
      c.type = "confirm";
      c.ref = confirmedId;
      c.result = "confirmed";
      c.note = note;
      c.time = h.time;
      history_.push_back(c);
      return true;
    }
  }

  if (nextCheckIndex() < 0) status_ = SessionStatus::Exhausted;
  return true;
}

bool Session::applyFix(const std::string& remedyId, long long appliedAt, std::string* error) {
  if (status_ != SessionStatus::Confirmed && status_ != SessionStatus::Fixing) {
    if (error) *error = "원인이 확인된 뒤에만 해결책을 적용할 수 있습니다 (상태: " +
                        statusText(status_) + ").";
    return false;
  }
  const std::vector<RemedyOption> list = remedies();
  std::string chosen = remedyId;
  if (chosen.empty() && remedy_index_ < static_cast<int>(list.size())) {
    chosen = list[remedy_index_].remedy_id;
  }
  bool found = false;
  for (std::size_t i = 0; i < list.size(); i++) {
    if (list[i].remedy_id == chosen) {
      found = true;
      remedy_index_ = static_cast<int>(i);
    }
  }
  if (!found) {
    if (error) *error = "이 원인에 맞는 해결책 id 가 아닙니다: " + chosen;
    return false;
  }
  applied_remedy_ = chosen;
  fix_applied_at_ = appliedAt != 0 ? appliedAt : nowEpoch();
  status_ = SessionStatus::Fixing;

  HistoryItem h;
  h.type = "fix_applied";
  h.ref = chosen;
  h.result = "applied";
  h.time = fix_applied_at_;
  history_.push_back(h);
  return true;
}

// [설명] verifyWithSnapshot : 해결책을 적용한 뒤 정말 해결됐는지 확인
//   사후 스냅샷에서 다음을 본다.
//     ① 해결책 적용 시각 '이후'에 같은 증상 신호가 몇 번 있었나 (크래시 이벤트 1000/1001/1002, 백신 탐지 등)
//        → 1번이라도 있으면 recurred(재발)
//     ② 대상 프로그램을 실제로 실행했나 → 안 썼으면 '안 튕긴 것'이 의미가 없으므로 observing(관찰 중)
//     ③ 관찰 시간이 기준(기본 60분) 이상인가 → 짧으면 observing
//     ④ 입력 누락처럼 로그로 셀 수 없는 증상 → need_answer(사용자에게 직접 물어봐야 함)
//   모두 통과하면 resolved.
bool Session::verifyWithSnapshot(const json::Value& afterSnapshotJson, Rules* rulesForFeedback,
                                 VerifyResult* out, std::string* error) {
  if (status_ != SessionStatus::Fixing) {
    if (error) *error = "해결책을 적용한 뒤에만 검증할 수 있습니다 (상태: " + statusText(status_) + ").";
    return false;
  }
  std::vector<std::string> warn;
  Snapshot after = parseSnapshot(afterSnapshotJson, &warn);
  if (after.target_process.empty()) after.target_process = snapshot_.target_process;
  if (after.marked_at == 0) {
    if (error) *error = "사후 스냅샷에 marked_at 이 없습니다.";
    return false;
  }

  VerifyResult r;
  const long long fixTime = fix_applied_at_;
  long long observeStart = fixTime;
  if (after.observed_from > observeStart) observeStart = after.observed_from;
  r.observed_minutes = (after.marked_at - observeStart) / 60.0;
  if (r.observed_minutes < 0) r.observed_minutes = 0;
  r.target_ran = targetRan(after);

  const int cIdx = findCandidate(diag_.candidates, confirmed_);
  if (cIdx >= 0) r.cause_still_present = subjectPresent(after, diag_.candidates[cIdx]);

  // 증상별로 '객관적으로 셀 수 있는 재발 신호'를 센다.
  bool measurable = true;
  const Symptom sym = snapshot_.symptom;
  if (sym == Symptom::AppCrash || sym == Symptom::Freeze || sym == Symptom::LaunchFail) {
    const int crashes = signals::crashEventsSince(after, fixTime);
    r.recurrences += crashes;
    if (crashes > 0) {
      r.details.push_back("해결책 적용 이후 대상 프로그램 오류 이벤트가 " + std::to_string(crashes) +
                          "건 있습니다.");
    }
    if (!after.available.events) measurable = false;
  }
  if (sym == Symptom::LaunchFail || sym == Symptom::AppCrash) {
    const std::vector<int> dets = signals::detectionsOnTarget(after);
    int n = 0;
    for (std::size_t i = 0; i < dets.size(); i++) {
      if (after.detections[dets[i]].time >= fixTime) n++;
    }
    r.recurrences += n;
    if (n > 0) r.details.push_back("해결책 적용 이후에도 보안 제품의 탐지·차단이 " + std::to_string(n) + "건 있습니다.");
  }
  if (sym == Symptom::NetworkFail) {
    int n = 0;
    for (std::size_t i = 0; i < after.firewall_blocks.size(); i++) {
      const FirewallBlock& b = after.firewall_blocks[i];
      if (b.time >= fixTime && equalsNoCase(b.process, after.target_process)) n++;
    }
    r.recurrences += n;
    if (n > 0) r.details.push_back("해결책 적용 이후에도 방화벽 차단이 " + std::to_string(n) + "건 있습니다.");
    if (!after.available.firewall) measurable = false;
  }
  if (sym == Symptom::Flicker) {
    int n = 0;
    for (std::size_t i = 0; i < after.events.size(); i++) {
      if (signals::isDisplayEvent(after.events[i]) && after.events[i].time >= fixTime) n++;
    }
    r.recurrences += n;
    if (n > 0) r.details.push_back("해결책 적용 이후 디스플레이 이벤트가 " + std::to_string(n) + "건 있습니다.");
    if (!after.available.events) measurable = false;
  }
  if (sym == Symptom::UnexpectedReboot) {
    int n = 0;
    for (std::size_t i = 0; i < after.events.size(); i++) {
      if (signals::isKernelPower41(after.events[i]) && after.events[i].time >= fixTime) n++;
    }
    r.recurrences += n;
    if (!after.available.events) measurable = false;
  }
  if (sym == Symptom::InputLoss || sym == Symptom::FpsDrop) measurable = false;

  if (r.recurrences > 0) {
    r.outcome = "recurred";
  } else if (!measurable) {
    r.outcome = "need_answer";
    r.details.push_back("이 증상은 로그로 재발 여부를 셀 수 없어 사용자 응답이 필요합니다.");
  } else if (!r.target_ran) {
    r.outcome = "observing";
    r.details.push_back("해결책 적용 이후 대상 프로그램을 실행한 기록이 없어 판단을 보류합니다.");
  } else if (r.observed_minutes < rules_.verify_min_observe_minutes) {
    r.outcome = "observing";
    r.details.push_back("관찰 시간이 " + std::to_string(static_cast<int>(r.observed_minutes)) +
                        "분으로, 기준(" +
                        std::to_string(static_cast<int>(rules_.verify_min_observe_minutes)) +
                        "분)보다 짧습니다.");
  } else {
    r.outcome = "resolved";
    r.details.push_back("해결책 적용 이후 " + std::to_string(static_cast<int>(r.observed_minutes)) +
                        "분 동안 대상 프로그램을 사용했고 재발이 없었습니다.");
  }
  if (cIdx >= 0) {
    r.details.push_back(r.cause_still_present
                            ? "원인 대상(" + diag_.candidates[cIdx].label + ")은 여전히 설치·상주 중입니다."
                            : "원인 대상(" + diag_.candidates[cIdx].label + ")이 더 이상 상주하지 않습니다.");
  }
  if (!finishVerification(&r, rulesForFeedback, error)) return false;
  if (out) *out = r;
  return true;
}

bool Session::verifyWithAnswer(bool recurred, double observedMinutes, Rules* rulesForFeedback,
                               VerifyResult* out, std::string* error) {
  if (status_ != SessionStatus::Fixing) {
    if (error) *error = "해결책을 적용한 뒤에만 검증할 수 있습니다 (상태: " + statusText(status_) + ").";
    return false;
  }
  VerifyResult r;
  r.target_ran = true;
  r.observed_minutes = observedMinutes;
  r.recurrences = recurred ? 1 : 0;
  if (recurred) {
    r.outcome = "recurred";
    r.details.push_back("사용자 응답: 해결책 적용 후에도 증상이 다시 나타났습니다.");
  } else if (observedMinutes < rules_.verify_min_observe_minutes) {
    r.outcome = "observing";
    r.details.push_back("사용자 응답: 아직 재발은 없지만 관찰 시간이 기준보다 짧습니다.");
  } else {
    r.outcome = "resolved";
    r.details.push_back("사용자 응답: 해결책 적용 후 충분히 사용했고 증상이 없었습니다.");
  }
  if (!finishVerification(&r, rulesForFeedback, error)) return false;
  if (out) *out = r;
  return true;
}

bool Session::finishVerification(VerifyResult* r, Rules* rulesForFeedback, std::string* error) {
  (void)error;
  HistoryItem h;
  h.type = "verify";
  h.ref = applied_remedy_;
  h.result = r->outcome;
  h.time = nowEpoch();

  if (r->outcome == "resolved") {
    status_ = SessionStatus::Resolved;
    const int cIdx = findCandidate(diag_.candidates, confirmed_);
    if (rulesForFeedback != nullptr && cIdx >= 0) {
      FeedbackResult fr = applyFeedback(rulesForFeedback, diag_.symptom, diag_.candidates[cIdx], true);
      r->feedback_message = fr.message;
      rules_ = *rulesForFeedback;
    }
  } else if (r->outcome == "recurred") {
    remedy_index_ += 1;
    if (remedy_index_ < static_cast<int>(remedies().size())) {
      status_ = SessionStatus::Confirmed;  // 다음 해결책을 안내한다
      r->details.push_back("다음 해결책을 안내합니다.");
    } else {
      status_ = SessionStatus::Failed;
      r->details.push_back("등록된 해결책을 모두 시도했습니다.");
    }
    fix_applied_at_ = 0;
  }
  // observing / need_answer 는 상태를 바꾸지 않는다(검증 대기 유지).
  h.note = r->details.empty() ? "" : r->details[0];
  history_.push_back(h);
  last_verify_ = r->toJson();
  return true;
}

// ---------------------------------------------------------------- 보기

json::Value Session::view() const {
  const int tz = snapshot_.tz_offset_minutes;
  json::Value o = json::Value::makeObject();
  o.set("status", json::Value(toString(status_)));
  o.set("status_text", json::Value(statusText(status_)));
  o.set("snapshot_id", json::Value(snapshot_.snapshot_id));
  o.set("symptom", json::Value(diag_.symptom));
  o.set("target_process", json::Value(snapshot_.target_process));
  o.set("policy", policy_.toJson());

  int checksDone = 0;
  for (std::size_t i = 0; i < history_.size(); i++) {
    if (history_[i].type == "check") checksDone++;
  }
  o.set("checks_done", json::Value(checksDone));

  // 현재 후보 확률(상위 5개)
  std::vector<int> order;
  for (std::size_t i = 0; i < belief_.size(); i++) order.push_back(static_cast<int>(i));
  for (std::size_t i = 0; i < order.size(); i++) {  // 단순 선택 정렬(후보 수가 적다)
    for (std::size_t k = i + 1; k < order.size(); k++) {
      if (belief_[order[k]] > belief_[order[i]]) {
        const int tmp = order[i];
        order[i] = order[k];
        order[k] = tmp;
      }
    }
  }
  json::Value bel = json::Value::makeArray();
  for (std::size_t i = 0; i < order.size() && i < 5; i++) {
    const Candidate& c = diag_.planning[order[i]];
    json::Value x = json::Value::makeObject();
    x.set("id", json::Value(c.id));
    x.set("label", json::Value(c.label));
    x.set("category", json::Value(c.category));
    x.set("in_focus", json::Value(c.in_focus));
    x.set("probability", json::Value(belief_[order[i]]));
    bel.push(x);
  }
  o.set("belief", bel);

  // 다음 점검
  const int next = nextCheckIndex();
  if (next >= 0) {
    const CheckInstance& inst = instances_[next];
    const std::map<std::string, int> index = indexOf(diag_.planning);
    const std::vector<char> inSet = coverMask(inst, index, belief_.size());
    double mass = 0;
    for (std::size_t i = 0; i < belief_.size(); i++) {
      if (inSet[i]) mass += belief_[i];
    }
    ActionTarget target;
    target.subject = inst.covers_label;
    target.product = inst.covers_label;
    target.target_process = snapshot_.target_process;
    if (inst.covers.size() == 1) {
      const int ci = findCandidate(diag_.candidates, inst.covers[0]);
      if (ci >= 0) target = actionTargetOf(diag_.candidates[ci], snapshot_.target_process);
    }
    json::Value n = json::Value::makeObject();
    n.set("check_id", json::Value(inst.check_id));
    n.set("label", json::Value(inst.label));
    n.set("target", json::Value(inst.covers_label));
    n.set("advice", json::Value(inst.advice));
    n.set("cost_seconds", json::Value(inst.cost_seconds));
    n.set("group", json::Value(inst.group));
    n.set("chance_to_reveal_cause", json::Value(mass * inst.reliability));
    json::Value cov = json::Value::makeArray();
    for (std::size_t i = 0; i < inst.covers.size(); i++) cov.push(json::Value(inst.covers[i]));
    n.set("covers", cov);
    n.set("action", decideAction(rules_, inst.action_id, policy_, target).toJson());
    n.set("report_with", json::Value("solved(증상 사라짐) / not_solved(그대로) / skipped(할 수 없음)"));
    o.set("next_check", n);
  } else {
    o.set("next_check", json::Value());
  }

  // 확인된 원인과 해결책
  if (!confirmed_.empty()) {
    const int ci = findCandidate(diag_.candidates, confirmed_);
    if (ci >= 0) {
      const Candidate& c = diag_.candidates[ci];
      json::Value x = json::Value::makeObject();
      x.set("id", json::Value(c.id));
      x.set("label", json::Value(c.label));
      x.set("category", json::Value(c.category));
      x.set("subject", json::Value(c.subject));
      x.set("in_focus", json::Value(c.in_focus));
      x.set("confirmed_by", json::Value(confirm_check_));
      json::Value ev = json::Value::makeArray();
      for (std::size_t k = 0; k < c.evidence.size(); k++) {
        if (c.evidence[k].kind == "prior" || c.evidence[k].kind == "layer") continue;
        ev.push(c.evidence[k].toJson(tz));
      }
      x.set("evidence", ev);
      o.set("confirmed", x);
    }
    const int checkIdx = instanceIndexOf(confirm_check_);
    if (checkIdx >= 0) {
      const ActionDecision undo =
          decideAction(rules_, instances_[checkIdx].action_id, policy_, ActionTarget());
      if (!undo.rollback.empty()) {
        o.set("restore_after_check",
              json::Value("점검 때 바꾼 설정은 해결책을 적용하기 전에 되돌려 주세요: " + undo.rollback +
                          " (해결책이 같은 조치라면 그대로 두면 됩니다.)"));
      }
    }
    const std::vector<RemedyOption> list = remedies();
    json::Value rs = json::Value::makeArray();
    for (std::size_t i = 0; i < list.size(); i++) rs.push(list[i].toJson());
    o.set("remedies", rs);
    o.set("current_remedy_index", json::Value(remedy_index_));
    if (remedy_index_ < static_cast<int>(list.size())) {
      o.set("current_remedy", json::Value(list[remedy_index_].remedy_id));
    }
  }
  if (!applied_remedy_.empty()) {
    o.set("applied_remedy", json::Value(applied_remedy_));
    o.set("fix_applied_at", json::Value(timeOrEmpty(fix_applied_at_, tz)));
  }
  if (!last_verify_.isNull()) o.set("last_verify", last_verify_);

  // 다음에 할 일 한 줄 요약 (UI·에이전트가 그대로 보여 주기 좋게)
  std::string nextAction;
  if (status_ == SessionStatus::Checking) {
    nextAction = next >= 0 ? "next_check 를 수행하고 결과를 session_report 로 알려 주세요."
                           : "더 할 점검이 없습니다.";
  } else if (status_ == SessionStatus::Confirmed) {
    nextAction = "current_remedy 를 적용하고 session_apply_fix 로 알려 주세요.";
  } else if (status_ == SessionStatus::Fixing) {
    nextAction = "대상 프로그램을 충분히 사용한 뒤 사후 스냅샷(또는 사용자 응답)으로 session_verify 를 호출하세요.";
  } else if (status_ == SessionStatus::Resolved) {
    nextAction = "해결이 확인되었습니다. 사례 DB 갱신은 규칙 파일(rules_path)과 save_rules 를 주면 파일에 저장됩니다.";
  } else if (status_ == SessionStatus::Exhausted) {
    nextAction = "재현 모드로 다시 수집하거나, 생략한 층의 점검을 고려하세요.";
  } else {
    nextAction = "제조사 지원 문의를 안내하고, 이 사례를 '미해결'로 기록하세요.";
  }
  o.set("next_action", json::Value(nextAction));

  json::Value h = json::Value::makeArray();
  for (std::size_t i = 0; i < history_.size(); i++) h.push(history_[i].toJson());
  o.set("history", h);
  return o;
}

std::string Session::viewText() const {
  const json::Value v = view();
  std::ostringstream os;
  os << "=== 진단 세션: " << v["snapshot_id"].asString() << " ===\n";
  os << "상태: " << v["status_text"].asString() << "  (증상 " << v["symptom"].asString();
  if (!v["target_process"].asString().empty()) os << " / 대상 " << v["target_process"].asString();
  os << " / " << (policy_.mode == ExecutionMode::Auto ? "자동 점검 모드" : "안내 모드") << ")\n";

  os << "\n[현재 원인 후보 확률]\n";
  const json::Array& bel = v["belief"].asArray();
  for (std::size_t i = 0; i < bel.size(); i++) {
    os << " " << (i + 1) << ". " << bel[i]["label"].asString() << "  "
       << percentText(bel[i]["probability"].asNumber()) << "\n";
  }

  const json::Value& n = v["next_check"];
  if (!n.isNull()) {
    os << "\n[다음 점검] " << n["label"].asString() << " → " << n["target"].asString() << "\n";
    os << "  id: " << n["check_id"].asString() << "\n";
    os << "  " << n["advice"].asString() << "\n";
    os << "  원인이 드러날 확률 " << percentText(n["chance_to_reveal_cause"].asNumber()) << ", 예상 "
       << durationText(n["cost_seconds"].asNumber()) << "\n";
    const json::Value& a = n["action"];
    os << "  실행 방식: " << a["execution"].asString() << " — " << a["reason"].asString() << "\n";
    const json::Array& steps = a["steps"].asArray();
    for (std::size_t i = 0; i < steps.size(); i++) os << "   " << (i + 1) << ") " << steps[i].asString() << "\n";
    if (!a["rollback"].asString().empty()) os << "   되돌리기: " << a["rollback"].asString() << "\n";
  }

  const json::Value& c = v["confirmed"];
  if (!c.isNull()) {
    os << "\n[확인된 원인] " << c["label"].asString() << (c["in_focus"].asBool() ? "" : " (1차 범위 밖)")
       << "\n";
    const json::Array& ev = c["evidence"].asArray();
    for (std::size_t i = 0; i < ev.size(); i++) os << "  · " << ev[i]["detail"].asString() << "\n";
    if (!v["restore_after_check"].isNull()) os << "  ※ " << v["restore_after_check"].asString() << "\n";

    os << "\n[해결 안내]\n";
    const json::Array& rs = v["remedies"].asArray();
    const int cur = v["current_remedy_index"].asInt(0);
    for (std::size_t i = 0; i < rs.size(); i++) {
      os << (static_cast<int>(i) == cur ? " ▶ " : "   ") << (i + 1) << ". " << rs[i]["label"].asString()
         << "  (id " << rs[i]["remedy_id"].asString() << ", 기대 해결률 "
         << percentText(rs[i]["expected_success"].asNumber()) << ")\n";
      if (static_cast<int>(i) != cur) continue;
      os << "      " << rs[i]["description"].asString() << "\n";
      const json::Array& acts = rs[i]["actions"].asArray();
      for (std::size_t k = 0; k < acts.size(); k++) {
        os << "      - " << acts[k]["label"].asString() << " [" << acts[k]["execution"].asString()
           << "]\n";
        const json::Array& steps = acts[k]["steps"].asArray();
        for (std::size_t j = 0; j < steps.size(); j++) {
          os << "        " << (j + 1) << ") " << steps[j].asString() << "\n";
        }
      }
    }
  }

  const json::Value& lv = v["last_verify"];
  if (!lv.isNull()) {
    os << "\n[최근 검증] " << lv["outcome"].asString() << " (관찰 "
       << static_cast<int>(lv["observed_minutes"].asNumber()) << "분, 재발 " << lv["recurrences"].asInt()
       << "건)\n";
    const json::Array& d = lv["details"].asArray();
    for (std::size_t i = 0; i < d.size(); i++) os << "  · " << d[i].asString() << "\n";
    if (!lv["feedback"].isNull()) os << "  · " << lv["feedback"].asString() << "\n";
  }

  os << "\n다음 할 일: " << v["next_action"].asString() << "\n";
  return os.str();
}

// ---------------------------------------------------------------- 파일

bool loadSessionFile(const std::string& path, const Rules& defaultRules, Session* out,
                     Rules* rulesOut, std::string* error) {
  std::string err;
  json::Value state = json::Value::parseFile(path, &err);
  if (!err.empty()) {
    if (error) *error = err;
    return false;
  }
  Rules rules = defaultRules;
  const std::string rulesPath = state["rules_path"].asString("");
  if (!rulesPath.empty()) {
    if (!Rules::load(rulesPath, &rules, &err)) {
      if (error) *error = "세션에 기록된 규칙 파일을 읽을 수 없습니다: " + err;
      return false;
    }
  }
  if (rulesOut) *rulesOut = rules;
  return Session::fromJson(state, rules, out, error);
}

bool saveSessionFile(const std::string& path, const Session& s, std::string* error) {
  return json::Value::writeFile(path, s.toJson(), error);
}

}  // namespace ce
