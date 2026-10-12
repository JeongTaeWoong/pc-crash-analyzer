// main.cpp - 추론 엔진 CLI
//   diagnose      : 스냅샷 1건을 진단하고 리포트/JSON 출력
//   session ...   : 증상 → 원인 확인 → 해결 안내 → 검증을 한 단계씩 진행 (사람이 직접 쓸 때)
//   tool ...      : 에이전트·UI용 JSON 도구 인터페이스 (stdin/stdout)
//   feedback      : '해결됨/아님' 피드백을 사례 DB에 반영
//   bench         : ground_truth 가 있는 시나리오들로 4.2 지표 측정
//   export-rules  : 내장 기본 규칙을 JSON으로 내보내기
//
// [팀원용] 이 파일은 명령줄 인자를 읽어 각 기능을 부르기만 한다. 실제 로직은 engine.cpp / session.cpp 에 있다.
//   session 명령은 내부에서 tool.cpp 의 runTool() 을 그대로 불러서, 사람이 쓰는 명령과 에이전트가 쓰는 도구가
//   항상 같은 결과를 내도록 했다.
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <iterator>
#include <sstream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

#include "ce/bench.hpp"
#include "ce/engine.hpp"
#include "ce/feedback.hpp"
#include "ce/session.hpp"
#include "ce/tool.hpp"

namespace {

using namespace ce;

void printUsage() {
  std::cout
      << "사용법:\n"
         "  crash_engine diagnose <snapshot.json> [--rules r.json] [--engine v0|v1]\n"
         "                        [--mode guide|auto] [--allow a_id,b_id] [--json out.json] [--compare]\n"
         "\n"
         "  crash_engine session start <snapshot.json> --session s.json [--mode guide|auto]\n"
         "                        [--allow a_id,b_id] [--rules r.json] [--engine v0|v1]\n"
         "  crash_engine session status    --session s.json [--json]\n"
         "  crash_engine session report    --session s.json --check <check_id> --result solved|not_solved|skipped\n"
         "  crash_engine session apply-fix --session s.json [--remedy <remedy_id>] [--at <ISO-8601>]\n"
         "  crash_engine session verify    --session s.json --after after.json [--save-rules]\n"
         "  crash_engine session verify    --session s.json --recurred yes|no --minutes N [--save-rules]\n"
         "\n"
         "  crash_engine tool list\n"
         "  crash_engine tool <name> [--input args.json]     (인자가 없으면 표준입력에서 JSON)\n"
         "\n"
         "  crash_engine feedback <snapshot.json> --candidate <id> --result solved|not_solved\n"
         "                        [--rules r.json] [--save r.json]\n"
         "  crash_engine bench <scenario1.json> [scenario2.json ...] [--rules r.json] [--json out.json]\n"
         "  crash_engine export-rules <out.json>\n";
}

struct Args {
  std::string command;
  std::string sub;  // session / tool 의 하위 명령
  std::vector<std::string> positional;
  std::string rules_path, out_json, save_path, candidate, result, engine = "v1";
  std::string mode = "guide", allow, session_path, check_id, remedy_id, at, after_path;
  std::string recurred, minutes, input_path;
  bool compare = false;
  bool json_out = false;
  bool save_rules = false;
};

bool takeValue(int argc, char** argv, int* i, std::string* dst) {
  if (*i + 1 >= argc) return false;
  *i += 1;
  *dst = argv[*i];
  return true;
}

bool parseArgs(int argc, char** argv, Args* a) {
  if (argc < 2) return false;
  a->command = argv[1];
  int start = 2;
  if ((a->command == "session" || a->command == "tool") && argc >= 3) {
    a->sub = argv[2];
    start = 3;
  }
  for (int i = start; i < argc; i++) {
    const std::string s = argv[i];
    bool ok = true;
    if (s == "--rules") ok = takeValue(argc, argv, &i, &a->rules_path);
    else if (s == "--json" && a->command == "session") a->json_out = true;
    else if (s == "--json") ok = takeValue(argc, argv, &i, &a->out_json);
    else if (s == "--save") ok = takeValue(argc, argv, &i, &a->save_path);
    else if (s == "--candidate") ok = takeValue(argc, argv, &i, &a->candidate);
    else if (s == "--result") ok = takeValue(argc, argv, &i, &a->result);
    else if (s == "--engine") ok = takeValue(argc, argv, &i, &a->engine);
    else if (s == "--mode") ok = takeValue(argc, argv, &i, &a->mode);
    else if (s == "--allow") ok = takeValue(argc, argv, &i, &a->allow);
    else if (s == "--session") ok = takeValue(argc, argv, &i, &a->session_path);
    else if (s == "--check") ok = takeValue(argc, argv, &i, &a->check_id);
    else if (s == "--remedy") ok = takeValue(argc, argv, &i, &a->remedy_id);
    else if (s == "--at") ok = takeValue(argc, argv, &i, &a->at);
    else if (s == "--after") ok = takeValue(argc, argv, &i, &a->after_path);
    else if (s == "--recurred") ok = takeValue(argc, argv, &i, &a->recurred);
    else if (s == "--minutes") ok = takeValue(argc, argv, &i, &a->minutes);
    else if (s == "--input") ok = takeValue(argc, argv, &i, &a->input_path);
    else if (s == "--compare") a->compare = true;
    else if (s == "--save-rules") a->save_rules = true;
    else if (s.size() > 2 && s[0] == '-' && s[1] == '-') {
      std::cerr << "알 수 없는 옵션: " << s << "\n";
      return false;
    } else {
      a->positional.push_back(s);
    }
    if (!ok) {
      std::cerr << s << " 뒤에 값이 필요합니다.\n";
      return false;
    }
  }
  return true;
}

std::vector<std::string> splitComma(const std::string& text) {
  std::vector<std::string> out;
  std::string cur;
  for (std::size_t i = 0; i < text.size(); i++) {
    if (text[i] == ',') {
      if (!cur.empty()) out.push_back(cur);
      cur.clear();
    } else if (text[i] != ' ') {
      cur += text[i];
    }
  }
  if (!cur.empty()) out.push_back(cur);
  return out;
}

json::Value stringArray(const std::vector<std::string>& list) {
  json::Value a = json::Value::makeArray();
  for (std::size_t i = 0; i < list.size(); i++) a.push(json::Value(list[i]));
  return a;
}

bool loadRules(const Args& a, Rules* rules) {
  if (a.rules_path.empty()) {
    *rules = Rules::defaults();
    return true;
  }
  std::string err;
  if (!Rules::load(a.rules_path, rules, &err)) {
    std::cerr << "규칙 파일을 읽을 수 없습니다: " << err << "\n";
    return false;
  }
  return true;
}

// ---------------------------------------------------------------- diagnose

int cmdDiagnose(const Args& a) {
  if (a.positional.empty()) {
    printUsage();
    return 2;
  }
  Rules rules;
  if (!loadRules(a, &rules)) return 1;

  bool ok = true;
  const EngineVersion version = engineVersionFromString(a.engine, &ok);
  if (!ok) {
    std::cerr << "--engine 값은 v0 또는 v1 이어야 합니다.\n";
    return 2;
  }
  ExecutionPolicy policy;
  policy.mode = executionModeFromString(a.mode, &ok);
  if (!ok) {
    std::cerr << "--mode 값은 guide 또는 auto 여야 합니다.\n";
    return 2;
  }
  policy.allowed_actions = splitComma(a.allow);

  Snapshot snap;
  std::vector<std::string> warn;
  std::string err;
  if (!loadSnapshotFile(a.positional[0], &snap, &warn, &err)) {
    std::cerr << "스냅샷을 읽을 수 없습니다: " << err << "\n";
    return 1;
  }

  const Diagnosis d = diagnose(snap, rules, version, policy, warn);
  std::cout << d.toText();

  if (a.compare) {
    const EngineVersion other = version == EngineVersion::V1 ? EngineVersion::V0 : EngineVersion::V1;
    const Diagnosis d2 = diagnose(snap, rules, other, policy, warn);
    const Plan base = buildBaselinePlan(d.candidates, rules, d.symptom);
    // 기대 총비용 = 안내한 점검들의 기대 비용 + 원인을 못 찾을 확률 x 못 찾았을 때 비용
    std::cout << "\n[순서 비교] 기대 총비용 (낮을수록 좋음, 원인을 못 찾는 경우는 "
              << rules.unresolved_cost_minutes << "분으로 계산)\n";
    const Plan* plans[3] = {&base, &d2.plan, &d.plan};
    const std::string names[3] = {"커뮤니티 표준 순서", "엔진 " + toString(d2.plan.version) + "            ",
                                  "엔진 " + toString(d.plan.version) + "            "};
    for (int i = 0; i < 3; i++) {
      char line[160];
      std::snprintf(line, sizeof(line), " / 원인 찾을 확률 %.0f%% / 기대 시도 %.1f회",
                    plans[i]->resolved_probability * 100.0, plans[i]->expected_trials);
      std::cout << "  " << names[i] << " : " << durationText(plans[i]->expected_total_seconds) << line
                << "\n";
    }
  }

  if (!a.out_json.empty()) {
    std::string werr;
    if (!json::Value::writeFile(a.out_json, d.toJson(), &werr)) {
      std::cerr << werr << "\n";
      return 1;
    }
    std::cout << "\nJSON 저장: " << a.out_json << "\n";
  }
  return 0;
}

// ---------------------------------------------------------------- session (사람용)

int printSession(const std::string& path, bool asJson) {
  Session s;
  Rules rules;
  std::string err;
  if (!loadSessionFile(path, Rules::defaults(), &s, &rules, &err)) {
    std::cerr << err << "\n";
    return 1;
  }
  if (asJson) {
    std::cout << s.view().dump(2) << "\n";
  } else {
    std::cout << s.viewText();
  }
  return 0;
}

int cmdSession(const Args& a) {
  if (a.session_path.empty()) {
    std::cerr << "--session <세션 파일> 이 필요합니다.\n";
    return 2;
  }
  json::Value args = json::Value::makeObject();
  args.set("session_path", json::Value(a.session_path));
  std::string tool;

  if (a.sub == "start") {
    if (a.positional.empty()) {
      printUsage();
      return 2;
    }
    tool = "session_start";
    args.set("snapshot_path", json::Value(a.positional[0]));
    args.set("engine", json::Value(a.engine));
    args.set("mode", json::Value(a.mode));
    args.set("allowed_actions", stringArray(splitComma(a.allow)));
    if (!a.rules_path.empty()) args.set("rules_path", json::Value(a.rules_path));
  } else if (a.sub == "status") {
    return printSession(a.session_path, a.json_out);
  } else if (a.sub == "report") {
    tool = "session_report";
    args.set("check_id", json::Value(a.check_id));
    args.set("result", json::Value(a.result));
  } else if (a.sub == "apply-fix") {
    tool = "session_apply_fix";
    if (!a.remedy_id.empty()) args.set("remedy_id", json::Value(a.remedy_id));
    if (!a.at.empty()) args.set("applied_at", json::Value(a.at));
  } else if (a.sub == "verify") {
    tool = "session_verify";
    if (!a.after_path.empty()) {
      args.set("after_snapshot_path", json::Value(a.after_path));
    } else if (!a.recurred.empty()) {
      args.set("recurred", json::Value(a.recurred == "yes" || a.recurred == "true"));
      args.set("observed_minutes", json::Value(std::atof(a.minutes.c_str())));
    }
    args.set("save_rules", json::Value(a.save_rules));
  } else {
    printUsage();
    return 2;
  }

  json::Value result;
  std::string err;
  if (!runTool(tool, args, &result, &err)) {
    std::cerr << "오류: " << err << "\n";
    return 1;
  }
  return printSession(a.session_path, a.json_out);
}

// ---------------------------------------------------------------- tool (에이전트용)

int cmdTool(const Args& a) {
  std::string name = a.sub;
  if (name.empty()) {
    printUsage();
    return 2;
  }
  if (name == "list") name = "list_tools";

  json::Value args = json::Value::makeObject();
  std::string text;
  if (!a.input_path.empty()) {
    if (!json::readTextFile(a.input_path, &text)) {
      std::cerr << "입력 파일을 열 수 없습니다: " << a.input_path << "\n";
      return 2;
    }
  } else if (name != "list_tools") {
    std::istreambuf_iterator<char> begin(std::cin);
    std::istreambuf_iterator<char> end;
    text.assign(begin, end);
  }
  bool blank = true;
  for (std::size_t i = 0; i < text.size(); i++) {
    if (text[i] != ' ' && text[i] != '\n' && text[i] != '\r' && text[i] != '\t') blank = false;
  }
  if (!blank) {
    std::string perr;
    args = json::Value::parse(text, &perr);
    if (!perr.empty() || !args.isObject()) {
      std::cout << toolResponse(name, false, json::Value(), "입력 JSON 오류: " + perr).dump(2) << "\n";
      return 1;
    }
  }

  json::Value result;
  std::string err;
  const bool ok = runTool(name, args, &result, &err);
  std::cout << toolResponse(name, ok, result, err).dump(2) << "\n";
  return ok ? 0 : 1;
}

// ---------------------------------------------------------------- feedback / bench / export

int cmdFeedback(const Args& a) {
  if (a.positional.empty() || a.candidate.empty() || a.result.empty()) {
    printUsage();
    return 2;
  }
  if (a.result != "solved" && a.result != "not_solved") {
    std::cerr << "--result 값은 solved 또는 not_solved 이어야 합니다.\n";
    return 2;
  }
  Rules rules;
  if (!loadRules(a, &rules)) return 1;

  Snapshot snap;
  std::vector<std::string> warn;
  std::string err;
  if (!loadSnapshotFile(a.positional[0], &snap, &warn, &err)) {
    std::cerr << "스냅샷을 읽을 수 없습니다: " << err << "\n";
    return 1;
  }
  const Diagnosis d = diagnose(snap, rules, EngineVersion::V1, ExecutionPolicy(), warn);
  const int idx = findCandidate(d.candidates, a.candidate);
  if (idx < 0) {
    std::cerr << "후보 id 를 찾을 수 없습니다: " << a.candidate << "\n";
    return 1;
  }
  const FeedbackResult fr = applyFeedback(&rules, d.symptom, d.candidates[idx], a.result == "solved");
  std::cout << fr.message << "\n";

  const std::string savePath = a.save_path.empty() ? a.rules_path : a.save_path;
  if (savePath.empty()) {
    std::cout << "(--save 또는 --rules 가 없어 파일로 저장하지 않았습니다)\n";
    return 0;
  }
  std::string werr;
  if (!rules.save(savePath, &werr)) {
    std::cerr << werr << "\n";
    return 1;
  }
  std::cout << "규칙 저장: " << savePath << "\n";
  return 0;
}

int cmdBench(const Args& a) {
  if (a.positional.empty()) {
    printUsage();
    return 2;
  }
  Rules rules;
  if (!loadRules(a, &rules)) return 1;
  const BenchSummary sum = runBench(a.positional, rules);
  std::cout << sum.toText();
  if (!a.out_json.empty()) {
    std::string werr;
    if (!json::Value::writeFile(a.out_json, sum.toJson(), &werr)) {
      std::cerr << werr << "\n";
      return 1;
    }
    std::cout << "JSON 저장: " << a.out_json << "\n";
  }
  return sum.n > 0 ? 0 : 1;
}

int cmdExportRules(const Args& a) {
  if (a.positional.empty()) {
    printUsage();
    return 2;
  }
  const Rules rules = Rules::defaults();
  std::string err;
  if (!rules.save(a.positional[0], &err)) {
    std::cerr << err << "\n";
    return 1;
  }
  std::cout << "기본 규칙을 저장했습니다: " << a.positional[0] << "\n";
  return 0;
}

}  // namespace

int main(int argc, char** argv) {
#ifdef _WIN32
  SetConsoleOutputCP(CP_UTF8);  // 한글 출력이 깨지지 않도록
  SetConsoleCP(CP_UTF8);
#endif
  Args a;
  if (!parseArgs(argc, argv, &a)) {
    printUsage();
    return 2;
  }
  if (a.command == "diagnose") return cmdDiagnose(a);
  if (a.command == "session") return cmdSession(a);
  if (a.command == "tool") return cmdTool(a);
  if (a.command == "feedback") return cmdFeedback(a);
  if (a.command == "bench") return cmdBench(a);
  if (a.command == "export-rules") return cmdExportRules(a);
  printUsage();
  return 2;
}
