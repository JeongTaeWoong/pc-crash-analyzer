// signals.cpp - 층 판정(layer.cpp)과 후보 점수(candidates.cpp)가 함께 쓰는 '신호 판별' 함수 모음.
//   [팀원용] "이 이벤트가 크래시 이벤트인가?", "증상 직전에 설치됐나?(시간 상관)", "센서에서 과열이 보였나?"
//   같은 질문에 답하는 작은 함수들이다. 새 신호를 추가할 때는 여기에 판별 함수를 만들고
//   layer.cpp / candidates.cpp 에서 불러 쓰면 된다.

#include "ce/signals.hpp"

#include <algorithm>
#include <cmath>

namespace ce {
namespace signals {

long long referenceTime(const Snapshot& s) {
  if (s.symptom_started_at != 0) return s.symptom_started_at;
  return s.marked_at;
}

double timeCorrelation(long long changeTime, long long symptomTime, const Rules& r) {
  if (changeTime == 0 || symptomTime == 0) return 0.0;
  const double deltaMin = (symptomTime - changeTime) / 60.0;  // 양수 = 변경이 먼저
  if (deltaMin < 0) {
    // 증상보다 나중에 생긴 변경은 원인이 아니다. 다만 시계 오차를 감안해 30분까지만 관용.
    if (deltaMin > -30.0) return 0.2;
    return 0.0;
  }
  if (deltaMin <= r.time_full_credit_minutes) return 1.0;
  if (deltaMin >= r.time_window_minutes) return 0.0;
  const double span = r.time_window_minutes - r.time_full_credit_minutes;
  const double x = (deltaMin - r.time_full_credit_minutes) / span;  // 0~1
  return std::pow(1.0 - x, 1.5);  // 멀어질수록 완만하게 감소
}

bool isAppCrashEvent(const EventItem& e) {
  return e.id == 1000 || e.id == 1001 || containsNoCase(e.provider, "application error");
}

bool isDisplayEvent(const EventItem& e) {
  return e.id == 4101 || containsNoCase(e.provider, "display") ||
         containsNoCase(e.provider, "nvlddmkm") || containsNoCase(e.provider, "amdkmdag");
}

bool isKernelPower41(const EventItem& e) {
  return e.id == 41 && containsNoCase(e.provider, "kernel-power");
}

bool isWheaEvent(const EventItem& e) { return containsNoCase(e.provider, "whea"); }

bool isDiskEvent(const EventItem& e) {
  if (containsNoCase(e.provider, "disk") || containsNoCase(e.provider, "ntfs") ||
      containsNoCase(e.provider, "storahci")) {
    return true;
  }
  return e.id == 7 || e.id == 11 || e.id == 51 || e.id == 153;
}

bool isDisconnectEvent(const EventItem& e) {
  return containsNoCase(e.provider, "usb") || containsNoCase(e.provider, "monitor") ||
         containsNoCase(e.provider, "pnp") || containsNoCase(e.subject, "disconnect");
}

bool isMemoryEvent(const EventItem& e) {
  return containsNoCase(e.provider, "memory") || containsNoCase(e.provider, "bugcheck");
}

namespace {
void addUnique(std::vector<std::string>* list, const std::string& value) {
  if (value.empty()) return;
  const std::string v = toLower(value);
  for (std::size_t i = 0; i < list->size(); i++) {
    if ((*list)[i] == v) return;
  }
  list->push_back(v);
}
}  // namespace

std::vector<std::string> distinctFaultingModules(const Snapshot& s) {
  std::vector<std::string> out;
  for (std::size_t i = 0; i < s.faulting_modules.size(); i++) {
    addUnique(&out, s.faulting_modules[i]);
  }
  for (std::size_t i = 0; i < s.events.size(); i++) {
    if (isAppCrashEvent(s.events[i])) addUnique(&out, s.events[i].faulting_module);
  }
  for (std::size_t i = 0; i < s.minidumps.size(); i++) {
    addUnique(&out, s.minidumps[i].faulting_module);
  }
  std::sort(out.begin(), out.end());
  return out;
}

double maxCpuTemp(const Snapshot& s) {
  double t = -1;
  for (std::size_t i = 0; i < s.sensors.size(); i++) {
    if (s.sensors[i].cpu_temp > t) t = s.sensors[i].cpu_temp;
  }
  return t;
}

double maxGpuTemp(const Snapshot& s) {
  double t = -1;
  for (std::size_t i = 0; i < s.sensors.size(); i++) {
    if (s.sensors[i].gpu_temp > t) t = s.sensors[i].gpu_temp;
  }
  return t;
}

bool throttlingSeen(const Snapshot& s) {
  for (std::size_t i = 0; i < s.sensors.size(); i++) {
    if (s.sensors[i].throttling) return true;
    if (equalsNoCase(s.sensors[i].gpu_power_limit_reason, "thermal")) return true;
  }
  return false;
}

bool clockDropMatchesFrameDrop(const Snapshot& s, long long* whenOut) {
  if (s.sensors.size() < 3 || s.frames.size() < 3) return false;

  // 프레임타임이 중앙값의 1.8배를 넘는 첫 구간을 찾는다.
  std::vector<double> sorted;
  for (std::size_t i = 0; i < s.frames.size(); i++) sorted.push_back(s.frames[i].frame_ms);
  std::sort(sorted.begin(), sorted.end());
  const double median = sorted[sorted.size() / 2];

  long long dropAt = 0;
  for (std::size_t i = 0; i < s.frames.size(); i++) {
    if (s.frames[i].frame_ms > median * 1.8) {
      dropAt = s.frames[i].time;
      break;
    }
  }
  if (dropAt == 0) return false;

  // 같은 시각(±20초) 부근에서 클럭이 처음 값보다 15% 이상 떨어졌는가
  double firstClock = -1;
  double dropClock = -1;
  for (std::size_t i = 0; i < s.sensors.size(); i++) {
    const SensorSample& x = s.sensors[i];
    const double clock = x.gpu_clock > 0 ? x.gpu_clock : x.cpu_clock;
    if (clock <= 0) continue;
    if (firstClock < 0) firstClock = clock;
    long long diff = x.time - dropAt;
    if (diff < 0) diff = -diff;
    if (diff <= 20) dropClock = clock;
  }
  if (firstClock <= 0 || dropClock <= 0) return false;
  if (dropClock >= firstClock * 0.85) return false;
  if (whenOut) *whenOut = dropAt;
  return true;
}

bool isMicrosoftModule(const Snapshot& s, const std::string& moduleName) {
  for (std::size_t i = 0; i < s.modules.size(); i++) {
    if (equalsNoCase(s.modules[i].name, moduleName)) return s.modules[i].microsoft_signed;
  }
  // 모듈 목록에 없으면 윈도우 기본 모듈 이름만 예외 처리한다.
  const char* systemModules[] = {"ntdll.dll",  "kernelbase.dll", "kernel32.dll",
                                 "user32.dll", "win32u.dll",     "ucrtbase.dll"};
  for (int i = 0; i < 6; i++) {
    if (equalsNoCase(moduleName, systemModules[i])) return true;
  }
  return false;
}

bool isWindowsClassFilter(const std::string& name) {
  const std::string n = normalizeName(name);
  return n == "kbdclass" || n == "mouclass";
}

int thirdPartyInputFilterCount(const Snapshot& s) {
  int n = 0;
  for (std::size_t i = 0; i < s.device_filters.keyboard_upper.size(); i++) {
    if (!isWindowsClassFilter(s.device_filters.keyboard_upper[i])) n++;
  }
  for (std::size_t i = 0; i < s.device_filters.mouse_upper.size(); i++) {
    if (!isWindowsClassFilter(s.device_filters.mouse_upper[i])) n++;
  }
  return n;
}

std::vector<int> detectionsOnTarget(const Snapshot& s) {
  std::vector<int> out;
  for (std::size_t i = 0; i < s.detections.size(); i++) {
    const SecurityDetection& d = s.detections[i];
    if (s.target_process.empty() || sameSubject(d.target, s.target_process) ||
        containsNoCase(d.target, normalizeName(s.target_process))) {
      out.push_back(static_cast<int>(i));
    }
  }
  return out;
}

int crashEventsSince(const Snapshot& s, long long since) {
  int n = 0;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    const EventItem& e = s.events[i];
    if (!isAppCrashEvent(e) && e.id != 1002) continue;  // 1002 = 응답 없음(프리징)
    if (e.time != 0 && e.time < since) continue;
    if (!s.target_process.empty() && !e.subject.empty() &&
        !equalsNoCase(e.subject, s.target_process)) {
      continue;
    }
    n++;
  }
  return n;
}

}  // namespace signals
}  // namespace ce
