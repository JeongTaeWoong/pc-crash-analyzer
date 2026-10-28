// snapshot.cpp - 수집기가 만든 스냅샷 JSON 을 Snapshot 구조체로 바꾼다.
//   [팀원용] 수집기 쪽에서 필드를 빼먹어도 프로그램이 멈추지 않는다. 없는 필드는 '확인 불가'로 표시되고
//   (available.xxx = false) 그만큼 신뢰도가 내려갈 뿐이다. 필드 이름과 형식은 docs/snapshot_schema.md 참고.
//   시각은 "2026-10-14T21:30:00+09:00" 같은 문자열을 받아 내부에서는 초 단위 정수(epoch)로 바꿔 쓴다.

#include "ce/snapshot.hpp"

#include <cctype>

namespace ce {

// ---------------------------------------------------------------- 문자열 도우미

std::string toLower(const std::string& s) {
  std::string out = s;
  for (std::size_t i = 0; i < out.size(); i++) {
    unsigned char c = static_cast<unsigned char>(out[i]);
    if (c < 0x80) out[i] = static_cast<char>(std::tolower(c));  // 한글(UTF-8)은 그대로
  }
  return out;
}

bool containsNoCase(const std::string& haystack, const std::string& needle) {
  if (needle.empty()) return false;
  return toLower(haystack).find(toLower(needle)) != std::string::npos;
}

bool equalsNoCase(const std::string& a, const std::string& b) { return toLower(a) == toLower(b); }

namespace {

long long readTime(const json::Value& v, std::vector<std::string>* warn,
                   const std::string& field) {
  if (v.isNull()) return 0;
  if (v.isNumber()) return static_cast<long long>(v.asNumber());
  long long t = 0;
  if (json::parseIso8601(v.asString(), &t)) return t;
  if (warn) warn->push_back("시각 형식을 해석할 수 없습니다: " + field + " = " + v.asString());
  return 0;
}

bool looksMicrosoft(const std::string& signer, const std::string& company) {
  return containsNoCase(signer + " " + company, "microsoft");
}

}  // namespace

int Availability::countCore() const {
  int n = 0;
  if (modules) n++;
  if (drivers) n++;
  if (events) n++;
  if (sensors) n++;
  if (change_history) n++;
  if (baseline) n++;
  if (device_filters) n++;
  return n;
}

std::string toString(Layer l) {
  if (l == Layer::Software) return "SW";
  if (l == Layer::Driver) return "DRIVER";
  if (l == Layer::Hardware) return "HW";
  return "UNKNOWN";
}

Layer layerFromString(const std::string& s) {
  if (s == "SW" || s == "software") return Layer::Software;
  if (s == "DRIVER" || s == "driver") return Layer::Driver;
  if (s == "HW" || s == "hardware") return Layer::Hardware;
  return Layer::Unknown;
}

std::string toString(Symptom s) {
  if (s == Symptom::AppCrash) return "app_crash";
  if (s == Symptom::Freeze) return "freeze";
  if (s == Symptom::FpsDrop) return "fps_drop";
  if (s == Symptom::Flicker) return "flicker";
  if (s == Symptom::InputLoss) return "input_loss";
  if (s == Symptom::LaunchFail) return "launch_fail";
  if (s == Symptom::NetworkFail) return "network_fail";
  if (s == Symptom::UnexpectedReboot) return "unexpected_reboot";
  if (s == Symptom::Slow) return "slow";
  return "unknown";
}

std::string toString(SymptomScope s) {
  if (s == SymptomScope::SingleApp) return "single_app";
  if (s == SymptomScope::MultipleApps) return "multiple_apps";
  if (s == SymptomScope::SystemWide) return "system_wide";
  return "unknown";
}

Symptom symptomFromString(const std::string& s) {
  if (s == "app_crash") return Symptom::AppCrash;
  if (s == "freeze") return Symptom::Freeze;
  if (s == "fps_drop") return Symptom::FpsDrop;
  if (s == "flicker") return Symptom::Flicker;
  if (s == "input_loss") return Symptom::InputLoss;
  if (s == "launch_fail") return Symptom::LaunchFail;
  if (s == "network_fail") return Symptom::NetworkFail;
  if (s == "unexpected_reboot") return Symptom::UnexpectedReboot;
  if (s == "slow") return Symptom::Slow;
  return Symptom::Unknown;
}

SymptomScope scopeFromString(const std::string& s) {
  if (s == "single_app") return SymptomScope::SingleApp;
  if (s == "multiple_apps") return SymptomScope::MultipleApps;
  if (s == "system_wide") return SymptomScope::SystemWide;
  return SymptomScope::Unknown;
}

std::vector<int> Snapshot::moduleIndexesOf(const std::string& process) const {
  std::vector<int> out;
  for (std::size_t i = 0; i < modules.size(); i++) {
    if (process.empty() || equalsNoCase(modules[i].process, process)) {
      out.push_back(static_cast<int>(i));
    }
  }
  return out;
}

bool Snapshot::hasEvent(int id) const { return countEvents(id) > 0; }

int Snapshot::countEvents(int id) const {
  int n = 0;
  for (std::size_t i = 0; i < events.size(); i++) {
    if (events[i].id == id) n++;
  }
  return n;
}

bool Snapshot::processRunning(const std::string& name) const {
  for (std::size_t i = 0; i < processes.size(); i++) {
    if (equalsNoCase(processes[i], name)) return true;
  }
  return false;
}

namespace {
ProcTrend parseProcTrend(const json::Value& p) {
  ProcTrend t;
  t.pid = p["pid"].asInt(0);
  t.name = p["name"].asString("");
  t.samples = p["samples"].asInt(0);
  t.priv_mb_now = p["privMBNow"].asNumber(-1);
  t.priv_mb_start = p["privMBStart"].asNumber(-1);
  t.priv_slope_mb_per_min = p["privSlopeMBPerMin"].asNumber(0);
  t.priv_r2 = p["privR2"].asNumber(-1);
  t.handle_slope_per_min = p["handleSlopePerMin"].asNumber(0);
  t.cpu_avg = p["cpuAvg"].asNumber(-1);
  return t;
}
}  // namespace

Snapshot parseSnapshot(const json::Value& v, std::vector<std::string>* warn) {
  Snapshot s;
  s.schema_version = v["schema_version"].asString("0");
  s.snapshot_id = v["snapshot_id"].asString("(이름 없음)");
  s.marked_at = readTime(v["marked_at"], warn, "marked_at");
  if (v["marked_at"].isString()) {
    long long tmp = 0;
    int off = 0;
    if (json::parseIso8601(v["marked_at"].asString(), &tmp, &off)) s.tz_offset_minutes = off;
  }
  s.trigger_kind = v["trigger"]["kind"].asString("hotkey");
  s.os_version = v["host"]["os"].asString("");
  s.observed_from = readTime(v["observed_from"], warn, "observed_from");

  const json::Value& sym = v["symptom"];
  s.symptom = symptomFromString(sym["type"].asString(""));
  s.scope = scopeFromString(sym["scope"].asString(""));
  s.target_process = sym["target_process"].asString("");
  s.symptom_started_at = readTime(sym["started_at"], warn, "symptom.started_at");
  s.faulting_modules = sym["faulting_modules"].asStringList();
  if (s.symptom == Symptom::Unknown && warn && s.trigger_kind != "verify") {
    warn->push_back("증상 유형(symptom.type)이 비어 있어 사전 가중치를 적용할 수 없습니다.");
  }

  // 프로세스 목록: 문자열 배열 또는 {"name":...} 객체 배열 모두 허용
  const json::Array& procs = v["processes"].asArray();
  for (std::size_t i = 0; i < procs.size(); i++) {
    std::string name = procs[i].isString() ? procs[i].asString() : procs[i]["name"].asString("");
    if (!name.empty()) s.processes.push_back(name);
  }
  s.available.processes = v.has("processes");

  const json::Array& mods = v["modules"].asArray();
  for (std::size_t i = 0; i < mods.size(); i++) {
    const json::Value& m = mods[i];
    ModuleInfo mi;
    mi.process = m["process"].asString(s.target_process);
    mi.name = m["name"].asString("");
    mi.path = m["path"].asString("");
    mi.company = m["company"].asString("");
    mi.signer = m["signer"].asString("");
    mi.version = m["version"].asString("");
    if (m["microsoft_signed"].isNull()) {
      mi.microsoft_signed = looksMicrosoft(mi.signer, mi.company);
    } else {
      mi.microsoft_signed = m["microsoft_signed"].asBool(false);
    }
    mi.loaded_at = readTime(m["loaded_at"], warn, "modules[].loaded_at");
    if (!mi.name.empty()) s.modules.push_back(mi);
  }
  s.available.modules = v.has("modules");

  const json::Array& drvs = v["drivers"].asArray();
  for (std::size_t i = 0; i < drvs.size(); i++) {
    const json::Value& d = drvs[i];
    DriverInfo di;
    di.name = d["name"].asString("");
    di.display = d["display"].asString(di.name);
    di.type = d["type"].asString("");
    di.company = d["company"].asString("");
    di.version = d["version"].asString("");
    di.started_at = readTime(d["started_at"], warn, "drivers[].started_at");
    if (!di.name.empty()) s.drivers.push_back(di);
  }
  s.available.drivers = v.has("drivers");

  const json::Value& df = v["device_filters"];
  s.device_filters.keyboard_upper = df["keyboard_upper"].asStringList();
  s.device_filters.mouse_upper = df["mouse_upper"].asStringList();
  s.device_filters.ndis_filters = df["ndis_filters"].asStringList();
  s.available.device_filters = v.has("device_filters");

  const json::Value& sec = v["security"];
  s.realtime_av = sec["realtime_av"].asStringList();
  const json::Array& blocks = sec["firewall_blocks"].asArray();
  for (std::size_t i = 0; i < blocks.size(); i++) {
    FirewallBlock fb;
    fb.time = readTime(blocks[i]["time"], warn, "firewall_blocks[].time");
    fb.process = blocks[i]["process"].asString("");
    fb.direction = blocks[i]["direction"].asString("");
    fb.product = blocks[i]["product"].asString("");
    s.firewall_blocks.push_back(fb);
  }
  s.available.firewall = sec.has("firewall_blocks");

  const json::Array& dets = sec["detections"].asArray();
  for (std::size_t i = 0; i < dets.size(); i++) {
    SecurityDetection sd;
    sd.time = readTime(dets[i]["time"], warn, "detections[].time");
    sd.product = dets[i]["product"].asString("");
    sd.target = dets[i]["target"].asString("");
    sd.action = dets[i]["action"].asString("detect");
    sd.threat = dets[i]["threat"].asString("");
    s.detections.push_back(sd);
  }
  s.available.detections = sec.has("detections");

  const json::Array& chs = v["change_history"].asArray();
  for (std::size_t i = 0; i < chs.size(); i++) {
    ChangeItem ci;
    ci.time = readTime(chs[i]["time"], warn, "change_history[].time");
    ci.kind = chs[i]["kind"].asString("install");
    ci.subject = chs[i]["subject"].asString("");
    ci.version = chs[i]["version"].asString("");
    if (!ci.subject.empty()) s.changes.push_back(ci);
  }
  s.available.change_history = v.has("change_history");

  const json::Array& evs = v["events"].asArray();
  for (std::size_t i = 0; i < evs.size(); i++) {
    EventItem ei;
    ei.time = readTime(evs[i]["time"], warn, "events[].time");
    ei.provider = evs[i]["provider"].asString("");
    ei.id = evs[i]["id"].asInt(0);
    ei.subject = evs[i]["subject"].asString("");
    ei.faulting_module = evs[i]["faulting_module"].asString("");
    ei.component = evs[i]["component"].asString("");
    s.events.push_back(ei);
  }
  s.available.events = v.has("events");

  const json::Array& sens = v["sensors"]["samples"].asArray();
  for (std::size_t i = 0; i < sens.size(); i++) {
    const json::Value& x = sens[i];
    SensorSample ss;
    ss.time = readTime(x["time"], warn, "sensors.samples[].time");
    ss.cpu_temp = x["cpu_temp"].asNumber(-1);
    ss.gpu_temp = x["gpu_temp"].asNumber(-1);
    ss.cpu_clock = x["cpu_clock"].asNumber(-1);
    ss.gpu_clock = x["gpu_clock"].asNumber(-1);
    ss.throttling = x["throttling"].asBool(false);
    ss.gpu_power_limit_reason = x["gpu_power_limit_reason"].asString("");
    s.sensors.push_back(ss);
  }
  s.available.sensors = !s.sensors.empty();

  const json::Array& frs = v["frametime"]["samples"].asArray();
  for (std::size_t i = 0; i < frs.size(); i++) {
    FrameSample fs;
    fs.time = readTime(frs[i]["time"], warn, "frametime.samples[].time");
    fs.frame_ms = frs[i]["ms"].asNumber(0);
    s.frames.push_back(fs);
  }
  s.available.frametime = !s.frames.empty();

  const json::Array& dumps = v["minidumps"].asArray();
  for (std::size_t i = 0; i < dumps.size(); i++) {
    MiniDumpInfo md;
    md.time = readTime(dumps[i]["time"], warn, "minidumps[].time");
    md.kind = dumps[i]["kind"].asString("app");
    md.process = dumps[i]["process"].asString("");
    md.bugcheck = dumps[i]["bugcheck"].asString("");
    md.faulting_module = dumps[i]["faulting_module"].asString("");
    md.third_party_in_stack = dumps[i]["stack_third_party"].asStringList();
    s.minidumps.push_back(md);
  }
  s.available.minidump = !s.minidumps.empty();

  const json::Array& disks = v["storage"]["disks"].asArray();
  for (std::size_t i = 0; i < disks.size(); i++) {
    DiskHealth dh;
    dh.name = disks[i]["name"].asString("");
    dh.health = disks[i]["health"].asString("");
    dh.reallocated_sectors = disks[i]["reallocated_sectors"].asInt(0);
    dh.pending_sectors = disks[i]["pending_sectors"].asInt(0);
    dh.uncorrectable = disks[i]["uncorrectable"].asInt(0);
    dh.media_errors = disks[i]["media_errors"].asInt(0);
    s.disks.push_back(dh);
  }
  s.available.storage = v["storage"].has("disks");

  // 수집기 v0.6 확장: 최근 60분 자원 추세 (없으면 available=false, 경고 없음 — 선택 필드)
  const json::Value& rt = v["resource_trends"];
  if (rt.isObject()) {
    ResourceTrends& t = s.trends;
    t.available = true;
    t.window_minutes = rt["window_minutes"].asInt(0);
    t.samples = rt["samples"].asInt(0);
    t.commit_slope_pct_per_hour = rt["commit_slope_pct_per_hour"].asNumber(0);
    t.commit_pct_now = rt["commit_pct_now"].asNumber(-1);
    t.avail_mb_slope_per_min = rt["avail_mb_slope_per_min"].asNumber(0);
    t.gpu_dedicated_mb_slope_per_min = rt["gpu_dedicated_mb_slope_per_min"].asNumber(0);
    t.gpu_temp_max = rt["gpu_temp_max"].asNumber(-1);
    t.gpu_clock_min = rt["gpu_clock_min"].asNumber(-1);
    t.gpu_clock_max = rt["gpu_clock_max"].asNumber(-1);
    if (rt["focus_process"].isObject()) {
      t.has_focus = true;
      t.focus = parseProcTrend(rt["focus_process"]);
    }
    const json::Array& gr = rt["top_memory_growers"].asArray();
    for (std::size_t i = 0; i < gr.size(); i++) t.memory_growers.push_back(parseProcTrend(gr[i]));
    const json::Array& tc = rt["top_cpu_60"].asArray();
    for (std::size_t i = 0; i < tc.size(); i++) t.top_cpu.push_back(parseProcTrend(tc[i]));
    s.available.trends = true;
  }

  const json::Value& bd = v["baseline_diff"];
  if (!bd.isNull()) {
    s.baseline.available = true;
    s.baseline.added = bd["added"].asStringList();
    s.baseline.removed = bd["removed"].asStringList();
    s.baseline.changed = bd["changed"].asStringList();
  }
  s.available.baseline = s.baseline.available;

  const json::Value& gt = v["ground_truth"];
  if (!gt.isNull()) {
    s.has_ground_truth = true;
    s.truth_layer = layerFromString(gt["layer"].asString(""));
    s.truth_category = gt["category"].asString("");
    s.truth_subject = gt["subject"].asString("");
  }
  return s;
}

bool loadSnapshotFile(const std::string& path, Snapshot* out, std::vector<std::string>* warnings,
                      std::string* error) {
  std::string err;
  json::Value v = json::Value::parseFile(path, &err);
  if (!err.empty() || !v.isObject()) {
    if (error) *error = err.empty() ? "스냅샷이 JSON 객체가 아닙니다: " + path : err;
    return false;
  }
  *out = parseSnapshot(v, warnings);
  return true;
}

}  // namespace ce
