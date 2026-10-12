// [팀원용] 테스트 실행: ctest --test-dir build -C Release --output-on-failure
//   각 check(조건, "설명") 이 하나의 확인 항목이다. 코드를 고친 뒤 이 테스트가 모두 통과하는지 꼭 확인한다.
//   시나리오별 기대 결과(1순위가 무엇이어야 하는지 등)를 여기서 보면 엔진이 무엇을 해야 하는지 빨리 알 수 있다.

// test_main.cpp - 의존성 없는 간단한 assert 테스트
// 실행: ce_tests <data 디렉터리 경로>
#include <cmath>
#include <cstdio>
#include <iostream>
#include <string>
#include <vector>

#include "ce/bench.hpp"
#include "ce/engine.hpp"
#include "ce/feedback.hpp"
#include "ce/session.hpp"
#include "ce/signals.hpp"
#include "ce/tool.hpp"

using namespace ce;

namespace {

int g_failed = 0;
int g_passed = 0;
std::string g_data;

void check(bool cond, const std::string& name, const std::string& extra = "") {
  if (cond) {
    g_passed++;
    std::cout << "  [PASS] " << name << "\n";
  } else {
    g_failed++;
    std::cout << "  [FAIL] " << name << (extra.empty() ? "" : " :: " + extra) << "\n";
  }
}

json::Value loadJson(const std::string& name) {
  std::string err;
  json::Value v = json::Value::parseFile(g_data + "/samples/" + name, &err);
  if (!err.empty()) {
    std::cout << "  [FAIL] 파일 로드: " << name << " (" << err << ")\n";
    g_failed++;
  }
  return v;
}

Diagnosis diagnoseFile(const std::string& name, const ExecutionPolicy& policy) {
  std::vector<std::string> warn;
  const Snapshot s = parseSnapshot(loadJson(name), &warn);
  return diagnose(s, Rules::defaults(), EngineVersion::V1, policy, warn);
}

std::string topLabel(const Diagnosis& d) { return d.top() ? d.top()->label : "(없음)"; }

// 확인 순서 첫 단계의 점검 템플릿 id (없으면 빈 문자열)
std::string firstCheckTemplate(const Diagnosis& d) {
  return d.plan.steps.empty() ? "" : d.plan.steps[0].check.template_id;
}

// 확인 순서에 이 점검 템플릿이 들어 있는가
bool planUses(const Diagnosis& d, const std::string& templateId) {
  for (std::size_t i = 0; i < d.plan.steps.size(); i++) {
    if (d.plan.steps[i].check.template_id == templateId) return true;
  }
  return false;
}

// 후보 id 로 위치 찾기 (없으면 -1)
int candidateIndex(const Diagnosis& d, const std::string& id) {
  for (std::size_t i = 0; i < d.candidates.size(); i++) {
    if (d.candidates[i].id == id) return static_cast<int>(i);
  }
  return -1;
}

// ---------------------------------------------------------------- 기초

void testJson() {
  std::cout << "[json]\n";
  std::string err;
  json::Value v = json::Value::parse(R"({"a":1,"b":[1,2,{"c":"한글"}],"d":true,"e":null})", &err);
  check(err.empty(), "파싱 성공", err);
  check(v["a"].asInt() == 1, "숫자");
  check(v["b"].size() == 3, "배열 길이");
  check(v["b"][2]["c"].asString() == "한글", "UTF-8 문자열");
  check(v["d"].asBool(), "불린");
  check(v["e"].isNull(), "null");
  check(v["없는키"].isNull(), "없는 키는 null");

  json::Value round = json::Value::parse(v.dump(), &err);
  check(err.empty() && round["b"][2]["c"].asString() == "한글", "덤프 후 재파싱");

  json::Value bom = json::Value::parse("\xEF\xBB\xBF{\"x\":2}", &err);
  check(bom["x"].asInt() == 2, "UTF-8 BOM 허용(메모장 저장 파일)");

  long long ts = 0;
  check(json::parseIso8601("2026-10-16T14:32:10+09:00", &ts), "ISO8601 파싱");
  long long ts2 = 0;
  json::parseIso8601("2026-10-16T05:32:10Z", &ts2);
  check(ts == ts2, "시간대 오프셋 반영", std::to_string(ts) + " vs " + std::to_string(ts2));
  check(json::formatIso8601(ts, 540) == "2026-10-16T14:32:10+09:00", "한국 시간 표기",
        json::formatIso8601(ts, 540));
}

void testRules() {
  std::cout << "[rules]\n";
  const Rules d = Rules::defaults();
  check(!d.actions.empty() && !d.checks.empty() && !d.remedies.empty(), "기본 규칙 로드");
  check(d.scopes.size() == 3, "지원 범위 3단계 (보안 → 드라이버·시스템 → 하드웨어)");
  check(d.stageOf("anticheat") == 1 && d.stageOf("gpu_driver") == 2 && d.stageOf("memory") == 3,
        "카테고리별 단계");
  check(d.stageOf("overlay") == 0 && !d.inFocus("overlay") && d.inFocus("anticheat"),
        "일반 소프트웨어(오버레이)는 범위 밖");

  Rules fromFile;
  std::string err;
  check(Rules::load(g_data + "/rules.json", &fromFile, &err), "data/rules.json 로드", err);
  check(fromFile.toJson().dump() == d.toJson().dump(),
        "data/rules.json 과 내장 기본 규칙이 같음 (tools/gen_rules_default.py 실행 확인)");

  // 모든 점검·해결책이 실제로 있는 조치를 가리키는가
  bool allOk = true;
  for (std::size_t i = 0; i < d.checks.size(); i++) {
    if (d.findAction(d.checks[i].action) == nullptr) {
      allOk = false;
      std::cout << "    점검의 조치 없음: " << d.checks[i].id << "\n";
    }
  }
  for (std::size_t i = 0; i < d.remedies.size(); i++) {
    for (std::size_t k = 0; k < d.remedies[i].actions.size(); k++) {
      if (d.findAction(d.remedies[i].actions[k]) == nullptr) {
        allOk = false;
        std::cout << "    해결책의 조치 없음: " << d.remedies[i].id << "\n";
      }
    }
  }
  check(allOk, "점검·해결책 → 조치 참조 무결성");

  json::Value round = Rules::fromJson(d.toJson()).toJson();
  check(round.dump() == d.toJson().dump(), "규칙 JSON 왕복 변환");
}

// ---------------------------------------------------------------- 시나리오별 진단

void testScenarios() {
  ExecutionPolicy guide;
  {
    std::cout << "[s1] 안티치트 + 다른 게임 크래시 (1차 범위)\n";
    const Diagnosis d = diagnoseFile("s1_anticheat_crash.json", guide);
    check(d.layer.top == Layer::Software, "층 판정 = SW", toString(d.layer.top));
    check(d.top() && d.top()->category == "anticheat", "1순위 = 안티치트", topLabel(d));
    check(d.top() && d.top()->in_focus, "1순위가 1차 범위 안");
    check(!d.plan.steps.empty(), "확인 순서 생성");
    check(d.step_actions.size() == d.plan.steps.size(), "단계마다 실행 방식 결정");
    check(!d.skipped.empty(), "하드웨어 점검 생략 안내");
  }
  {
    std::cout << "[s2] 금융 키보드 보안모듈 + 입력 누락 (1차 범위)\n";
    const Diagnosis d = diagnoseFile("s2_finsec_input_loss.json", guide);
    check(d.top() && d.top()->category == "fin_security", "1순위 = 금융 보안모듈", topLabel(d));
    bool sawClass = false;
    for (std::size_t i = 0; i < d.candidates.size(); i++) {
      if (normalizeName(d.candidates[i].subject) == "kbdclass") sawClass = true;
    }
    check(!sawClass, "Windows 기본 kbdclass 는 후보에서 제외");
  }
  {
    std::cout << "[s3] 백신 오탐 격리 + 실행 불가 (1차 범위)\n";
    const Diagnosis d = diagnoseFile("s3_av_launch_fail.json", guide);
    check(d.top() && d.top()->category == "antivirus", "1순위 = 백신", topLabel(d));
    check(d.top() && containsNoCase(d.top()->label, "AhnLab"), "격리한 제품(AhnLab V3)을 지목",
          topLabel(d));
    bool selfCandidate = false;
    for (std::size_t i = 0; i < d.candidates.size(); i++) {
      if (sameSubject(d.candidates[i].subject, "IndieGame")) selfCandidate = true;
    }
    check(!selfCandidate, "대상 프로그램 자신의 업데이트는 충돌 후보가 아님");
    bool note = false;
    for (std::size_t i = 0; i < d.notes.size(); i++) {
      if (d.notes[i].find("대상 프로그램 자체") != std::string::npos) note = true;
    }
    check(note, "대상 프로그램 자체 변경은 노트로 안내");
  }
  {
    std::cout << "[s4] 모니터 유틸 + 깜빡임 (2차 범위)\n";
    const Diagnosis d = diagnoseFile("s4_display_util_flicker.json", guide);
    check(d.layer.top == Layer::Driver, "층 판정 = DRIVER", toString(d.layer.top));
    check(d.top() && d.top()->category == "display_util", "1순위 = 모니터 유틸", topLabel(d));
    check(d.top() && d.top()->stage == 2, "2차 범위(드라이버·시스템)로 표시");
  }
  {
    std::cout << "[s5] 과열 + 프레임 저하 (3차 범위)\n";
    const Diagnosis d = diagnoseFile("s5_thermal_fps_drop.json", guide);
    check(d.layer.top == Layer::Hardware, "층 판정 = HW", toString(d.layer.top));
    check(d.top() && d.top()->category == "thermal", "1순위 = 냉각", topLabel(d));
    check(d.top() && d.top()->stage == 3, "3차 범위(하드웨어)로 표시");
    check(firstCheckTemplate(d) == "fan_curve_test", "첫 점검 = 팬 속도 올려 재현(5분)",
          firstCheckTemplate(d));
  }
  {
    std::cout << "[s6] 수집기 MVP 신호만 있는 스냅샷 (모듈·이벤트·UpperFilters)\n";
    const Diagnosis d = diagnoseFile("s6_mvp_av_hook_crash.json", guide);
    check(d.top() && d.top()->category == "antivirus", "부분 수집으로도 1순위 = 백신 훅", topLabel(d));
    check(d.layer.confidence < 0.9, "수집 항목이 적어 층 신뢰도를 낮춤",
          percentText(d.layer.confidence));
    bool merged = true;
    int count = 0;
    for (std::size_t i = 0; i < d.candidates.size(); i++) {
      if (sameSubject(d.candidates[i].subject, "aswhook.dll")) count++;
    }
    if (count != 1) merged = false;
    check(merged, "오류 모듈과 로드 모듈이 같은 후보로 합쳐짐", std::to_string(count) + "건");
  }
}

// ---------------------------------------------------------------- 2차(드라이버)·3차(하드웨어) 시나리오

void testDriverHardwareScenarios() {
  ExecutionPolicy guide;
  {
    std::cout << "[s7] GPU 드라이버 블루스크린 0x116 (2차 범위)\n";
    const Diagnosis d = diagnoseFile("s7_gpu_driver_bsod.json", guide);
    check(d.layer.top == Layer::Driver, "층 판정 = DRIVER", toString(d.layer.top));
    check(d.top() && d.top()->id == "gpu_driver:nvlddmkm.sys", "1순위 = NVIDIA 드라이버", topLabel(d));
    check(d.top() && d.top()->stage == 2, "2차 범위로 표시");
    check(firstCheckTemplate(d) == "rollback_gpu_driver_quick", "첫 점검 = GPU 드라이버 롤백(7분)",
          firstCheckTemplate(d));
    check(!planUses(d, "nic_power_saving_test"), "블루스크린 증상에 랜카드 절전 점검은 쓰지 않음");
    const int power = candidateIndex(d, "power:power");
    check(power < 0 || d.candidates[power].probability < 0.2,
          "블루스크린 뒤의 Kernel-Power 41 은 전원 문제로 세지 않음");
  }
  {
    std::cout << "[s8] 랜카드 드라이버 + 네트워크 끊김 (2차 범위)\n";
    const Diagnosis d = diagnoseFile("s8_net_driver_drop.json", guide);
    check(d.layer.top == Layer::Driver, "층 판정 = DRIVER", toString(d.layer.top));
    check(d.top() && d.top()->category == "network_driver", "1순위 = 랜카드 드라이버", topLabel(d));
    check(firstCheckTemplate(d) == "nic_power_saving_test", "첫 점검 = 랜카드 절전 끄고 재현",
          firstCheckTemplate(d));
  }
  {
    std::cout << "[s9] 전원 부족 + 예고 없는 재부팅 (3차 범위)\n";
    const Diagnosis d = diagnoseFile("s9_power_reboot.json", guide);
    check(d.layer.top == Layer::Hardware, "층 판정 = HW", toString(d.layer.top));
    check(d.top() && d.top()->category == "power", "1순위 = 전원", topLabel(d));
    check(firstCheckTemplate(d) == "gpu_power_limit_test", "첫 점검 = GPU 전력 제한 낮춰 재현",
          firstCheckTemplate(d));
  }
  {
    std::cout << "[s10] 메모리 WHEA 오류 (3차 범위)\n";
    const Diagnosis d = diagnoseFile("s10_memory_whea.json", guide);
    check(d.layer.top == Layer::Hardware, "층 판정 = HW", toString(d.layer.top));
    check(d.top() && d.top()->category == "memory", "1순위 = 메모리", topLabel(d));
    check(firstCheckTemplate(d) == "xmp_off_test", "첫 점검 = XMP 끄고 재현(10분)",
          firstCheckTemplate(d));
  }
  {
    std::cout << "[s11] 저장장치 SMART 경고 + 프리징 (3차 범위)\n";
    const Diagnosis d = diagnoseFile("s11_storage_freeze.json", guide);
    check(d.layer.top == Layer::Hardware, "층 판정 = HW", toString(d.layer.top));
    check(d.top() && d.top()->category == "storage", "1순위 = 저장장치", topLabel(d));
    check(planUses(d, "storage_check"), "저장장치 점검이 순서에 포함");
  }
  {
    std::cout << "[신호] 전원 손실 판정\n";
    std::vector<std::string> warn;
    const Snapshot bsod = parseSnapshot(loadJson("s7_gpu_driver_bsod.json"), &warn);
    int lost = 0;
    for (std::size_t i = 0; i < bsod.events.size(); i++) {
      if (signals::isPowerLossEvent(bsod, bsod.events[i])) lost++;
    }
    check(lost == 0, "블루스크린 직후 41 은 전원 손실 아님", std::to_string(lost) + "건");
    const Snapshot power = parseSnapshot(loadJson("s9_power_reboot.json"), &warn);
    lost = 0;
    for (std::size_t i = 0; i < power.events.size(); i++) {
      if (signals::isPowerLossEvent(power, power.events[i])) lost++;
    }
    check(lost == 3, "버그체크 없는 41 3건 = 전원 손실 3건", std::to_string(lost) + "건");
    check(signals::hasNonZeroBugcheckCode("BugcheckCode 278") &&
              !signals::hasNonZeroBugcheckCode("BugcheckCode 0") &&
              !signals::hasNonZeroBugcheckCode(""),
          "41 이벤트의 BugcheckCode 읽기 (덤프가 없어도 블루스크린 재부팅 구분)");
  }
}

// ---------------------------------------------------------------- 실행 방식 (안내/자동)

void testExecutionPolicy() {
  std::cout << "[실행 방식]\n";
  const Rules r = Rules::defaults();
  ActionTarget t;
  t.subject = "vgk.sys";
  t.product = "Riot Vanguard";
  t.target_process = "game.exe";

  ExecutionPolicy guide;
  check(decideAction(r, "a_overlay_off", guide, t).execution == "guide", "안내 모드는 실행하지 않음");

  ExecutionPolicy autoMode;
  autoMode.mode = ExecutionMode::Auto;
  check(decideAction(r, "a_overlay_off", autoMode, t).execution == "auto",
        "자동 모드: 되돌릴 수 있고 기본 허용 → 자동");
  check(decideAction(r, "a_av_pause", autoMode, t).execution == "confirm",
        "자동 모드: 보안 공백이 생기는 조치는 기본 확인");
  check(decideAction(r, "a_reinstall", autoMode, t).execution == "confirm",
        "자동 모드: 되돌릴 수 없는 조치는 항상 확인");
  check(decideAction(r, "a_cooling", autoMode, t).execution == "manual", "물리 점검은 수동");
  check(decideAction(r, "a_anticheat_stop", autoMode, t).execution == "confirm",
        "재부팅이 필요한 조치는 확인");

  ExecutionPolicy narrow = autoMode;
  narrow.allowed_actions.push_back("a_av_pause");
  check(decideAction(r, "a_av_pause", narrow, t).execution == "auto", "허용 목록에 넣으면 자동");
  check(decideAction(r, "a_overlay_off", narrow, t).execution == "confirm",
        "허용 목록을 주면 목록 밖은 확인");

  const ActionDecision av = decideAction(r, "a_av_exclude", autoMode, t);
  check(av.automation == "av_exclude:Riot Vanguard:game.exe", "실행기 명령 자리표시자 채움",
        av.automation);
}

// ---------------------------------------------------------------- 세션: 확인 → 안내 → 해결 → 검증

void testSessionEndToEnd() {
  std::cout << "[세션] s1 안티치트: 증상 → 원인 확인 → 해결 안내 → 실제 해결\n";
  const Rules rules = Rules::defaults();
  Session s;
  std::string err;
  check(Session::start(loadJson("s1_anticheat_crash.json"), rules, EngineVersion::V1,
                       ExecutionPolicy(), "", &s, &err),
        "세션 시작", err);
  check(s.status() == SessionStatus::Checking, "상태 = 원인 확인 중");

  // 사용자가 '아님'을 보고하는 동안 안티치트 점검이 나올 때까지 진행
  int guard = 0;
  bool reachedAnticheat = false;
  while (s.status() == SessionStatus::Checking && guard < 10) {
    guard++;
    const int next = s.nextCheckIndex();
    if (next < 0) break;
    const CheckInstance& inst = s.checkInstances()[next];
    const bool isAnticheat = inst.template_id == "pause_anticheat";
    if (isAnticheat) reachedAnticheat = true;
    if (!s.reportCheck(inst.check_id, isAnticheat ? "solved" : "not_solved", &err)) {
      check(false, "점검 결과 보고", err);
      return;
    }
  }
  check(reachedAnticheat, "안티치트 점검까지 도달", std::to_string(guard) + "단계");
  check(s.status() == SessionStatus::Confirmed, "개별 점검 해결 → 원인 확정", statusText(s.status()));
  check(s.confirmedCandidateId() == "anticheat:vgk.sys", "확정 원인 = vgk.sys", s.confirmedCandidateId());

  const std::vector<RemedyOption> rem = s.remedies();
  check(!rem.empty() && rem[0].remedy_id == "r_anticheat_autostart_off",
        "1순위 해결책 = 해당 게임 할 때만 켜기", rem.empty() ? "" : rem[0].remedy_id);

  // 저장 → 복원 (도구 호출처럼 프로세스가 바뀌어도 이어지는지)
  Session restored;
  check(Session::fromJson(s.toJson(), rules, &restored, &err), "세션 저장·복원", err);
  check(restored.status() == SessionStatus::Confirmed && restored.confirmedCandidateId() == s.confirmedCandidateId(),
        "복원 후 상태 동일");

  long long applied = 0;
  json::parseIso8601("2026-10-15T20:55:00+09:00", &applied);
  check(restored.applyFix("", applied, &err), "현재 해결책 적용 기록", err);
  check(restored.status() == SessionStatus::Fixing, "상태 = 검증 대기");

  Rules learning = rules;
  VerifyResult vr;
  check(restored.verifyWithSnapshot(loadJson("s1_anticheat_after_fix.json"), &learning, &vr, &err),
        "사후 스냅샷 검증", err);
  check(vr.outcome == "resolved", "재발 없음 → 해결", vr.outcome);
  check(vr.target_ran, "적용 후 대상 프로그램을 실제로 실행함");
  check(!vr.cause_still_present, "원인(vgk)이 더 이상 상주하지 않음");
  check(std::fabs(vr.observed_minutes - 120.0) < 0.01, "관찰 시간 120분",
        std::to_string(vr.observed_minutes));
  check(restored.status() == SessionStatus::Resolved, "상태 = 해결 완료");
  const int ci = learning.findCaseIndex("app_crash", "anticheat", "vgk.sys");
  check(ci >= 0 && learning.cases[ci].solved == 1, "사례 DB에 '해결됨' 1건 반영");
}

void testSessionRecurrence() {
  std::cout << "[세션] 재발 → 다음 해결책 → 모두 실패\n";
  const Rules rules = Rules::defaults();
  Session s;
  std::string err;
  Session::start(loadJson("s1_anticheat_crash.json"), rules, EngineVersion::V1, ExecutionPolicy(),
                 "", &s, &err);
  check(s.reportCheck("pause_anticheat@anticheat:vgk.sys", "solved", &err), "추천 순서가 아닌 점검도 보고 가능", err);
  check(s.status() == SessionStatus::Confirmed, "원인 확정");

  // 사후 스냅샷에 크래시가 다시 있으면 재발
  json::Value after = loadJson("s1_anticheat_after_fix.json");
  json::Value ev = json::Value::makeObject();
  ev.set("time", json::Value("2026-10-15T22:10:00+09:00"));
  ev.set("provider", json::Value("Application Error"));
  ev.set("id", json::Value(1000));
  ev.set("subject", json::Value("othergame.exe"));
  ev.set("faulting_module", json::Value("ntdll.dll"));
  json::Value events = after["events"];
  events.push(ev);
  after.set("events", events);

  long long applied = 0;
  json::parseIso8601("2026-10-15T20:55:00+09:00", &applied);
  const int total = static_cast<int>(s.remedies().size());
  s.applyFix("", applied, &err);
  VerifyResult vr;
  s.verifyWithSnapshot(after, nullptr, &vr, &err);
  check(vr.outcome == "recurred" && vr.recurrences == 1, "적용 후 크래시 1건 → 재발", vr.outcome);
  check(s.status() == SessionStatus::Confirmed, "다음 해결책 안내로 돌아감");

  for (int i = 1; i < total; i++) {
    s.applyFix("", applied, &err);
    s.verifyWithSnapshot(after, nullptr, &vr, &err);
  }
  check(s.status() == SessionStatus::Failed, "해결책을 모두 시도하면 실패 상태", statusText(s.status()));

  std::cout << "[세션] 관찰 부족·응답 검증\n";
  Session s2;
  Session::start(loadJson("s2_finsec_input_loss.json"), rules, EngineVersion::V1, ExecutionPolicy(),
                 "", &s2, &err);
  s2.reportCheck("pause_fin_security@fin_security:nxkeyfilt.sys", "solved", &err);
  check(s2.status() == SessionStatus::Confirmed, "금융 보안모듈 원인 확정", err);
  s2.applyFix("", applied, &err);
  json::Value inputAfter = loadJson("s1_anticheat_after_fix.json");  // 증상 유형이 달라도 형식 검증용
  s2.verifyWithSnapshot(inputAfter, nullptr, &vr, &err);
  check(vr.outcome == "need_answer", "입력 누락은 로그로 셀 수 없어 사용자 응답 필요", vr.outcome);
  s2.verifyWithAnswer(false, 20, nullptr, &vr, &err);
  check(vr.outcome == "observing", "20분 관찰은 기준(60분) 미만 → 관찰 중", vr.outcome);
  s2.verifyWithAnswer(false, 90, nullptr, &vr, &err);
  check(vr.outcome == "resolved" && s2.status() == SessionStatus::Resolved, "90분 재발 없음 → 해결");
}

// 2차·3차 범위도 1차와 같은 흐름(원인 확인 → 해결 안내 → 검증)으로 끝까지 가는지 확인한다.
// 검증은 증상 종류가 아니라 '원인 신호'(드라이버 = 새 블루스크린·4101, 냉각 = 스로틀링)로 센다.
void runFixFlow(const std::string& before, const std::string& checkId, const std::string& expectId,
                const std::string& expectRemedy, const std::string& appliedAt,
                const std::string& after) {
  const Rules rules = Rules::defaults();
  Session s;
  std::string err;
  check(Session::start(loadJson(before), rules, EngineVersion::V1, ExecutionPolicy(), "", &s, &err),
        "세션 시작", err);
  check(s.reportCheck(checkId, "solved", &err), "점검 '해결됨' 보고", err);
  check(s.status() == SessionStatus::Confirmed && s.confirmedCandidateId() == expectId,
        "원인 확정 = " + expectId, s.confirmedCandidateId());
  const std::vector<RemedyOption> rem = s.remedies();
  check(!rem.empty() && rem[0].remedy_id == expectRemedy,
        "점검에서 효과를 본 조치가 1순위 해결책 = " + expectRemedy, rem.empty() ? "" : rem[0].remedy_id);
  long long applied = 0;
  json::parseIso8601(appliedAt, &applied);
  check(s.applyFix("", applied, &err), "해결책 적용 기록", err);
  VerifyResult vr;
  check(s.verifyWithSnapshot(loadJson(after), nullptr, &vr, &err), "사후 스냅샷 검증", err);
  check(vr.outcome == "resolved" && s.status() == SessionStatus::Resolved, "원인 신호 재발 없음 → 해결",
        vr.outcome);

  // 같은 사후 스냅샷에 원인 신호를 하나 넣으면 재발로 판단해야 한다
  Session s2;
  Session::start(loadJson(before), rules, EngineVersion::V1, ExecutionPolicy(), "", &s2, &err);
  s2.reportCheck(checkId, "solved", &err);
  s2.applyFix("", applied, &err);
  json::Value bad = loadJson(after);
  if (expectId.find("thermal") == 0) {
    json::Value sensors = bad["sensors"];
    json::Value samples = sensors["samples"];
    json::Value hot = json::Value::makeObject();
    hot.set("time", json::Value("2026-11-02T22:00:00+09:00"));
    hot.set("cpu_temp", json::Value(98.0));
    hot.set("gpu_temp", json::Value(91.0));
    hot.set("throttling", json::Value(true));
    samples.push(hot);
    sensors.set("samples", samples);
    bad.set("sensors", sensors);
  } else {
    json::Value events = bad["events"];
    json::Value ev = json::Value::makeObject();
    ev.set("time", json::Value("2026-11-07T21:00:00+09:00"));
    ev.set("provider", json::Value("Display"));
    ev.set("id", json::Value(4101));
    ev.set("subject", json::Value("nvlddmkm"));
    events.push(ev);
    bad.set("events", events);
  }
  s2.verifyWithSnapshot(bad, nullptr, &vr, &err);
  check(vr.outcome == "recurred", "원인 신호 1건 → 재발", vr.outcome);
}

void testDriverHardwareSessions() {
  std::cout << "[세션] s7 GPU 드라이버: 롤백 점검 → 해결 → 이틀 관찰 검증\n";
  runFixFlow("s7_gpu_driver_bsod.json", "rollback_gpu_driver_quick@gpu_driver:nvlddmkm.sys",
             "gpu_driver:nvlddmkm.sys", "r_gpu_rollback", "2026-11-06T10:00:00+09:00",
             "s7_gpu_driver_after_fix.json");
  std::cout << "[세션] s5 과열: 팬 속도 점검 → 팬 곡선 조정 → 스로틀링 재발 검증\n";
  runFixFlow("s5_thermal_fps_drop.json", "fan_curve_test@thermal:thermal", "thermal:thermal",
             "r_fan_curve", "2026-11-02T20:55:00+09:00", "s5_thermal_after_fix.json");
}

void testSessionGuards() {
  std::cout << "[세션] 잘못된 호출 막기\n";
  const Rules rules = Rules::defaults();
  Session s;
  std::string err;
  Session::start(loadJson("s3_av_launch_fail.json"), rules, EngineVersion::V1, ExecutionPolicy(), "",
                 &s, &err);
  check(!s.applyFix("", 0, &err), "원인 확정 전 해결책 적용 거부");
  check(!s.reportCheck("없는점검", "solved", &err), "없는 점검 id 거부");
  const int next = s.nextCheckIndex();
  const std::string id = next >= 0 ? s.checkInstances()[next].check_id : "";
  check(!s.reportCheck(id, "maybe", &err), "잘못된 result 값 거부");
  check(s.reportCheck(id, "skipped", &err), "할 수 없는 점검은 skipped 로 건너뜀", err);
  check(!s.reportCheck(id, "solved", &err), "같은 점검을 두 번 보고하면 거부");

  json::Value noSymptom = loadJson("s3_av_launch_fail.json");
  json::Value sym = json::Value::makeObject();
  noSymptom.set("symptom", sym);
  Session bad;
  check(!Session::start(noSymptom, rules, EngineVersion::V1, ExecutionPolicy(), "", &bad, &err),
        "증상 유형 없는 스냅샷은 세션 시작 거부");
}

void testBayes() {
  std::cout << "[베이즈 갱신]\n";
  std::vector<double> b;
  b.push_back(0.5);
  b.push_back(0.3);
  b.push_back(0.2);
  std::vector<char> in(3, 0);
  in[0] = 1;
  const std::vector<double> neg = bayesUpdate(b, in, 0.9, false);
  check(neg[0] < 0.5 && neg[1] > 0.3, "'아님' → 커버 후보 확률 감소");
  const std::vector<double> pos = bayesUpdate(b, in, 0.9, true);
  check(pos[0] > 0.85, "'해결됨' → 커버 후보 확률 증가", std::to_string(pos[0]));
  double sum = 0;
  for (std::size_t i = 0; i < neg.size(); i++) sum += neg[i];
  check(std::fabs(sum - 1.0) < 1e-9, "합 = 1 유지");
}

void testFeedback() {
  std::cout << "[피드백 학습]\n";
  Rules r = Rules::defaults();
  const Diagnosis d = diagnoseFile("s1_anticheat_crash.json", ExecutionPolicy());
  const FeedbackResult up = applyFeedback(&r, d.symptom, *d.top(), true);
  check(up.ok && up.weight_after > up.weight_before, "해결됨 → 가중치 증가");
  const FeedbackResult down = applyFeedback(&r, d.symptom, *d.top(), false);
  check(down.weight_after < down.weight_before, "아님 → 가중치 감소");
}

void testTool() {
  std::cout << "[도구 인터페이스]\n";
  json::Value specs = toolSpecs();
  check(specs.size() == 7, "도구 7종 정의", std::to_string(specs.size()));

  json::Value args = json::Value::makeObject();
  args.set("snapshot_path", json::Value(g_data + "/samples/s1_anticheat_crash.json"));
  args.set("mode", json::Value("auto"));
  json::Value result;
  std::string err;
  check(runTool("diagnose", args, &result, &err), "diagnose 도구", err);
  check(result["plan"]["steps"][0]["action"]["execution"].asString() != "",
        "단계별 action.execution 포함");

  const std::string path = "ce_test_session.json";
  json::Value start = json::Value::makeObject();
  start.set("session_path", json::Value(path));
  start.set("snapshot_path", json::Value(g_data + "/samples/s1_anticheat_crash.json"));
  check(runTool("session_start", start, &result, &err), "session_start 도구", err);
  const std::string firstCheck = result["next_check"]["check_id"].asString();
  check(!firstCheck.empty(), "첫 점검 안내");

  json::Value rep = json::Value::makeObject();
  rep.set("session_path", json::Value(path));
  rep.set("check_id", json::Value("pause_anticheat@anticheat:vgk.sys"));
  rep.set("result", json::Value("solved"));
  check(runTool("session_report", rep, &result, &err), "session_report 도구", err);
  check(result["status"].asString() == "confirmed", "도구 호출로 원인 확정",
        result["status"].asString());

  json::Value fix = json::Value::makeObject();
  fix.set("session_path", json::Value(path));
  fix.set("applied_at", json::Value("2026-10-15T20:55:00+09:00"));
  check(runTool("session_apply_fix", fix, &result, &err), "session_apply_fix 도구", err);

  json::Value ver = json::Value::makeObject();
  ver.set("session_path", json::Value(path));
  ver.set("after_snapshot_path", json::Value(g_data + "/samples/s1_anticheat_after_fix.json"));
  check(runTool("session_verify", ver, &result, &err), "session_verify 도구", err);
  check(result["verify"]["outcome"].asString() == "resolved", "도구 호출로 해결 확인");
  std::remove(path.c_str());

  json::Value bad = json::Value::makeObject();
  check(!runTool("session_status", bad, &result, &err), "필수 인자 없으면 오류");
  check(!runTool("없는도구", bad, &result, &err), "없는 도구 이름은 오류");
}

void testBench() {
  std::cout << "[검증 지표]\n";
  std::vector<std::string> paths;
  const char* names[] = {"s1_anticheat_crash.json",   "s2_finsec_input_loss.json",
                         "s3_av_launch_fail.json",    "s4_display_util_flicker.json",
                         "s5_thermal_fps_drop.json",  "s6_mvp_av_hook_crash.json",
                         "s7_gpu_driver_bsod.json",   "s8_net_driver_drop.json",
                         "s9_power_reboot.json",      "s10_memory_whea.json",
                         "s11_storage_freeze.json"};
  for (int i = 0; i < 11; i++) paths.push_back(g_data + "/samples/" + names[i]);
  const BenchSummary sum = runBench(paths, Rules::defaults());
  check(sum.n == 11, "시나리오 11건", std::to_string(sum.n));
  check(sum.n_stage[1] == 4 && sum.n_stage[2] == 3 && sum.n_stage[3] == 4,
        "단계별 1차 4 / 2차 3 / 3차 4건");
  check(sum.top1_stage[1] == 4 && sum.top1_stage[2] == 3 && sum.top1_stage[3] == 4,
        "단계별 1순위 정답 수");
  check(sum.layer_accuracy >= 0.99, "층 판정 11/11", percentText(sum.layer_accuracy));
  check(sum.top1 >= 0.99, "Top-1 11/11", percentText(sum.top1));
  check(sum.avg_cost_v1 < sum.avg_cost_base, "v1 누적 비용 < 커뮤니티 표준 순서");
  check(sum.avg_exp_v1 <= sum.avg_exp_v0 + 1e-6, "v1 기대 비용 <= v0 기대 비용");
}

}  // namespace

int main(int argc, char** argv) {
  g_data = argc > 1 ? argv[1] : "data";
  testJson();
  testRules();
  testScenarios();
  testDriverHardwareScenarios();
  testExecutionPolicy();
  testSessionEndToEnd();
  testSessionRecurrence();
  testDriverHardwareSessions();
  testSessionGuards();
  testBayes();
  testFeedback();
  testTool();
  testBench();
  std::cout << "\n결과: " << g_passed << " 통과 / " << g_failed << " 실패\n";
  return g_failed == 0 ? 0 : 1;
}
