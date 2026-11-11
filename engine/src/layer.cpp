#include "ce/layer.hpp"

#include <algorithm>

#include "ce/signals.hpp"

namespace ce {

namespace {

// 층별 점수와 근거를 모으는 그릇
struct LayerAccumulator {
  double sw = 0;
  double drv = 0;
  double hw = 0;
  std::vector<Evidence> ev;

  void add(Layer l, double w, const std::string& kind, const std::string& detail, long long t) {
    if (w <= 0) return;
    if (l == Layer::Software) {
      sw += w;
    } else if (l == Layer::Driver) {
      drv += w;
    } else if (l == Layer::Hardware) {
      hw += w;
    } else {
      return;
    }
    ev.push_back(Evidence(kind, "[" + toString(l) + "] " + detail, w, t));
  }
};

// 변경 이력 1건이 드라이버성 변경인지 판단
bool isDriverChange(const ChangeItem& c, const Rules& r) {
  if (c.kind == "driver_update" || c.kind == "os_update") return true;
  std::string cat;
  std::string label;
  r.classifyName(c.subject, c.subject, "other_sw", &cat, &label);
  return layerOfCategory(cat) == Layer::Driver;
}

}  // namespace

double availabilityFactor(const Snapshot& s) {
  const int have = s.available.countCore();
  return 0.55 + 0.45 * (static_cast<double>(have) / 7.0);
}

double LayerResult::probabilityOf(Layer l) const {
  if (l == Layer::Software) return p_sw;
  if (l == Layer::Driver) return p_driver;
  if (l == Layer::Hardware) return p_hw;
  return 0;
}

json::Value LayerResult::toJson(int tzOffsetMinutes) const {
  json::Value o = json::Value::makeObject();
  o.set("top", json::Value(toString(top)));
  o.set("confidence", json::Value(confidence));
  json::Value p = json::Value::makeObject();
  p.set("SW", json::Value(p_sw));
  p.set("DRIVER", json::Value(p_driver));
  p.set("HW", json::Value(p_hw));
  o.set("probability", p);
  o.set("evidence", evidenceListToJson(evidence, tzOffsetMinutes));
  return o;
}

// ─────────────────────────────────────────────────────────────────────────────
// classifyLayer() : [1단계] 어느 층의 문제인가
//
// 방법: 세 층(SW/DRIVER/HW)에 '점수 통'을 하나씩 두고, 스냅샷에서 신호를 하나 볼 때마다
//       해당 층 통에 가중치를 더한다. 마지막에 세 통의 합으로 나누면 확률이 된다.
//
//   예) s1 안티치트 시나리오
//       기본값            SW 0.10  DRIVER 0.08  HW 0.08
//       특정 프로그램만    SW +0.35, DRIVER +0.10
//       서드파티 DLL 로드  SW +0.12
//       ------------------------------------------------
//       합계              SW 0.57  DRIVER 0.18  HW 0.08  → SW 69% · DRIVER 22% · HW 10%
//
// 가중치 숫자는 rules.json 의 layer_weights 에서 이름으로 꺼내 쓴다(layerWeight("이름", 기본값)).
// 신뢰도는 "1등과 2등의 차이가 클수록, 수집된 항목이 많을수록" 높아진다.
// ─────────────────────────────────────────────────────────────────────────────
LayerResult classifyLayer(const Snapshot& s, const Rules& r) {
  LayerAccumulator a;
  // 어느 층도 0이 되지 않도록 기본값을 깐다(근거 없음 = 배제 아님).
  a.sw = r.layerWeight("base_sw", 0.1);
  a.drv = r.layerWeight("base_driver", 0.08);
  a.hw = r.layerWeight("base_hw", 0.08);

  const long long ref = signals::referenceTime(s);

  // (1) 증상 범위
  if (s.scope == SymptomScope::SingleApp) {
    a.add(Layer::Software, r.layerWeight("scope_single_app_sw", 0), "coexist",
          "증상이 특정 프로그램 하나에서만 발생합니다.", 0);
    a.add(Layer::Driver, r.layerWeight("scope_single_app_driver", 0), "coexist",
          "특정 프로그램 전용 증상도 드라이버 경로를 탈 수 있습니다.", 0);
  } else if (s.scope == SymptomScope::MultipleApps) {
    a.add(Layer::Hardware, r.layerWeight("scope_multiple_apps_hw", 0), "coexist",
          "서로 무관한 여러 프로그램에서 증상이 나타납니다.", 0);
    a.add(Layer::Driver, r.layerWeight("scope_multiple_apps_driver", 0), "coexist",
          "여러 프로그램 공통 증상은 공유 드라이버 문제일 수 있습니다.", 0);
  } else if (s.scope == SymptomScope::SystemWide) {
    a.add(Layer::Hardware, r.layerWeight("scope_system_wide_hw", 0), "coexist",
          "시스템 전체가 영향을 받습니다.", 0);
    a.add(Layer::Driver, r.layerWeight("scope_system_wide_driver", 0), "coexist",
          "시스템 전체 증상은 커널·드라이버 계층일 수 있습니다.", 0);
  }

  // (2) 오류 모듈: 매번 다르면 HW, 특정 서드파티면 SW
  const std::vector<std::string> faulting = signals::distinctFaultingModules(s);
  if (faulting.size() >= 2) {
    a.add(Layer::Hardware, r.layerWeight("faulting_module_varied_hw", 0), "event",
          "크래시마다 오류 모듈이 다릅니다 (" + std::to_string(faulting.size()) + "종).", 0);
  } else if (faulting.size() == 1 && !signals::isMicrosoftModule(s, faulting[0]) &&
             !signals::isDriverModuleName(faulting[0])) {
    a.add(Layer::Software, r.layerWeight("faulting_module_thirdparty_sw", 0), "event",
          "오류 모듈이 서드파티 모듈(" + faulting[0] + ")로 일정합니다.", 0);
  }

  // (2-1) 블루스크린 커널 덤프: 하드웨어 버그체크면 HW, 특정 드라이버가 원인이면 DRIVER
  bool sawHwBugcheck = false;
  bool sawDriverBugcheck = false;
  for (std::size_t i = 0; i < s.minidumps.size(); i++) {
    const MiniDumpInfo& d = s.minidumps[i];
    if (!signals::isKernelDump(d)) continue;
    if (!sawHwBugcheck && signals::isHardwareBugcheck(d.bugcheck)) {
      sawHwBugcheck = true;
      a.add(Layer::Hardware, r.layerWeight("hw_bugcheck_hw", 0), "event",
            "하드웨어 오류로 인한 블루스크린(" + d.bugcheck + ")이 기록되었습니다.", d.time);
    } else if (!sawDriverBugcheck && signals::isDriverModuleName(d.faulting_module) &&
               !signals::isMicrosoftModule(s, d.faulting_module)) {
      sawDriverBugcheck = true;
      a.add(Layer::Driver, r.layerWeight("kernel_crash_driver_driver", 0), "event",
            "블루스크린(" + d.bugcheck + ")의 원인 드라이버가 " + d.faulting_module + "(으)로 기록되었습니다.",
            d.time);
    }
  }

  // (2-2) 저장장치 SMART 경고
  const int warnDisks = signals::disksWithWarning(s);
  if (warnDisks > 0) {
    a.add(Layer::Hardware, r.layerWeight("smart_warning_hw", 0), "sensor",
          "SMART 경고가 있는 저장장치가 " + std::to_string(warnDisks) + "개 있습니다.", 0);
  }

  // (2-3) 네트워크 링크 끊김이 반복되면 랜카드 드라이버 쪽 근거
  int linkDrops = 0;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    if (signals::isNetworkLinkEvent(s.events[i])) linkDrops++;
  }
  if (linkDrops >= 2) {
    a.add(Layer::Driver, r.layerWeight("network_link_drop_driver", 0), "event",
          "네트워크 어댑터 링크 끊김이 " + std::to_string(linkDrops) + "번 기록되었습니다.", 0);
  }

  // (3) 대상 프로세스에 주입된 서드파티 모듈
  const std::vector<int> mods = s.moduleIndexesOf(s.target_process);
  int injected = 0;
  for (std::size_t i = 0; i < mods.size(); i++) {
    if (!s.modules[mods[i]].microsoft_signed) injected++;
  }
  if (injected > 0) {
    a.add(Layer::Software, r.layerWeight("third_party_module_loaded_sw", 0), "coexist",
          "대상 프로세스에 서드파티 모듈 " + std::to_string(injected) + "개가 로드되어 있습니다.",
          0);
  }

  // (4) 입력 필터 드라이버 (입력 누락 증상의 우선 후보)
  const int inputFilters = signals::thirdPartyInputFilterCount(s);
  if (inputFilters > 0 && s.symptom == Symptom::InputLoss) {
    a.add(Layer::Software, r.layerWeight("input_filter_present_sw", 0), "coexist",
          "키보드·마우스 클래스에 서드파티 필터 드라이버 " + std::to_string(inputFilters) +
              "개가 등록되어 있습니다.",
          0);
  }

  // (5) 보안 제품 흔적: 방화벽 차단 / 탐지·격리 / 실시간 백신 중복
  for (std::size_t i = 0; i < s.firewall_blocks.size(); i++) {
    const FirewallBlock& f = s.firewall_blocks[i];
    if (s.target_process.empty() || equalsNoCase(f.process, s.target_process)) {
      a.add(Layer::Software, r.layerWeight("firewall_block_sw", 0), "event",
            "보안 제품이 대상 프로그램의 통신을 차단한 기록이 있습니다.", f.time);
      break;
    }
  }
  const std::vector<int> dets = signals::detectionsOnTarget(s);
  if (!dets.empty()) {
    const SecurityDetection& d = s.detections[dets[0]];
    a.add(Layer::Software, r.layerWeight("security_detection_sw", 0), "detection",
          d.product + "이(가) 대상 프로그램을 탐지·" + d.action + " 처리한 기록이 있습니다.",
          d.time);
  }
  if (s.realtime_av.size() >= 2) {
    a.add(Layer::Software, r.layerWeight("duplicate_av_sw", 0), "coexist",
          "실시간 감시 백신이 " + std::to_string(s.realtime_av.size()) + "개 동시에 동작 중입니다.",
          0);
  }

  // (6) 증상 직전의 변경 이력 (시간 상관)
  for (std::size_t i = 0; i < s.changes.size(); i++) {
    const ChangeItem& c = s.changes[i];
    double corr = signals::timeCorrelation(c.time, ref, r);
    if (r.isAutoUpdateNoise(c.subject)) corr *= 0.25;  // 브라우저·런처 자동 업데이트는 약한 단서
    if (corr < 0.3) continue;
    const bool driverish = isDriverChange(c, r);
    double w = 0;
    if (driverish) {
      w = r.layerWeight("recent_driver_change_driver", 0) * corr;
    } else {
      w = r.layerWeight("recent_app_change_sw", 0) * corr;
    }
    a.add(driverish ? Layer::Driver : Layer::Software, w, "time_corr",
          "증상 시작 직전 변경: " + c.subject + " (" + c.kind + ")", c.time);
  }

  // (7) 이벤트 로그 흔적 (종류별로 한 번씩만 반영)
  bool sawDisplay = false;
  bool sawPower = false;
  bool sawWhea = false;
  bool sawDisk = false;
  bool sawDisconnect = false;
  for (std::size_t i = 0; i < s.events.size(); i++) {
    const EventItem& e = s.events[i];
    if (!sawDisplay && signals::isDisplayEvent(e)) {
      sawDisplay = true;
      a.add(Layer::Driver, r.layerWeight("display_event_driver", 0), "event",
            "디스플레이 드라이버 응답 중지·복구 이벤트(" + std::to_string(e.id) + ")가 있습니다.",
            e.time);
    }
    if (!sawPower && signals::isPowerLossEvent(s, e)) {  // 블루스크린 뒤 재부팅은 제외
      sawPower = true;
      a.add(Layer::Hardware, r.layerWeight("kernel_power_41_hw", 0), "event",
            "예고 없는 종료(Kernel-Power 41) 기록이 있습니다.", e.time);
    }
    if (!sawWhea && signals::isWheaEvent(e)) {
      sawWhea = true;
      a.add(Layer::Hardware, r.layerWeight("whea_error_hw", 0), "event",
            "WHEA 하드웨어 오류가 기록되었습니다.", e.time);
    }
    if (!sawDisk && signals::isDiskEvent(e)) {
      sawDisk = true;
      a.add(Layer::Hardware, r.layerWeight("disk_error_hw", 0), "event",
            "저장장치 I/O 오류가 기록되었습니다.", e.time);
    }
    if (!sawDisconnect && signals::isDisconnectEvent(e)) {
      sawDisconnect = true;
      a.add(Layer::Hardware, r.layerWeight("device_disconnect_hw", 0), "event",
            "장치 연결 해제·재연결 기록이 있습니다.", e.time);
    }
  }

  // (8) 센서
  if (s.available.sensors) {
    if (signals::throttlingSeen(s)) {
      a.add(Layer::Hardware, r.layerWeight("sensor_throttling_hw", 0), "sensor",
            "스로틀링(온도 제한) 구간이 관측되었습니다.", 0);
    }
    const double cpu = signals::maxCpuTemp(s);
    const double gpu = signals::maxGpuTemp(s);
    if (cpu >= 95 || gpu >= 87) {
      a.add(Layer::Hardware, r.layerWeight("sensor_high_temp_hw", 0), "sensor",
            "최고 온도가 임계에 도달했습니다 (CPU " + std::to_string(static_cast<int>(cpu)) +
                "℃ / GPU " + std::to_string(static_cast<int>(gpu)) + "℃).",
            0);
    }
    long long when = 0;
    if (s.symptom == Symptom::FpsDrop && signals::clockDropMatchesFrameDrop(s, &when)) {
      a.add(Layer::Hardware, r.layerWeight("fps_drop_clock_correlated_hw", 0), "sensor",
            "프레임 저하 시각과 클럭 하락 시각이 일치합니다.", when);
    }
  }

  LayerResult out;
  out.raw_sw = a.sw;
  out.raw_driver = a.drv;
  out.raw_hw = a.hw;
  out.evidence = a.ev;

  const double total = a.sw + a.drv + a.hw;
  if (total > 0) {
    out.p_sw = a.sw / total;
    out.p_driver = a.drv / total;
    out.p_hw = a.hw / total;
  }

  double best = out.p_sw;
  out.top = Layer::Software;
  if (out.p_driver > best) {
    best = out.p_driver;
    out.top = Layer::Driver;
  }
  if (out.p_hw > best) {
    best = out.p_hw;
    out.top = Layer::Hardware;
  }

  double ps[3] = {out.p_sw, out.p_driver, out.p_hw};
  std::sort(ps, ps + 3);
  const double margin = ps[2] - ps[1];  // 1위 - 2위

  // 수집 가용성: 근거를 못 본 항목이 많을수록 신뢰도를 깎는다(제안서 10장 대응).
  out.confidence = std::min(0.97, (0.35 + 1.3 * margin) * availabilityFactor(s));
  return out;
}

}  // namespace ce
