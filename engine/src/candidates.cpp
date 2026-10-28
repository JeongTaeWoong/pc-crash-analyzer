#include "ce/candidates.hpp"

#include <algorithm>
#include <cmath>
#include <map>

#include "ce/signals.hpp"

namespace ce {

namespace {

// ─────────────────────────────────────────────────────────────────────────────
// [2단계] 원인 후보 만들기와 점수 매기기
//
// 흐름:
//   (1) 스냅샷의 여러 곳(로드된 DLL, 드라이버, 필터, 백신, 방화벽, 설치 이력, 크래시 덤프 ...)을 돌면서
//       "이게 범인일 수 있다"는 후보를 CandidateBag 에 넣고, 왜 그런지(근거, Evidence)를 붙인다.
//       같은 대상이 여러 곳에서 나오면 하나로 합친다. (vgk.sys 드라이버 + Riot Vanguard 설치 이력 = 한 후보)
//   (2) buildCandidates() 에서 근거들을 합쳐 점수를 계산한다.
//
//       점수 = (사전 비중 + 시간 상관 + 공존 상관 + 사례 DB) × 층 보정
//
//       - 사전 비중 : 이 증상에서 이 종류가 원인인 경우가 원래 얼마나 많은가 (rules.json priors)
//       - 시간 상관 : 증상이 시작되기 직전에 설치·변경되었는가 (가까울수록 1에 가까움)
//       - 공존 상관 : 문제 순간에 그 자리에 있었는가 (대상 프로그램에 로드됨, 덤프 스택에 있음 등)
//       - 사례 DB   : 예전에 같은 증상에서 실제로 범인이었던 비율
//       - 층 보정   : 1단계에서 확률이 낮은 층의 후보는 점수를 깎는다
//
//   (3) 모든 후보 점수의 합으로 나누어 확률로 만들고, 높은 순으로 정렬한다.
// ─────────────────────────────────────────────────────────────────────────────

// 후보 id 에 쓰는 이름: 소문자 + 공백은 '_' (명령줄에서 따옴표 없이 쓸 수 있게)
std::string idPart(const std::string& subject) {
  std::string out = toLower(subject);
  for (std::size_t i = 0; i < out.size(); i++) {
    if (out[i] == ' ') out[i] = '_';
  }
  return out;
}

// 표시명에서 제품 이름만 꺼낸다: "Riot Vanguard (커널 안티치트)" -> "Riot Vanguard"
std::string productOf(const std::string& label) {
  const std::size_t pos = label.find(" (");
  if (pos == std::string::npos) return label;
  return label.substr(0, pos);
}

// 후보를 id로 모으는 그릇. 같은 대상이 여러 경로(모듈·드라이버·이력)에서 나와도 한 건으로 합친다.
// 벡터가 늘어나면 참조가 무효가 되므로, 항상 '인덱스'로 주고받는다.
// [설명] CandidateBag 은 '후보를 담는 가방'이다.
//   get()         : (카테고리, 이름)으로 후보를 찾고, 없으면 새로 만들어 그 번호(인덱스)를 돌려준다.
//   addEvidence() : 그 후보에 근거 한 줄을 붙인다.
//   vector 에 원소가 추가되면 기존 참조(&)가 깨질 수 있어서, 참조 대신 '번호'로 주고받는다.
class CandidateBag {
 public:
  explicit CandidateBag(const Rules& r) : rules_(r) {}

  int get(const std::string& category, const std::string& subject, const std::string& label) {
    const std::string id = category + ":" + idPart(subject);
    std::map<std::string, int>::const_iterator it = index_.find(id);
    if (it != index_.end()) return it->second;

    // 같은 카테고리 안에서 이름이 사실상 같은 후보가 이미 있으면 그쪽에 합친다.
    const int similar = findSimilar(category, subject, label);
    if (similar >= 0) return similar;

    Candidate c;
    c.id = id;
    c.category = category;
    c.subject = subject;
    c.label = label.empty() ? subject : label;
    c.product = productOf(c.label);
    c.layer = layerOfCategory(category);
    c.stage = rules_.stageOf(category);
    c.in_focus = rules_.inFocus(category);
    items_.push_back(c);
    const int idx = static_cast<int>(items_.size()) - 1;
    index_[id] = idx;
    return idx;
  }

  void addEvidence(int idx, const std::string& kind, const std::string& detail, double contrib,
                   long long time) {
    Candidate& c = items_[idx];
    for (std::size_t i = 0; i < c.evidence.size(); i++) {
      if (c.evidence[i].kind == kind && c.evidence[i].detail == detail) return;  // 같은 근거는 한 번만
    }
    c.evidence.push_back(Evidence(kind, detail, contrib, time));
  }

  std::vector<Candidate>& items() { return items_; }

 private:
  int findSimilar(const std::string& category, const std::string& subject,
                  const std::string& label) const {
    if (normalizeName(subject).size() < 3) return -1;
    // 힌트로 제품을 알아낸 표시명은 "제품 (구성요소)" 꼴이다. 같은 제품의 드라이버·DLL 여러 개는 원인 하나로 본다
    // (10/29 Blender 실험: AhnLab Safe Transaction 커널 드라이버 5개가 각각 후보가 되어 확률을 나눠 가졌음).
    const std::string product = productOf(label);
    const bool hasProduct = product != label && normalizeName(product).size() >= 3;
    for (std::size_t i = 0; i < items_.size(); i++) {
      if (items_[i].category != category) continue;
      if (sameSubject(subject, items_[i].subject) || sameSubject(label, items_[i].label)) {
        return static_cast<int>(i);
      }
      if (hasProduct && items_[i].label != items_[i].product &&
          normalizeName(items_[i].product) == normalizeName(product)) {
        return static_cast<int>(i);
      }
    }
    return -1;
  }

  const Rules& rules_;
  std::vector<Candidate> items_;
  std::map<std::string, int> index_;
};

// 하드웨어 후보의 표시 이름 (카테고리마다 한 후보만 둔다)
std::string hardwareLabel(const std::string& category) {
  if (category == "thermal") return "냉각·온도 문제(스로틀링)";
  if (category == "power") return "전원·전력 공급 문제";
  if (category == "connection") return "케이블·장착 접촉 불량";
  if (category == "storage") return "저장장치 이상";
  if (category == "memory") return "메모리 이상";
  return category;
}

void addFromModules(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  for (std::size_t i = 0; i < s.modules.size(); i++) {
    const ModuleInfo& m = s.modules[i];
    if (m.microsoft_signed) continue;
    std::string cat;
    std::string label;
    r.classifyName(m.name + " " + m.company, m.name, "other_sw", &cat, &label);
    const int idx = bag.get(cat, m.name, label);
    const bool inTarget = s.target_process.empty() || equalsNoCase(m.process, s.target_process);
    if (inTarget) {
      bag.addEvidence(idx, "coexist",
                      "문제 시점에 대상 프로세스(" + m.process + ")에 로드되어 있었습니다.", 0.6,
                      m.loaded_at);
    } else {
      bag.addEvidence(idx, "coexist", "문제 시점에 시스템에 로드되어 있었습니다(" + m.process + ").",
                      0.2, m.loaded_at);
    }
  }
}

void addFromDrivers(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  for (std::size_t i = 0; i < s.drivers.size(); i++) {
    const DriverInfo& d = s.drivers[i];
    std::string fallback = "other_sw";
    if (containsNoCase(d.type, "display")) {
      fallback = "gpu_driver";
    } else if (containsNoCase(d.type, "network")) {
      fallback = "network_driver";
    } else if (!containsNoCase(d.type, "kernel")) {
      fallback = "other_driver";
    }
    std::string cat;
    std::string label;
    r.classifyName(d.name + " " + d.display + " " + d.company, d.display.empty() ? d.name : d.display,
                   fallback, &cat, &label);
    const int idx = bag.get(cat, d.name, label);
    const std::string type = d.type.empty() ? "kernel" : d.type;
    bag.addEvidence(idx, "coexist", "커널 드라이버로 상주 중입니다 (" + type + ").", 0.5,
                    d.started_at);
  }
}

void addFilterList(const std::vector<std::string>& list, const std::string& where,
                   const std::string& fallback, double weight, const Rules& r, CandidateBag& bag) {
  for (std::size_t i = 0; i < list.size(); i++) {
    if (signals::isWindowsClassFilter(list[i])) continue;  // Windows 기본 클래스 드라이버
    std::string cat;
    std::string label;
    r.classifyName(list[i], list[i], fallback, &cat, &label);
    const int idx = bag.get(cat, list[i], label);
    bag.addEvidence(idx, "coexist", where + "에 필터 드라이버로 등록되어 있습니다.", weight, 0);
  }
}

void addFromDeviceFilters(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  const double inputWeight = (s.symptom == Symptom::InputLoss) ? 0.7 : 0.25;
  addFilterList(s.device_filters.keyboard_upper, "키보드 클래스(UpperFilters)", "input_util",
                inputWeight, r, bag);
  addFilterList(s.device_filters.mouse_upper, "마우스 클래스(UpperFilters)", "input_util",
                inputWeight, r, bag);
  const double netWeight = (s.symptom == Symptom::NetworkFail) ? 0.7 : 0.25;
  addFilterList(s.device_filters.ndis_filters, "네트워크 어댑터(NDIS 필터)", "network_driver",
                netWeight, r, bag);
}

void addFromSecurity(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  // 실시간 백신
  for (std::size_t i = 0; i < s.realtime_av.size(); i++) {
    std::string cat;
    std::string label;
    r.classifyName(s.realtime_av[i], s.realtime_av[i], "antivirus", &cat, &label);
    const int idx = bag.get(cat, s.realtime_av[i], label);
    const bool duplicate = s.realtime_av.size() > 1;
    bag.addEvidence(idx, "coexist", "실시간 감시가 켜져 있습니다.", duplicate ? 0.5 : 0.35, 0);
    if (duplicate) {
      bag.addEvidence(idx, "coexist", "실시간 감시 백신이 2개 이상 동시에 동작 중입니다.", 0.3, 0);
    }
  }

  // 방화벽 차단 기록
  for (std::size_t i = 0; i < s.firewall_blocks.size(); i++) {
    const FirewallBlock& f = s.firewall_blocks[i];
    if (!s.target_process.empty() && !equalsNoCase(f.process, s.target_process)) continue;
    const std::string name = f.product.empty() ? "보안 방화벽" : f.product;
    std::string cat;
    std::string label;
    // 방화벽 기록은 '그 제품의 방화벽 기능'이 원인이므로 security_firewall 로 묶는다.
    r.classifyName(name + " firewall", name, "security_firewall", &cat, &label);
    if (cat != "security_firewall") {
      cat = "security_firewall";
      label = name + " 방화벽";
    }
    const int idx = bag.get(cat, name, label);
    bag.addEvidence(idx, "coexist",
                    "대상 프로그램의 통신(" + f.direction + ")을 차단한 기록이 있습니다.", 0.8,
                    f.time);
  }

  // 탐지·격리 기록: 실행 불가(launch_fail)에서 가장 직접적인 근거
  const std::vector<int> dets = signals::detectionsOnTarget(s);
  for (std::size_t i = 0; i < dets.size(); i++) {
    const SecurityDetection& d = s.detections[dets[i]];
    std::string cat;
    std::string label;
    r.classifyName(d.product, d.product, "antivirus", &cat, &label);
    const int idx = bag.get(cat, d.product, label);
    std::string text = "대상 파일(" + d.target + ")을 " + d.action + " 처리했습니다";
    if (!d.threat.empty()) text += " (탐지명 " + d.threat + ")";
    bag.addEvidence(idx, "detection", text + ".", 0.95, d.time);
  }
}

void addFromChanges(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  const long long ref = signals::referenceTime(s);
  for (std::size_t i = 0; i < s.changes.size(); i++) {
    const ChangeItem& ch = s.changes[i];
    double corr = signals::timeCorrelation(ch.time, ref, r);
    if (corr <= 0.05) continue;
    // 대상 프로그램 자신의 업데이트는 충돌 '상대'가 아니다(엔진 노트로 따로 안내한다).
    if (!s.target_process.empty() && sameSubject(ch.subject, s.target_process)) continue;
    // 브라우저·런처처럼 스스로 업데이트되는 프로그램의 '설치' 기록은 사용자 행동이 아닐 가능성이 커 가중을 1/4 로
    const bool autoNoise = r.isAutoUpdateNoise(ch.subject);
    if (autoNoise) corr *= 0.25;
    std::string fallback = "other_sw";
    if (ch.kind == "os_update") {
      fallback = "os_update";
    } else if (ch.kind == "driver_update") {
      fallback = "other_driver";
    }
    std::string cat;
    std::string label;
    r.classifyName(ch.subject, ch.subject, fallback, &cat, &label);
    // 하드웨어 설정 변경(예: 메모리 XMP)은 그 하드웨어 후보 하나로 합친다.
    std::string subject = ch.subject;
    if (layerOfCategory(cat) == Layer::Hardware) {
      subject = cat;
      label = hardwareLabel(cat);
    }
    const int idx = bag.get(cat, subject, label);
    const std::string when =
        ch.time != 0 ? json::formatIso8601(ch.time, s.tz_offset_minutes) : "시각 미상";
    const std::string verb = ch.kind == "install" ? "설치" : "변경";
    std::string detail = "증상 시작 직전에 " + verb + "되었습니다 (" + when;
    if (!ch.version.empty()) detail += ", " + ch.version;
    detail += ").";
    if (autoNoise) detail += " 자동 업데이트 프로그램이라 시간 상관 가중을 낮췄습니다.";
    bag.addEvidence(idx, "time_corr", detail, corr, ch.time);
  }
  // 베이스라인 대비 추가된 항목도 동일하게 다룬다(설치 이력 레지스트리 누락 대비).
  for (std::size_t i = 0; i < s.baseline.added.size(); i++) {
    const std::string& added = s.baseline.added[i];
    std::string cat;
    std::string label;
    r.classifyName(added, added, "other_sw", &cat, &label);
    const int idx = bag.get(cat, added, label);
    bag.addEvidence(idx, "time_corr", "설치 직후 베이스라인에는 없던 항목입니다(이후 추가됨).",
                    0.55, 0);
  }
}

// 모듈 이름으로 스냅샷 모듈 목록에서 제조사를 찾는다(오류 모듈 분류를 모듈 목록과 일치시키기 위해).
std::string companyOfModule(const Snapshot& s, const std::string& name) {
  for (std::size_t i = 0; i < s.modules.size(); i++) {
    if (equalsNoCase(s.modules[i].name, name)) return s.modules[i].company;
  }
  return "";
}

void addFromCrashModules(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  // (1) 앱 크래시의 오류 모듈: 매번 다르면 특정 모듈을 지목할 수 없으므로 1종일 때만 후보로 올린다.
  //     .sys(커널 드라이버)는 아래 (3) 블루스크린 쪽에서 따로 다룬다.
  const std::vector<std::string> all = signals::distinctFaultingModules(s);
  std::vector<std::string> faulting;
  for (std::size_t i = 0; i < all.size(); i++) {
    if (!signals::isDriverModuleName(all[i])) faulting.push_back(all[i]);
  }
  if (faulting.size() == 1 && !signals::isMicrosoftModule(s, faulting[0])) {
    std::string cat;
    std::string label;
    r.classifyName(faulting[0] + " " + companyOfModule(s, faulting[0]), faulting[0], "other_sw", &cat,
                   &label);
    const int idx = bag.get(cat, faulting[0], label);
    bag.addEvidence(idx, "event", "크래시의 오류 모듈로 기록되었습니다.", 0.9, 0);
  }

  // (2) 미니덤프 스택에 남은 서드파티 모듈. 오류 모듈이 ntdll 처럼 시스템 모듈이어도
  //     실제로 호출 경로에 끼어든 모듈을 짚어낼 수 있다.
  for (std::size_t i = 0; i < s.minidumps.size(); i++) {
    const MiniDumpInfo& d = s.minidumps[i];
    for (std::size_t k = 0; k < d.third_party_in_stack.size(); k++) {
      const std::string& mod = d.third_party_in_stack[k];
      if (mod.empty() || signals::isMicrosoftModule(s, mod)) continue;
      std::string cat;
      std::string label;
      r.classifyName(mod + " " + companyOfModule(s, mod), mod, "other_sw", &cat, &label);
      const int idx = bag.get(cat, mod, label);
      bag.addEvidence(idx, "event", "크래시 덤프의 호출 스택에 이 모듈이 있습니다 (" + mod + ").",
                      0.75, d.time);
    }
  }

  // (3) 블루스크린 커널 덤프의 원인 드라이버. 하드웨어 버그체크(0x124 등)는 드라이버 탓이 아니므로
  //     여기서 제외하고 하드웨어 후보 쪽 근거로 쓴다.
  for (std::size_t i = 0; i < s.minidumps.size(); i++) {
    const MiniDumpInfo& d = s.minidumps[i];
    if (!signals::isKernelDump(d) || signals::isHardwareBugcheck(d.bugcheck)) continue;
    if (d.faulting_module.empty() || signals::isMicrosoftModule(s, d.faulting_module)) continue;
    std::string display = d.faulting_module;
    std::string company;
    for (std::size_t k = 0; k < s.drivers.size(); k++) {
      if (equalsNoCase(s.drivers[k].name, d.faulting_module)) {
        display = s.drivers[k].display.empty() ? s.drivers[k].name : s.drivers[k].display;
        company = s.drivers[k].company;
      }
    }
    std::string cat;
    std::string label;
    r.classifyName(d.faulting_module + " " + display + " " + company, display, "other_driver", &cat, &label);
    const int idx = bag.get(cat, d.faulting_module, label);
    bag.addEvidence(idx, "event", "블루스크린(" + d.bugcheck + ")의 원인 드라이버로 기록되었습니다.", 0.95,
                    d.time);
  }
}

// 수치 표시용 (소수 1자리)
std::string fmt1(double v) {
  char buf[32];
  std::snprintf(buf, sizeof(buf), "%.1f", v);
  return buf;
}
std::string fmt2(double v) {
  char buf[32];
  std::snprintf(buf, sizeof(buf), "%.2f", v);
  return buf;
}

// 수집기 v0.6 의 60분 자원 추세(resource_trends)로 느려짐 메커니즘 후보를 만든다 (docs/slowdown-mechanisms.md 2절 분기표).
//   A 앱 메모리 증가 : 초점 프로세스의 Private Bytes 가 분당 5MB 이상 단조 증가(R² ≥ 0.7) → 그 프로그램 자체(other_sw 등)
//   C 열 스로틀     : GPU 최고 온도 83℃ 이상이면서 최저 클럭이 최고의 75% 미만 → thermal
//   E 외부 간섭     : 초점이 아닌 프로세스가 60분 평균 CPU 10% 이상 → 그 프로세스(hints 로 백신·금융보안 분류)
// 느려짐(slow)이 아닌 증상에서도 근거는 붙이되 기여도를 절반으로 — 크래시 직전 메모리 폭증은 참고 정보다.
void addFromResourceTrends(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  const ResourceTrends& t = s.trends;
  if (!t.available) return;
  const double scale = (s.symptom == Symptom::Slow) ? 1.0 : 0.5;

  // A. 초점 프로세스 메모리 증가
  if (t.has_focus && t.focus.samples >= 10 && t.focus.priv_slope_mb_per_min >= 5.0 && t.focus.priv_r2 >= 0.7) {
    // 프로그램 '자체'의 메모리 누적은 충돌 상대가 아니라 app_memory 범주(저장 후 재시작·캐시 비우기 처방)
    const int idx = bag.get("app_memory", t.focus.name, t.focus.name + " (프로그램 자체의 메모리 누적)");
    bag.addEvidence(idx, "sensor",
                    "최근 " + std::to_string(t.window_minutes) + "분 동안 메모리가 분당 " +
                        fmt1(t.focus.priv_slope_mb_per_min) + "MB 씩 꾸준히 늘었습니다 (" +
                        fmt1(t.focus.priv_mb_start) + "→" + fmt1(t.focus.priv_mb_now) + "MB, R² " +
                        fmt2(t.focus.priv_r2) + ").",
                    0.8 * scale, 0);
    if (t.commit_pct_now >= 85) {
      bag.addEvidence(idx, "sensor",
                      "시스템 커밋 메모리가 " + fmt1(t.commit_pct_now) + "% 로 한계에 가깝습니다.", 0.5 * scale, 0);
    }
    if (t.focus.handle_slope_per_min >= 2.0) {
      bag.addEvidence(idx, "sensor", "핸들 수도 분당 " + fmt1(t.focus.handle_slope_per_min) + "개씩 늘고 있습니다 (누수 의심).",
                      0.3 * scale, 0);
    }
  }
  // 초점이 아닌 프로세스가 강하게 늘어나는 경우 (지정 대상이 틀렸거나 배경 프로그램이 원인) — 10/29 Blender 실험에서 필요
  for (std::size_t i = 0; i < t.memory_growers.size(); i++) {
    const ProcTrend& g = t.memory_growers[i];
    if (t.has_focus && (g.pid == t.focus.pid || sameSubject(g.name, t.focus.name))) continue;
    if (g.samples < 10 || g.priv_slope_mb_per_min < 10.0 || g.priv_r2 < 0.8) continue;
    const int idx = bag.get("app_memory", g.name, g.name + " (프로그램 자체의 메모리 누적)");
    bag.addEvidence(idx, "sensor",
                    "최근 " + std::to_string(t.window_minutes) + "분 동안 이 프로세스의 메모리가 분당 " +
                        fmt1(g.priv_slope_mb_per_min) + "MB 씩 늘었습니다 (R² " + fmt2(g.priv_r2) + ", 초점 프로그램 아님).",
                    0.6 * scale, 0);
  }
  if (!t.has_focus && t.commit_pct_now >= 85 && !t.memory_growers.empty() &&
             t.memory_growers[0].priv_slope_mb_per_min >= 5.0) {
    // 초점이 없을 때(수동 마킹에서 전경을 못 잡은 경우) 가장 빨리 늘어난 프로세스를 올린다
    const ProcTrend& g = t.memory_growers[0];
    const int idx = bag.get("app_memory", g.name, g.name + " (프로그램 자체의 메모리 누적)");
    bag.addEvidence(idx, "sensor",
                    "커밋 메모리 " + fmt1(t.commit_pct_now) + "% 상태에서 이 프로세스의 메모리가 분당 " +
                        fmt1(g.priv_slope_mb_per_min) + "MB 씩 늘었습니다.",
                    0.6 * scale, 0);
  }

  // B. GPU 메모리(VRAM) 압박: 최근 60분 최대 사용률 90% 이상 — 초점 프로그램(없으면 GPU) 에 근거를 단다
  if (t.gpu_mem_pct_max >= 90.0 && t.gpu_mem_total_mb > 0) {
    const std::string detail = "GPU 메모리가 총량 " + fmt1(t.gpu_mem_total_mb / 1024.0) + "GB 의 " +
                               fmt1(t.gpu_mem_pct_max) + "% 까지 찼습니다" +
                               (t.gpu_name.empty() ? "" : " (" + t.gpu_name + ")") +
                               ". 넘치면 시스템 메모리로 밀려나 급격히 느려집니다.";
    if (t.has_focus) {
      const int idx = bag.get("app_memory", t.focus.name, t.focus.name + " (프로그램 자체의 메모리 누적)");
      bag.addEvidence(idx, "sensor", detail, 0.6 * scale, 0);
    } else {
      std::string cat;
      std::string label;
      r.classifyName(t.gpu_name, t.gpu_name.empty() ? "GPU" : t.gpu_name, "gpu_driver", &cat, &label);
      const int idx = bag.get(cat, t.gpu_name.empty() ? "gpu" : t.gpu_name, label);
      bag.addEvidence(idx, "sensor", detail, 0.4 * scale, 0);
    }
  }

  // C. GPU 열 스로틀 (온도↑ 클럭↓ 공존)
  if (t.gpu_temp_max >= 83 && t.gpu_clock_max > 0 && t.gpu_clock_min >= 0 &&
      t.gpu_clock_min < 0.75 * t.gpu_clock_max) {
    const int idx = bag.get("thermal", "thermal", hardwareLabel("thermal"));
    bag.addEvidence(idx, "sensor",
                    "최근 " + std::to_string(t.window_minutes) + "분 GPU 최고 " + fmt1(t.gpu_temp_max) +
                        "℃, 클럭이 " + fmt1(t.gpu_clock_max) + "→" + fmt1(t.gpu_clock_min) + "MHz 로 떨어졌습니다.",
                    0.7 * scale, 0);
  }

  // E. 외부 간섭: 초점이 아닌 상주 프로세스의 CPU 점유
  for (std::size_t i = 0; i < t.top_cpu.size(); i++) {
    const ProcTrend& p = t.top_cpu[i];
    if (p.cpu_avg < 10.0 || p.samples < 10) continue;
    if (t.has_focus && (p.pid == t.focus.pid || sameSubject(p.name, t.focus.name))) continue;
    if (!s.target_process.empty() && sameSubject(p.name, s.target_process)) continue;
    std::string cat;
    std::string label;
    r.classifyName(p.name, p.name, "other_sw", &cat, &label);
    const int idx = bag.get(cat, p.name, label);
    bag.addEvidence(idx, "coexist",
                    "배경에서 최근 " + std::to_string(t.window_minutes) + "분 평균 CPU " + fmt1(p.cpu_avg) +
                        "% 를 계속 점유했습니다.",
                    0.6 * scale, 0);
  }

  // CPU 속도 제한 이벤트(Kernel-Processor-Power 37): Microsoft 는 펌웨어·전원 정책 원인을 들므로 power 쪽에 둔다
  int limited = 0;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    if (s.events[i].id == 37 && containsNoCase(s.events[i].provider, "Processor-Power")) limited++;
  }
  if (limited > 0) {
    const int idx = bag.get("power", "power", hardwareLabel("power"));
    bag.addEvidence(idx, "event",
                    "프로세서 속도가 시스템 펌웨어에 의해 제한된 기록이 " + std::to_string(limited) + "건 있습니다 (이벤트 37).",
                    0.5 * scale, 0);
  }
}

// 조건(pred)에 맞는 이벤트 수. firstTime 이 있으면 처음 나온 이벤트 시각을 적어 준다.
int countEvents(const Snapshot& s, bool (*pred)(const EventItem&), long long* firstTime) {
  int n = 0;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    if (!pred(s.events[i])) continue;
    if (n == 0 && firstTime) *firstTime = s.events[i].time;
    n++;
  }
  return n;
}

// [설명] 하드웨어 후보 5종(냉각·전원·연결·저장장치·메모리)
//   카테고리마다 후보를 하나씩 두고, 스냅샷에 그 부품을 가리키는 신호가 있을 때만 공존 근거를 붙인다.
//   신호가 없으면 근거 없이 사전 가중치(priors)만 남아 확률이 낮게 나온다.
//   근거 값(0.4~0.95)은 '얼마나 그 부품을 직접 가리키는가' 순서다. 예) 메모리 WHEA 0.9 > 매번 다른 오류 모듈 0.4
void addHardwareCandidates(const Snapshot& s, CandidateBag& bag) {
  const bool sensorsOk = s.available.sensors;
  const double cpu = signals::maxCpuTemp(s);
  const double gpu = signals::maxGpuTemp(s);

  // 온도
  {
    const int idx = bag.get("thermal", "thermal", hardwareLabel("thermal"));
    if (sensorsOk && signals::throttlingSeen(s)) {
      bag.addEvidence(idx, "sensor", "스로틀링(온도·전력 제한) 구간이 관측되었습니다.", 0.9, 0);
    }
    if (sensorsOk && (cpu >= 90 || gpu >= 84)) {
      bag.addEvidence(idx, "sensor",
                      "최고 온도 CPU " + std::to_string(static_cast<int>(cpu)) + "℃ / GPU " +
                          std::to_string(static_cast<int>(gpu)) + "℃.",
                      0.6, 0);
    }
    long long when = 0;
    if (sensorsOk && signals::clockDropMatchesFrameDrop(s, &when)) {
      bag.addEvidence(idx, "sensor", "프레임 저하와 클럭 하락 시각이 일치합니다.", 0.95, when);
    }
    if (!sensorsOk) {
      bag.addEvidence(idx, "sensor", "센서 값을 읽을 수 없어 온도는 확인 불가입니다.", 0.0, 0);
    }
  }
  // 전원: 블루스크린 뒤 재부팅이 아닌 '진짜' 갑작스러운 전원 끊김만 센다.
  {
    const int idx = bag.get("power", "power", hardwareLabel("power"));
    int losses = 0;
    long long first = 0;
    for (std::size_t i = 0; i < s.events.size(); i++) {
      if (!signals::isPowerLossEvent(s, s.events[i])) continue;
      if (losses == 0) first = s.events[i].time;
      losses++;
    }
    if (losses > 0) {
      bag.addEvidence(idx, "event",
                      "블루스크린 없이 갑자기 꺼진 기록(Kernel-Power 41)이 " + std::to_string(losses) +
                          "번 있습니다.",
                      losses >= 2 ? 0.95 : 0.85, first);
    }
    for (std::size_t i = 0; i < s.sensors.size(); i++) {
      if (equalsNoCase(s.sensors[i].gpu_power_limit_reason, "power")) {
        bag.addEvidence(idx, "sensor", "GPU가 전력 제한에 걸린 구간이 있습니다.", 0.6, s.sensors[i].time);
        break;
      }
    }
  }
  // 연결·장착: 장치 연결 해제, PCIe 버스 WHEA 오류
  {
    const int idx = bag.get("connection", "connection", hardwareLabel("connection"));
    long long first = 0;
    const int n = countEvents(s, signals::isDisconnectEvent, &first);
    if (n > 0) {
      bag.addEvidence(idx, "event", "장치 연결 해제·재연결이 " + std::to_string(n) + "번 기록되었습니다.",
                      0.8, first);
    }
    for (std::size_t i = 0; i < s.events.size(); i++) {
      if (signals::wheaComponent(s.events[i]) == "pcie") {
        bag.addEvidence(idx, "event", "PCIe 버스 WHEA 오류가 기록되었습니다(그래픽카드 장착·라이저 케이블 의심).",
                        0.6, s.events[i].time);
        break;
      }
    }
  }
  // 저장장치: SMART 경고, 디스크 I/O 오류
  {
    const int idx = bag.get("storage", "storage", hardwareLabel("storage"));
    for (std::size_t i = 0; i < s.disks.size(); i++) {
      const DiskHealth& d = s.disks[i];
      if (!signals::diskHasWarning(d)) continue;
      bag.addEvidence(idx, "sensor",
                      "SMART 경고: " + d.name + " (재할당 " + std::to_string(d.reallocated_sectors) +
                          ", 대기 " + std::to_string(d.pending_sectors) + ", 복구 불가 " +
                          std::to_string(d.uncorrectable) + ").",
                      0.9, 0);
    }
    long long first = 0;
    const int n = countEvents(s, signals::isDiskEvent, &first);
    if (n > 0) {
      bag.addEvidence(idx, "event", "저장장치 I/O 오류가 " + std::to_string(n) + "번 기록되었습니다.",
                      n >= 3 ? 0.9 : 0.8, first);
    }
  }
  // 메모리: 메모리 WHEA, 하드웨어 버그체크, 여러 프로그램에서 오류 모듈이 매번 다름
  {
    const int idx = bag.get("memory", "memory", hardwareLabel("memory"));
    long long first = 0;
    const int memWhea = countEvents(s, signals::isMemoryEvent, &first);
    if (memWhea > 0) {
      bag.addEvidence(idx, "event", "메모리 영역 WHEA 오류가 " + std::to_string(memWhea) + "번 기록되었습니다.",
                      0.9, first);
    } else {
      for (std::size_t i = 0; i < s.events.size(); i++) {
        const std::string comp = signals::wheaComponent(s.events[i]);
        if (signals::isWheaEvent(s.events[i]) && comp != "pcie") {
          bag.addEvidence(idx, "event", "부품을 특정하지 못한 WHEA 하드웨어 오류가 기록되었습니다.", 0.5,
                          s.events[i].time);
          break;
        }
      }
    }
    for (std::size_t i = 0; i < s.minidumps.size(); i++) {
      if (signals::isKernelDump(s.minidumps[i]) && signals::isHardwareBugcheck(s.minidumps[i].bugcheck)) {
        bag.addEvidence(idx, "event", "하드웨어 오류 블루스크린(" + s.minidumps[i].bugcheck + ")이 있습니다.",
                        0.6, s.minidumps[i].time);
        break;
      }
    }
    const std::vector<std::string> faulting = signals::distinctFaultingModules(s);
    if (faulting.size() >= 3 && s.scope != SymptomScope::SingleApp) {
      bag.addEvidence(idx, "event",
                      "여러 프로그램에서 오류 모듈이 매번 다릅니다(" + std::to_string(faulting.size()) + "종).",
                      0.4, 0);
    }
  }
}

// 드라이버 관련 이벤트를 해당 드라이버 후보에 붙인다 (디스플레이 복구, 네트워크 링크 끊김).
void addDriverEventEvidence(const Snapshot& s, const Rules& r, CandidateBag& bag) {
  long long firstDisplay = 0;
  const int display = countEvents(s, signals::isDisplayEvent, &firstDisplay);
  long long firstLink = 0;
  const int link = countEvents(s, signals::isNetworkLinkEvent, &firstLink);
  if (display == 0 && link == 0) return;
  for (std::size_t i = 0; i < s.drivers.size(); i++) {
    const DriverInfo& d = s.drivers[i];
    const bool isDisplay = containsNoCase(d.type, "display");
    const bool isNet = containsNoCase(d.type, "network");
    if (!(isDisplay && display > 0) && !(isNet && link > 0)) continue;
    std::string cat;
    std::string label;
    r.classifyName(d.name + " " + d.display + " " + d.company, d.display.empty() ? d.name : d.display,
                   isDisplay ? "gpu_driver" : "network_driver", &cat, &label);
    const int idx = bag.get(cat, d.name, label);
    if (isDisplay) {
      bag.addEvidence(idx, "event",
                      "디스플레이 드라이버 응답 중지·복구(4101)가 " + std::to_string(display) + "번 있습니다.",
                      display >= 2 ? 0.7 : 0.5, firstDisplay);
    } else {
      bag.addEvidence(idx, "event", "네트워크 링크 끊김이 " + std::to_string(link) + "번 있습니다.",
                      link >= 2 ? 0.6 : 0.4, firstLink);
    }
  }
}

bool candidateGreater(const Candidate& a, const Candidate& b) {
  if (a.score != b.score) return a.score > b.score;
  return a.id < b.id;
}

}  // namespace

int findCandidate(const std::vector<Candidate>& list, const std::string& id) {
  for (std::size_t i = 0; i < list.size(); i++) {
    if (list[i].id == id) return static_cast<int>(i);
  }
  return -1;
}

json::Value Candidate::toJson(int tzOffsetMinutes) const {
  json::Value o = json::Value::makeObject();
  o.set("id", json::Value(id));
  o.set("label", json::Value(label));
  o.set("subject", json::Value(subject));
  o.set("product", json::Value(product));
  o.set("category", json::Value(category));
  o.set("layer", json::Value(toString(layer)));
  o.set("in_focus", json::Value(in_focus));
  o.set("stage", json::Value(stage));
  o.set("score", json::Value(score));
  o.set("probability", json::Value(probability));
  o.set("confidence", json::Value(confidence));
  json::Value parts = json::Value::makeObject();
  parts.set("prior", json::Value(s_prior));
  parts.set("time_correlation", json::Value(s_time));
  parts.set("coexistence", json::Value(s_coexist));
  parts.set("case_db", json::Value(s_case));
  parts.set("layer_gate", json::Value(layer_gate));
  o.set("score_parts", parts);
  o.set("evidence", evidenceListToJson(evidence, tzOffsetMinutes));
  return o;
}

std::vector<Candidate> buildCandidates(const Snapshot& s, const Rules& r,
                                       const LayerResult& layer) {
  CandidateBag bag(r);
  addFromModules(s, r, bag);
  addFromDrivers(s, r, bag);
  addFromDeviceFilters(s, r, bag);
  addFromSecurity(s, r, bag);
  addFromChanges(s, r, bag);
  addFromCrashModules(s, r, bag);
  addDriverEventEvidence(s, r, bag);
  addHardwareCandidates(s, bag);
  addFromResourceTrends(s, r, bag);

  const std::string symptom = toString(s.symptom);
  const double availability = availabilityFactor(s);

  std::vector<Candidate> out;
  std::vector<Candidate>& items = bag.items();
  for (std::size_t i = 0; i < items.size(); i++) {
    Candidate c = items[i];

    // [설명] 근거(evidence)들을 종류별로 모아 점수 성분을 만든다.
    //   s1 의 Vanguard 예: 커널 상주 0.5 + 덤프 스택 0.75 → 공존 1.2(상한), 베이스라인 추가 0.55 → 시간 0.55
    // 성분 집계: 공존은 합(상한 있음), 시간 상관은 최댓값
    double coexist = 0;
    double timeCorr = 0;
    int strongEvidence = 0;
    for (std::size_t k = 0; k < c.evidence.size(); k++) {
      const Evidence& e = c.evidence[k];
      if (e.kind == "coexist" || e.kind == "sensor" || e.kind == "event" ||
          e.kind == "detection") {
        coexist += e.contribution;
      } else if (e.kind == "time_corr") {
        if (e.contribution > timeCorr) timeCorr = e.contribution;
      }
      if (e.contribution >= 0.5) strongEvidence++;
    }
    c.s_coexist = std::min(1.2, coexist);
    c.s_time = timeCorr;
    c.s_prior = r.prior(symptom, c.category);

    const int caseIdx = r.findCaseIndex(symptom, c.category, c.subject);
    if (caseIdx >= 0) {
      const CaseEntry& e = r.cases[caseIdx];
      const int total = e.solved + e.rejected;
      // 피드백이 쌓일수록 시드 가중치보다 실제 해결률을 더 믿는다.
      double observed = e.weight;
      double trust = 0.0;
      if (total > 0) {
        observed = static_cast<double>(e.solved) / total;
        trust = std::min(1.0, total / 5.0);
      }
      c.s_case = e.weight * (1 - trust) + observed * trust;
      if (total > 0) {
        c.evidence.push_back(Evidence("case_db",
                                      "사례 DB: 같은 증상에서 해결 " + std::to_string(e.solved) +
                                          "건 / 아님 " + std::to_string(e.rejected) + "건.",
                                      c.s_case, 0));
      }
    }

    // 층 판정 보정: 층 확률이 낮은 후보는 강하게 눌린다(불필요한 점검을 건너뛰게 함).
    c.layer_gate = 0.25 + r.w_layer * layer.probabilityOf(c.layer);

    c.score = (r.w_prior * c.s_prior + r.w_time * c.s_time + r.w_coexist * c.s_coexist +
               r.w_case * c.s_case) *
              c.layer_gate;

    if (c.score <= 1e-6) continue;  // 근거도 사전 확률도 없는 후보는 버린다
    c.confidence = std::min(0.95, (0.25 + 0.18 * strongEvidence) * availability);
    out.push_back(c);
  }

  double total = 0;
  for (std::size_t i = 0; i < out.size(); i++) total += out[i].score;

  for (std::size_t i = 0; i < out.size(); i++) {
    Candidate& c = out[i];
    c.probability = total > 0 ? c.score / total : 0;
    // 최종 신뢰도는 순위 확률도 함께 반영한다.
    c.confidence = std::min(0.95, c.confidence + 0.25 * c.probability);
    // 근거 그래프에 실제 반영된 기여분으로 갱신
    for (std::size_t k = 0; k < c.evidence.size(); k++) {
      Evidence& e = c.evidence[k];
      if (e.kind == "time_corr") {
        e.contribution *= r.w_time * c.layer_gate;
      } else if (e.kind == "case_db") {
        e.contribution *= r.w_case * c.layer_gate;
      } else {
        e.contribution *= r.w_coexist * c.layer_gate;
      }
    }
    c.evidence.push_back(Evidence("prior", "증상 '" + symptom + "' 에서 이 유형의 사전 발생 비중.",
                                  r.w_prior * c.s_prior * c.layer_gate, 0));
    c.evidence.push_back(
        Evidence("layer",
                 "층 판정 " + toString(c.layer) + " 확률 " +
                     std::to_string(static_cast<int>(std::lround(layer.probabilityOf(c.layer) * 100))) +
                     "% 를 반영했습니다.",
                 c.layer_gate, 0));
  }

  std::sort(out.begin(), out.end(), candidateGreater);
  return out;
}

}  // namespace ce
