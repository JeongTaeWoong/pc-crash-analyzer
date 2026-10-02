// rules_default.cpp - 기본 규칙(가중치·점검·조치·해결책·사례 시드)의 내장본.
// 직접 고치지 말고 data/rules.json 을 고친 뒤 tools/gen_rules_default.py 로 다시 만든다.
// 값의 근거는 docs/rules_rationale.md 에 정리한다.

namespace ce {
namespace internal {

extern const char* const kDefaultRulesParts[];
const char* const kDefaultRulesParts[] = {
    R"JSON({
  "version": 2,

  "focus": {
    "name": "보안 프로그램 충돌",
    "categories": ["anticheat", "antivirus", "fin_security", "security_firewall"]
  },

  "session": {
    "skip_layer_threshold": 0.15,
    "confirm_threshold": 0.85,
    "verify_min_observe_minutes": 60
  },

  "layer_weights": {
    "scope_single_app_sw": 0.35,
    "scope_single_app_driver": 0.10,
    "scope_multiple_apps_hw": 0.30,
    "scope_multiple_apps_driver": 0.15,
    "scope_system_wide_hw": 0.25,
    "scope_system_wide_driver": 0.20,
    "faulting_module_varied_hw": 0.30,
    "faulting_module_thirdparty_sw": 0.30,
    "third_party_module_loaded_sw": 0.12,
    "input_filter_present_sw": 0.20,
    "firewall_block_sw": 0.25,
    "security_detection_sw": 0.35,
    "duplicate_av_sw": 0.12,
    "recent_app_change_sw": 0.15,
    "recent_driver_change_driver": 0.30,
    "display_event_driver": 0.30,
    "device_disconnect_hw": 0.20,
    "kernel_power_41_hw": 0.30,
    "whea_error_hw": 0.40,
    "disk_error_hw": 0.25,
    "sensor_throttling_hw": 0.30,
    "sensor_high_temp_hw": 0.20,
    "fps_drop_clock_correlated_hw": 0.35,
    "base_sw": 0.10,
    "base_driver": 0.08,
    "base_hw": 0.08
  },

  "correlation": {
    "w_prior": 1.0,
    "w_time": 0.9,
    "w_coexist": 1.0,
    "w_case": 0.6,
    "w_layer": 1.1,
    "time_window_minutes": 4320,
    "time_full_credit_minutes": 120
  },

  "priors": {
    "app_crash":         {"anticheat": 0.22, "overlay": 0.22, "antivirus": 0.15, "fin_security": 0.08, "security_firewall": 0.03, "other_sw": 0.05, "gpu_driver": 0.12, "os_update": 0.05, "thermal": 0.06, "power": 0.05, "memory": 0.05},
    "freeze":            {"antivirus": 0.18, "anticheat": 0.12, "fin_security": 0.10, "overlay": 0.08, "other_sw": 0.08, "gpu_driver": 0.15, "thermal": 0.12, "power": 0.10, "storage": 0.13, "memory": 0.08},
    "fps_drop":          {"thermal": 0.30, "power": 0.15, "overlay": 0.15, "antivirus": 0.12, "gpu_driver": 0.13, "storage": 0.08, "other_sw": 0.05},
    "flicker":           {"gpu_driver": 0.30, "display_util": 0.30, "connection": 0.20, "peripheral_driver": 0.08, "power": 0.10, "overlay": 0.05},
    "input_loss":        {"input_util": 0.30, "antivirus": 0.18, "fin_security": 0.15, "connection": 0.15, "overlay": 0.08, "peripheral_driver": 0.10},
    "launch_fail":       {"antivirus": 0.30, "anticheat": 0.20, "fin_security": 0.18, "security_firewall": 0.12, "other_sw": 0.12, "os_update": 0.05},
    "network_fail":      {"security_firewall": 0.33, "antivirus": 0.24, "fin_security": 0.20, "network_driver": 0.12, "connection": 0.08, "other_sw": 0.03},
    "unexpected_reboot": {"power": 0.35, "thermal": 0.25, "memory": 0.15, "storage": 0.10, "gpu_driver": 0.08, "other_driver": 0.04, "antivirus": 0.03}
  },

  "hints": [
    {"match": "vgk",                "category": "anticheat",         "label": "Riot Vanguard (커널 안티치트)"},
    {"match": "vgc",                "category": "anticheat",         "label": "Riot Vanguard (커널 안티치트)"},
    {"match": "vanguard",           "category": "anticheat",         "label": "Riot Vanguard (커널 안티치트)"},
    {"match": "easyanticheat",      "category": "anticheat",         "label": "EasyAntiCheat"},
    {"match": "eac",                "category": "anticheat",         "label": "EasyAntiCheat"},
    {"match": "beservice",          "category": "anticheat",         "label": "BattlEye"},
    {"match": "battleye",           "category": "anticheat",         "label": "BattlEye"},
    {"match": "xigncode",           "category": "anticheat",         "label": "XIGNCODE3"},
    {"match": "nprotect gameguard", "category": "anticheat",         "label": "nProtect GameGuard"},
    {"match": "gameguard",          "category": "anticheat",         "label": "nProtect GameGuard"},
    {"match": "ahnlab v3",          "category": "antivirus",         "label": "AhnLab V3"},
    {"match": "ahnlab",             "category": "antivirus",         "label": "AhnLab V3"},
    {"match": "v3l4",               "category": "antivirus",         "label": "AhnLab V3"},
    {"match": "alyac",              "category": "antivirus",         "label": "알약"},
    {"match": "알약",               "category": "antivirus",         "label": "알약"},
    {"match": "defender",           "category": "antivirus",         "label": "Microsoft Defender"},
    {"match": "msmpeng",            "category": "antivirus",         "label": "Microsoft Defender"},
    {"match": "avast",              "category": "antivirus",         "label": "Avast"},
    {"match": "kaspersky",          "category": "antivirus",         "label": "Kaspersky"},
    {"match": "norton",             "category": "antivirus",         "label": "Norton"},
    {"match": "mcafee",             "category": "antivirus",         "label": "McAfee"},
    {"match": "astx",               "category": "fin_security",      "label": "AhnLab Safe Transaction"},
    {"match": "safe transaction",   "category": "fin_security",      "label": "AhnLab Safe Transaction"},
    {"match": "veraport",           "category": "fin_security",      "label": "VeraPort"},
    {"match": "wizvera",            "category": "fin_security",      "label": "WIZVERA"},
    {"match": "delfino",            "category": "fin_security",      "label": "Delfino"},
    {"match": "initech",            "category": "fin_security",      "label": "INITECH"},
    {"match": "touchen",            "category": "fin_security",      "label": "TouchEn nxKey"},
    {"match": "nxkey",              "category": "fin_security",      "label": "TouchEn nxKey"},
    {"match": "interezen",          "category": "fin_security",      "label": "Interezen IPInside"},
    {"match": "ipinside",           "category": "fin_security",      "label": "Interezen IPInside"},
    {"match": "nprotect",           "category": "fin_security",      "label": "nProtect Online Security"},
    {"match": "firewall",           "category": "security_firewall", "label": "보안 방화벽 모듈"},
    {"match": "smartdefense",       "category": "security_firewall", "label": "보안 방화벽 모듈"},
    {"match": "netfilter",          "category": "security_firewall", "label": "네트워크 필터 드라이버"},
    {"match": "discord",            "category": "overlay",           "label": "Discord 오버레이"},
    {"match": "nvcontainer",        "category": "overlay",           "label": "NVIDIA 오버레이"},
    {"match": "geforce",            "category": "overlay",           "label": "NVIDIA 오버레이"},
    {"match": "overlay",            "category": "overlay",           "label": "게임 오버레이"},
    {"match": "rtss",               "category": "overlay",           "label": "RivaTuner (RTSS)"},
    {"match": "afterburner",        "category": "overlay",           "label": "MSI Afterburner"},
    {"match": "gameoverlay",        "category": "overlay",           "label": "Steam 오버레이"},
    {"match": "obs",                "category": "overlay",           "label": "OBS 캡처 훅"},
    {"match": "logi",               "category": "input_util",        "label": "Logitech 입력 유틸"},
    {"match": "razer",              "category": "input_util",        "label": "Razer Synapse"},
    {"match": "icue",               "category": "input_util",        "label": "Corsair iCUE"},
    {"match": "kbfilt",             "category": "input_util",        "label": "키보드 필터 드라이버"},
    {"match": "synaptics",          "category": "input_util",        "label": "Synaptics 입력 드라이버"},
    {"match": "nvlddmkm",           "category": "gpu_driver",        "label": "NVIDIA 디스플레이 드라이버"},
    {"match": "amdkmdag",           "category": "gpu_driver",        "label": "AMD 디스플레이 드라이버"},
    {"match": "igdkmd",             "category": "gpu_driver",        "label": "Intel 그래픽 드라이버"},
    {"match": "nvidia display",     "category": "gpu_driver",        "label": "NVIDIA 디스플레이 드라이버"},
    {"match": "displayfusion",      "category": "display_util",      "label": "DisplayFusion"},
    {"match": "monitor",            "category": "display_util",      "label": "모니터 유틸리티"},
    {"match": "onscreen",           "category": "display_util",      "label": "모니터 제조사 유틸"},
    {"match": "easy setting",       "category": "display_util",      "label": "모니터 제조사 유틸"},
    {"match": "ndis",               "category": "network_driver",    "label": "네트워크 필터 드라이버"},
    {"match": "realtek",            "category": "network_driver",    "label": "Realtek 네트워크 드라이버"}
  ],

  "actions": [
    {"id": "a_overlay_off", "label": "게임 내 오버레이 끄기", "kind": "toggle_setting",
     "steps": ["해당 프로그램 설정에서 '게임 내 오버레이'를 끕니다.", "대상 프로그램을 다시 실행해 증상이 다시 나는지 확인합니다."],
     "rollback": "같은 설정에서 오버레이를 다시 켭니다.", "reversible": true, "requires_reboot": false, "risk": "low",
     "automation": "toggle_overlay:{product}:off", "auto_default": true},

    {"id": "a_stop_process", "label": "의심 프로그램 종료", "kind": "stop_process",
     "steps": ["트레이 아이콘 또는 작업 관리자에서 해당 프로그램을 완전히 종료합니다.", "대상 프로그램을 다시 실행해 증상이 다시 나는지 확인합니다."],
     "rollback": "프로그램을 다시 실행합니다.", "reversible": true, "requires_reboot": false, "risk": "low",
     "automation": "stop_process:{subject}", "auto_default": true},

    {"id": "a_av_pause", "label": "백신 실시간 감시 10분 일시 중지", "kind": "toggle_setting",
     "steps": ["백신 설정에서 실시간 감시를 10분만 일시 중지합니다. 삭제하지 마세요.", "그 사이 대상 프로그램을 실행해 증상이 다시 나는지 확인합니다."],
     "rollback": "10분 뒤 자동으로 다시 켜지며, 직접 다시 켜도 됩니다.", "reversible": true, "requires_reboot": false, "risk": "medium",
     "automation": "av_realtime:{product}:pause:600", "auto_default": false},

    {"id": "a_anticheat_stop", "label": "안티치트 서비스 중지 후 재부팅", "kind": "stop_service",
     "steps": ["안티치트 서비스의 시작 유형을 '사용 안 함'으로 바꾸고 재부팅합니다.", "안티치트를 쓰는 게임은 실행하지 않은 상태로 대상 프로그램을 실행해 봅니다."],
     "rollback": "서비스 시작 유형을 원래대로 되돌리고 재부팅합니다.", "reversible": true, "requires_reboot": true, "risk": "low",
     "automation": "service_start_type:{subject}:disabled", "auto_default": false},

    {"id": "a_finsec_stop", "label": "금융 보안모듈 상주 중지", "kind": "stop_service",
     "steps": ["서비스 관리자에서 해당 보안모듈 서비스를 중지합니다.", "대상 프로그램에서 증상이 다시 나는지 확인합니다."],
     "rollback": "인터넷뱅킹을 쓰기 전에 서비스를 다시 시작합니다(은행 사이트 접속 시 자동 시작되기도 합니다).", "reversible": true, "requires_reboot": false, "risk": "medium",
     "automation": "stop_service:{subject}", "auto_default": false},

    {"id": "a_firewall_allow", "label": "보안 제품 방화벽에 허용 규칙 추가", "kind": "firewall_allow",
     "steps": ["보안 제품의 방화벽 설정에서 대상 프로그램을 허용 목록에 추가합니다.", "대상 프로그램의 접속이 되는지 확인합니다."],
     "rollback": "추가한 허용 규칙을 삭제합니다.", "reversible": true, "requires_reboot": false, "risk": "low",
     "automation": "firewall_allow:{target_process}", "auto_default": true},

    {"id": "a_clean_boot", "label": "클린 부트", "kind": "toggle_setting",
)JSON",
    R"JSON(     "steps": ["msconfig에서 'Microsoft 서비스 모두 숨기기'를 체크한 뒤 나머지 서비스를 끕니다.", "작업 관리자 시작 프로그램을 모두 끄고 재부팅합니다.", "대상 프로그램에서 증상이 다시 나는지 확인합니다."],
     "rollback": "msconfig에서 '모든 서비스 사용', 시작 프로그램을 다시 켜고 재부팅합니다.", "reversible": true, "requires_reboot": true, "risk": "medium",
     "automation": "clean_boot:on", "auto_default": false},

    {"id": "a_av_exclude", "label": "백신 예외(신뢰 목록)에 대상 프로그램 폴더 등록", "kind": "exclude_path",
     "steps": ["백신 설정의 '검사 예외' 또는 '신뢰 프로그램'에 대상 프로그램 폴더를 추가합니다.", "격리된 파일이 있으면 격리함에서 복원합니다.", "대상 프로그램을 다시 실행합니다."],
     "rollback": "예외 목록에서 해당 폴더를 삭제합니다.", "reversible": true, "requires_reboot": false, "risk": "medium",
     "automation": "av_exclude:{product}:{target_process}", "auto_default": true},

    {"id": "a_av_report_fp", "label": "백신 제조사에 오탐 신고", "kind": "inspect",
     "steps": ["백신 제조사 오탐 신고 페이지에 탐지명과 파일을 제출합니다.", "엔진 업데이트 후 예외를 지워도 정상 동작하는지 확인합니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low",
     "automation": "", "auto_default": false},

    {"id": "a_av_single", "label": "실시간 감시 백신을 하나만 남기기", "kind": "toggle_setting",
     "steps": ["두 개 이상 켜져 있는 실시간 감시 중 하나만 남기고 나머지의 실시간 감시를 끕니다.", "대상 프로그램을 다시 실행합니다."],
     "rollback": "끈 백신의 실시간 감시를 다시 켭니다.", "reversible": true, "requires_reboot": false, "risk": "medium",
     "automation": "av_realtime:{product}:off", "auto_default": false},

    {"id": "a_anticheat_repair", "label": "게임 런처에서 안티치트·게임 복구(무결성 검사)", "kind": "update_program",
     "steps": ["게임 런처에서 '복구' 또는 '파일 무결성 검사'를 실행합니다.", "안티치트를 최신 버전으로 업데이트한 뒤 재부팅합니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "low",
     "automation": "launcher_repair:{subject}", "auto_default": false},

    {"id": "a_anticheat_autostart_off", "label": "안티치트를 해당 게임을 할 때만 켜지도록 설정", "kind": "toggle_setting",
     "steps": ["안티치트 서비스 시작 유형을 '수동'으로 바꿉니다(트레이 메뉴에 '종료' 기능이 있으면 그것을 씁니다).", "안티치트를 쓰는 게임을 할 때만 실행되도록 합니다.", "재부팅 후 대상 프로그램을 실행합니다."],
     "rollback": "서비스 시작 유형을 '자동'으로 되돌립니다.", "reversible": true, "requires_reboot": true, "risk": "low",
     "automation": "service_start_type:{subject}:manual", "auto_default": false},

    {"id": "a_finsec_autostart_off", "label": "금융 보안모듈 자동 실행 끄기", "kind": "toggle_setting",
     "steps": ["서비스 관리자에서 해당 보안모듈의 시작 유형을 '수동'으로 바꿉니다.", "인터넷뱅킹을 쓸 때만 실행되도록 합니다."],
     "rollback": "시작 유형을 '자동'으로 되돌립니다.", "reversible": true, "requires_reboot": false, "risk": "low",
     "automation": "service_start_type:{subject}:manual", "auto_default": true},

    {"id": "a_finsec_uninstall", "label": "사용하지 않는 보안모듈 제거", "kind": "uninstall",
     "steps": ["설정 → 앱에서 최근에 쓰지 않은 보안모듈을 제거합니다.", "필요하면 해당 은행·관공서 사이트에서 다시 설치할 수 있습니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "medium",
     "automation": "uninstall:{subject}", "auto_default": false},

    {"id": "a_program_update", "label": "해당 프로그램 최신 버전으로 업데이트", "kind": "update_program",
     "steps": ["제조사 사이트나 프로그램 내 업데이트 기능으로 최신 버전을 설치합니다.", "대상 프로그램을 다시 실행합니다."],
     "rollback": "", "reversible": false, "requires_reboot": false, "risk": "low",
     "automation": "update:{subject}", "auto_default": false},

    {"id": "a_reinstall", "label": "해당 프로그램 재설치", "kind": "reinstall",
     "steps": ["설정 → 앱에서 해당 프로그램을 제거하고 재부팅합니다.", "제조사 사이트에서 최신 설치 파일을 받아 다시 설치합니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "medium",
     "automation": "reinstall:{subject}", "auto_default": false},

    {"id": "a_uninstall", "label": "해당 프로그램 제거", "kind": "uninstall",
     "steps": ["설정 → 앱에서 해당 프로그램을 제거하고 재부팅합니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "medium",
     "automation": "uninstall:{subject}", "auto_default": false},

    {"id": "a_gpu_rollback", "label": "GPU 드라이버 롤백/재설치", "kind": "driver_rollback",
     "steps": ["DDU로 현재 드라이버를 지운 뒤 이전 정식 버전을 설치합니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "medium",
     "automation": "", "auto_default": false},

    {"id": "a_driver_rollback", "label": "장치 드라이버 이전 버전으로 되돌리기", "kind": "driver_rollback",
     "steps": ["장치 관리자 → 해당 장치 → 드라이버 → '드라이버 롤백'을 누릅니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "medium",
     "automation": "", "auto_default": false},

    {"id": "a_os_update_remove", "label": "최근 Windows 업데이트 제거", "kind": "os_update_remove",
     "steps": ["설정 → Windows 업데이트 → 업데이트 기록 → 업데이트 제거에서 증상 직전 업데이트를 지웁니다."],
     "rollback": "", "reversible": false, "requires_reboot": true, "risk": "high",
     "automation": "", "auto_default": false},

    {"id": "a_cooling", "label": "냉각 점검(청소·팬 커브·써멀)", "kind": "physical",
     "steps": ["먼지 청소와 팬 커브 조정을 합니다.", "온도가 임계에 닿지 않는 상태에서 재현해 봅니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low", "automation": "", "auto_default": false},

    {"id": "a_power", "label": "전원 점검(케이블·파워·전력 제한)", "kind": "physical",
     "steps": ["보조전원 케이블을 다시 연결합니다.", "가능하면 다른 파워로 바꾸거나 전력 제한을 낮춰 재현해 봅니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low", "automation": "", "auto_default": false},

    {"id": "a_cable", "label": "케이블·부품 재장착", "kind": "physical",
     "steps": ["영상 케이블과 해당 부품을 분리 후 다시 장착합니다.", "가능하면 다른 케이블·포트로 바꿔 재현해 봅니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low", "automation": "", "auto_default": false},

    {"id": "a_storage_check", "label": "저장장치 점검(SMART·오류 검사)", "kind": "inspect",
     "steps": ["SMART 값과 디스크 오류를 확인합니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low", "automation": "smart_check", "auto_default": true},

    {"id": "a_memory_test", "label": "메모리 테스트", "kind": "inspect",
     "steps": ["Windows 메모리 진단을 1회 이상 완주합니다.", "여러 개라면 한 개씩만 꽂아 재현해 봅니다."],
     "rollback": "", "reversible": true, "requires_reboot": true, "risk": "low", "automation": "", "auto_default": false},

    {"id": "a_hw_visual", "label": "하드웨어 일괄 육안 점검", "kind": "physical",
     "steps": ["온도·케이블·장착 상태를 한 번에 확인합니다."],
     "rollback": "", "reversible": true, "requires_reboot": false, "risk": "low", "automation": "", "auto_default": false}
  ],

  "checks": [
    {"id": "pause_overlay", "label": "오버레이 종료", "action": "a_overlay_off", "cost_seconds": 20, "reliability": 0.9, "categories": ["overlay"],
     "advice": "해당 오버레이(디스코드·스팀·NVIDIA 등)의 게임 내 오버레이만 끄고 문제를 재현해 보세요."},
    {"id": "pause_input_util", "label": "입력장치 유틸 종료", "action": "a_stop_process", "cost_seconds": 60, "reliability": 0.85, "categories": ["input_util"],
     "advice": "입력장치 유틸리티를 완전히 종료(트레이 아이콘 → 끝내기)하고 재현해 보세요."},
    {"id": "av_pause", "label": "백신 실시간 감시 일시 중지", "action": "a_av_pause", "cost_seconds": 120, "reliability": 0.85, "categories": ["antivirus"],
     "advice": "백신의 실시간 감시를 10분만 일시 중지하고 재현해 보세요. 삭제하지 마세요."},
    {"id": "pause_anticheat", "label": "안티치트 서비스 중지 후 재부팅", "action": "a_anticheat_stop", "cost_seconds": 300, "reliability": 0.8, "categories": ["anticheat"],
     "advice": "안티치트 서비스를 중지하고 재부팅한 뒤, 해당 게임을 실행하지 않은 상태로 문제를 재현해 보세요."},
    {"id": "pause_fin_security", "label": "금융 보안모듈 일시 중지", "action": "a_finsec_stop", "cost_seconds": 420, "reliability": 0.8, "categories": ["fin_security"],
     "advice": "금융·공공 보안모듈의 상주 실행을 중지하고 재현해 보세요. 인터넷뱅킹 사용 전에는 다시 켜야 합니다."},
    {"id": "firewall_allow", "label": "보안 제품 방화벽 예외 추가", "action": "a_firewall_allow", "cost_seconds": 180, "reliability": 0.85, "categories": ["security_firewall"],
     "advice": "보안 제품의 방화벽에서 해당 프로그램을 허용 목록에 추가하고 재현해 보세요."},
    {"id": "pause_other_sw", "label": "의심 프로그램 종료", "action": "a_stop_process", "cost_seconds": 60, "reliability": 0.8, "categories": ["other_sw"],
     "advice": "해당 프로그램을 종료한 상태로 재현해 보세요."},
    {"id": "clean_boot", "label": "클린 부트(서드파티 전체 중지)", "action": "a_clean_boot", "cost_seconds": 480, "reliability": 0.92, "group": true, "layers": ["SW"],
     "advice": "Microsoft 서비스를 제외한 모든 서비스와 시작 프로그램을 끄고 재부팅해 재현해 보세요. 소프트웨어 충돌 전체를 한 번에 가릅니다."},
    {"id": "rollback_gpu_driver", "label": "GPU 드라이버 롤백/재설치", "action": "a_gpu_rollback", "cost_seconds": 1800, "reliability": 0.85, "categories": ["gpu_driver"],
     "advice": "현재 드라이버를 제거(DDU)하고 이전 정식 버전으로 되돌린 뒤 재현해 보세요."},
    {"id": "remove_display_util", "label": "모니터 유틸 제거", "action": "a_uninstall", "cost_seconds": 300, "reliability": 0.9, "categories": ["display_util"],
     "advice": "모니터 제조사 유틸리티를 종료하거나 제거하고 재현해 보세요."},
    {"id": "rollback_peripheral_driver", "label": "주변기기 드라이버 롤백", "action": "a_driver_rollback", "cost_seconds": 900, "reliability": 0.82, "categories": ["peripheral_driver", "network_driver"],
     "advice": "장치 관리자에서 해당 장치 드라이버를 이전 버전으로 되돌리고 재현해 보세요."},
)JSON",
    R"JSON(    {"id": "uninstall_os_update", "label": "최근 Windows 업데이트 제거", "action": "a_os_update_remove", "cost_seconds": 2400, "reliability": 0.78, "categories": ["os_update", "other_driver"],
     "advice": "증상 시작 직전에 설치된 업데이트를 제거하고 재현해 보세요."},
    {"id": "cooling_check", "label": "냉각 점검(청소·팬 커브·써멀)", "action": "a_cooling", "cost_seconds": 3600, "reliability": 0.8, "categories": ["thermal"],
     "advice": "먼지 청소와 팬 커브 조정을 하고, 온도가 임계에 닿지 않는 상태에서 재현해 보세요."},
    {"id": "power_check", "label": "전원 점검(케이블·파워·전력 제한)", "action": "a_power", "cost_seconds": 3000, "reliability": 0.7, "categories": ["power"],
     "advice": "보조전원 케이블을 다시 연결하고, 가능하면 다른 파워로 바꾸거나 전력 제한을 낮춰 재현해 보세요."},
    {"id": "cable_reseat", "label": "케이블·부품 재장착", "action": "a_cable", "cost_seconds": 1200, "reliability": 0.75, "categories": ["connection"],
     "advice": "영상 케이블과 해당 부품을 분리 후 다시 장착하고, 가능하면 다른 케이블·포트로 바꿔 재현해 보세요."},
    {"id": "storage_check", "label": "저장장치 점검(SMART·오류 검사)", "action": "a_storage_check", "cost_seconds": 1800, "reliability": 0.8, "categories": ["storage"],
     "advice": "SMART 값과 디스크 오류를 확인하고, 가능하면 다른 저장장치에서 재현해 보세요."},
    {"id": "memory_test", "label": "메모리 테스트", "action": "a_memory_test", "cost_seconds": 5400, "reliability": 0.85, "categories": ["memory"],
     "advice": "메모리 진단 도구를 1회 이상 완주하고, 여러 개라면 한 개씩만 꽂아 재현해 보세요."},
    {"id": "hw_visual_check", "label": "하드웨어 일괄 육안 점검", "action": "a_hw_visual", "cost_seconds": 2400, "reliability": 0.6, "group": true, "layers": ["HW"],
     "advice": "온도·케이블·장착 상태를 한 번에 확인합니다. 하드웨어 층 전체를 거칠게 가릅니다."}
  ],

  "remedies": [
    {"id": "r_anticheat_autostart_off", "label": "안티치트를 해당 게임을 할 때만 켜기", "categories": ["anticheat"], "symptoms": ["app_crash", "freeze", "fps_drop"],
     "description": "다른 게임용 커널 안티치트가 상시 상주하면서 무관한 프로그램에 간섭하는 경우입니다. 상시 실행을 끄면 원래 게임은 그대로 쓰면서 충돌을 없앨 수 있습니다.",
     "actions": ["a_anticheat_autostart_off"], "success_seed": 0.7},
    {"id": "r_anticheat_repair", "label": "안티치트·게임 복구 및 최신 버전 업데이트", "categories": ["anticheat"],
     "description": "커널 안티치트는 버전 사이 호환 문제가 많고 최신 버전에서 고쳐지는 경우가 많습니다.",
     "actions": ["a_anticheat_repair"], "success_seed": 0.55},
    {"id": "r_anticheat_reinstall", "label": "안티치트(와 해당 게임) 재설치", "categories": ["anticheat"],
     "description": "설치가 손상되었거나 이전 버전 드라이버가 남아 있을 때 쓰는 마지막 방법입니다.",
     "actions": ["a_reinstall"], "success_seed": 0.45},

    {"id": "r_av_exclude", "label": "대상 프로그램을 백신 예외에 등록", "categories": ["antivirus"],
     "description": "백신이 정상 프로그램을 오탐해 격리하거나 실행을 막는 경우입니다. 예외 등록은 해당 폴더만 검사에서 빼므로 백신 전체를 끄는 것보다 안전합니다.",
     "actions": ["a_av_exclude"], "success_seed": 0.7},
    {"id": "r_av_single", "label": "실시간 감시 백신을 하나만 남기기", "categories": ["antivirus"],
     "description": "백신 두 개가 동시에 실시간 감시를 하면 서로의 훅이 겹쳐 프로그램이 멈추거나 튕길 수 있습니다.",
     "actions": ["a_av_single"], "success_seed": 0.5},
    {"id": "r_av_report_fp", "label": "백신 제조사에 오탐 신고", "categories": ["antivirus"], "symptoms": ["launch_fail", "app_crash"],
     "description": "예외 등록은 임시 해결입니다. 제조사가 탐지 규칙을 고치면 예외 없이도 정상 동작합니다.",
     "actions": ["a_av_report_fp"], "success_seed": 0.4},

    {"id": "r_finsec_autostart_off", "label": "금융 보안모듈 자동 실행 끄기", "categories": ["fin_security"],
     "description": "키보드 보안·안전 거래 모듈은 상시 상주하면서 입력 장치와 네트워크에 필터를 겁니다. 인터넷뱅킹을 할 때만 실행되게 하면 다른 프로그램과의 충돌이 사라집니다.",
     "actions": ["a_finsec_autostart_off"], "success_seed": 0.65},
    {"id": "r_finsec_remove_unused", "label": "사용하지 않는 보안모듈 제거", "categories": ["fin_security"],
     "description": "한 번 설치된 보안모듈은 사이트를 다시 쓰지 않아도 남아 있습니다. 쓰지 않는 모듈을 지우면 필터 드라이버가 줄어듭니다.",
     "actions": ["a_finsec_uninstall"], "success_seed": 0.6},
    {"id": "r_finsec_reinstall", "label": "보안모듈 최신 버전으로 재설치", "categories": ["fin_security"],
     "description": "이전 버전 모듈이 Windows 업데이트 후 호환되지 않는 경우가 있습니다.",
     "actions": ["a_reinstall"], "success_seed": 0.5},

    {"id": "r_fw_allow", "label": "보안 제품 방화벽에 허용 규칙 유지", "categories": ["security_firewall"],
     "description": "보안 제품의 방화벽이 대상 프로그램의 통신을 막고 있습니다. 허용 규칙을 남겨 두면 해결됩니다.",
     "actions": ["a_firewall_allow"], "success_seed": 0.75},

    {"id": "r_overlay_off", "label": "해당 오버레이를 계속 꺼 두기", "categories": ["overlay"],
     "description": "오버레이는 게임에 DLL을 주입해 화면을 그립니다. 꺼 두면 주입이 일어나지 않습니다.",
     "actions": ["a_overlay_off"], "success_seed": 0.8},
    {"id": "r_overlay_update", "label": "오버레이 프로그램 업데이트", "categories": ["overlay"],
     "description": "오버레이 기능이 꼭 필요하면 최신 버전으로 올려 호환 문제가 고쳐졌는지 확인합니다.",
     "actions": ["a_program_update"], "success_seed": 0.4},

    {"id": "r_input_util_update", "label": "입력장치 유틸 업데이트", "categories": ["input_util"],
     "description": "입력 필터 드라이버의 이전 버전이 다른 필터와 충돌하는 경우가 많습니다.",
     "actions": ["a_program_update"], "success_seed": 0.5},
    {"id": "r_input_util_remove", "label": "입력장치 유틸 제거", "categories": ["input_util"],
     "description": "유틸이 꼭 필요하지 않다면 제거하면 필터 드라이버도 함께 빠집니다.",
     "actions": ["a_uninstall"], "success_seed": 0.6},

    {"id": "r_other_update", "label": "해당 프로그램 업데이트", "categories": ["other_sw"],
     "description": "최신 버전에서 호환 문제가 고쳐졌는지 확인합니다.", "actions": ["a_program_update"], "success_seed": 0.45},
    {"id": "r_other_remove", "label": "해당 프로그램 제거", "categories": ["other_sw"],
     "description": "필요 없는 프로그램이면 제거합니다.", "actions": ["a_uninstall"], "success_seed": 0.6},

    {"id": "r_gpu_rollback", "label": "GPU 드라이버 이전 버전 유지", "categories": ["gpu_driver"],
     "description": "증상이 사라진 이전 버전을 유지하고, 다음 정식 버전에서 고쳐졌는지 확인합니다.", "actions": ["a_gpu_rollback"], "success_seed": 0.6},
    {"id": "r_display_util_remove", "label": "모니터 유틸 제거", "categories": ["display_util"],
     "description": "모니터 제조사 유틸이 디스플레이 드라이버 설정을 바꾸는 경우입니다.", "actions": ["a_uninstall"], "success_seed": 0.7},
    {"id": "r_driver_rollback", "label": "장치 드라이버 이전 버전 유지", "categories": ["peripheral_driver", "network_driver", "other_driver"],
     "description": "증상이 사라진 이전 버전을 유지합니다.", "actions": ["a_driver_rollback"], "success_seed": 0.55},
    {"id": "r_os_update_remove", "label": "문제 업데이트 제거 후 일시 보류", "categories": ["os_update"],
     "description": "업데이트를 제거하고, 다음 누적 업데이트에서 고쳐졌는지 확인합니다.", "actions": ["a_os_update_remove"], "success_seed": 0.5},
    {"id": "r_thermal", "label": "냉각 개선", "categories": ["thermal"],
     "description": "청소·써멀 재도포·팬 커브 조정으로 스로틀링을 없앱니다.", "actions": ["a_cooling"], "success_seed": 0.65},
    {"id": "r_power", "label": "전원 공급 개선", "categories": ["power"],
     "description": "케이블 재연결·파워 교체·전력 제한 조정.", "actions": ["a_power"], "success_seed": 0.55},
    {"id": "r_connection", "label": "케이블·포트 교체", "categories": ["connection"],
     "description": "접촉 불량 케이블이나 포트를 바꿉니다.", "actions": ["a_cable"], "success_seed": 0.6},
    {"id": "r_storage", "label": "저장장치 교체 준비·백업", "categories": ["storage"],
     "description": "SMART 경고가 있으면 백업 후 교체를 준비합니다.", "actions": ["a_storage_check"], "success_seed": 0.5},
    {"id": "r_memory", "label": "불량 메모리 모듈 교체", "categories": ["memory"],
     "description": "진단에서 오류가 난 모듈을 빼거나 교체합니다.", "actions": ["a_memory_test"], "success_seed": 0.6}
  ],

  "baseline_checklist": [
    "rollback_gpu_driver",
    "clean_boot",
    "av_pause",
    "uninstall_os_update",
    "cable_reseat",
    "memory_test",
    "storage_check",
    "cooling_check",
    "power_check"
  ],

  "cases": [
    {"symptom": "app_crash",    "category": "anticheat",         "subject": "vgk.sys", "weight": 0.7,  "solved": 0, "rejected": 0},
    {"symptom": "app_crash",    "category": "overlay",           "subject": "",        "weight": 0.5,  "solved": 0, "rejected": 0},
    {"symptom": "input_loss",   "category": "fin_security",      "subject": "",        "weight": 0.65, "solved": 0, "rejected": 0},
    {"symptom": "input_loss",   "category": "input_util",        "subject": "",        "weight": 0.6,  "solved": 0, "rejected": 0},
    {"symptom": "network_fail", "category": "security_firewall", "subject": "",        "weight": 0.7,  "solved": 0, "rejected": 0},
    {"symptom": "launch_fail",  "category": "antivirus",         "subject": "",        "weight": 0.6,  "solved": 0, "rejected": 0},
    {"symptom": "flicker",      "category": "display_util",      "subject": "",        "weight": 0.55, "solved": 0, "rejected": 0},
    {"symptom": "fps_drop",     "category": "thermal",           "subject": "",        "weight": 0.6,  "solved": 0, "rejected": 0},
    {"symptom": "unexpected_reboot", "category": "power",        "subject": "",        "weight": 0.6,  "solved": 0, "rejected": 0}
  ]
}
)JSON",
    nullptr};

}  // namespace internal
}  // namespace ce
