// snapshot.hpp - 수집기(블랙박스)가 마킹 시점에 덤프하는 스냅샷의 메모리 표현
// 대응 스키마 문서: docs/snapshot_schema.md (제안서 5.2 수집 신호 표 기준)
//
// 수집기 MVP(10/9 목표)에서 먼저 잡히는 신호는 이벤트 1000/1001, UpperFilters, 로드된 DLL,
// 센서 가용 여부다. 나머지 필드는 없어도 되며, 없는 항목은 '확인 불가'로 처리해 신뢰도만 낮춘다.
#pragma once
#include <string>
#include <vector>

#include "ce/json.hpp"

namespace ce {

// 증상 유형 (제안서 7.1 크래시 유형)
enum class Symptom {
  Unknown,
  AppCrash,         // 프로그램 비정상 종료
  Freeze,           // 프리징
  FpsDrop,          // 프레임 저하
  Flicker,          // 화면 깜빡임·끊김
  InputLoss,        // 입력 누락
  LaunchFail,       // 실행 불가
  NetworkFail,      // 네트워크 접속 불가·끊김
  UnexpectedReboot  // 예고 없는 재부팅
};

// 증상이 나타나는 범위 - 층 판정의 1차 단서 (제안서 5.3)
enum class SymptomScope {
  Unknown,
  SingleApp,     // 특정 프로그램 하나
  MultipleApps,  // 무관한 여러 프로그램
  SystemWide     // 시스템 전체
};

enum class Layer { Unknown, Software, Driver, Hardware };

std::string toString(Layer l);
std::string toString(Symptom s);
std::string toString(SymptomScope s);
Symptom symptomFromString(const std::string& s);
Layer layerFromString(const std::string& s);
SymptomScope scopeFromString(const std::string& s);

struct ModuleInfo {
  std::string process;  // 어느 프로세스에 로드되었는가
  std::string name;     // overlay64.dll
  std::string path;
  std::string company;
  std::string signer;
  std::string version;
  bool microsoft_signed = false;
  long long loaded_at = 0;
};

struct DriverInfo {
  std::string name;     // vgk.sys
  std::string display;  // Riot Vanguard
  std::string type;     // kernel_filter | file_system | network | display ...
  std::string company;
  std::string version;
  long long started_at = 0;
};

struct ChangeItem {  // 설치·업데이트·설정 변경 이력 (시간 상관의 근거)
  long long time = 0;
  std::string kind;     // install | update | uninstall | driver_update | setting | os_update
  std::string subject;  // 프로그램/드라이버 이름
  std::string version;
};

struct EventItem {  // Windows 이벤트 로그 항목
  long long time = 0;
  std::string provider;  // Application Error, Display, Kernel-Power, WHEA-Logger ...
  int id = 0;            // 1000, 1001, 4101, 41 ...
  std::string subject;   // 대상 프로세스/장치 (BugCheck 이벤트면 버그체크 코드)
  std::string faulting_module;
  std::string component;  // WHEA 오류가 난 부품: memory | cpu | pcie | cache (WHEA 이벤트만)
};

struct SensorSample {
  long long time = 0;
  double cpu_temp = -1;  // 음수 = 미지원(확인 불가)
  double gpu_temp = -1;
  double cpu_clock = -1;  // MHz
  double gpu_clock = -1;
  bool throttling = false;
  std::string gpu_power_limit_reason;  // thermal | power | voltage ...
};

struct FrameSample {
  long long time = 0;
  double frame_ms = 0;  // PresentMon 프레임타임
};

struct FirewallBlock {
  long long time = 0;
  std::string process;
  std::string direction;  // inbound | outbound
  std::string product;    // 차단한 보안 제품
};

// 보안 제품의 탐지·격리·차단 기록 (Defender 1116/1117, V3·알약 로그 등)
// 실행 불가(launch_fail) 증상에서 가장 직접적인 근거가 된다.
struct SecurityDetection {
  long long time = 0;
  std::string product;  // AhnLab V3, Microsoft Defender ...
  std::string target;   // 대상 파일·프로세스 이름
  std::string action;   // quarantine | block | kill | detect
  std::string threat;   // 탐지명(오탐 여부 판단용)
};

struct MiniDumpInfo {
  long long time = 0;
  std::string kind;      // app(앱 크래시 덤프) | kernel(블루스크린 커널 덤프)
  std::string process;
  std::string faulting_module;
  std::string bugcheck;  // 커널 덤프의 버그체크 코드와 이름 (예: "0x116 VIDEO_TDR_FAILURE")
  std::vector<std::string> third_party_in_stack;
};

// 저장장치 SMART 상태 (드라이브 1개)
struct DiskHealth {
  std::string name;              // Samsung SSD 970 EVO ...
  std::string health;            // good | warning | bad (도구가 판정한 값, 없으면 빈 값)
  int reallocated_sectors = 0;   // 재할당된 섹터 수
  int pending_sectors = 0;       // 재할당 대기 섹터 수
  int uncorrectable = 0;         // 고칠 수 없는 오류 수
  int media_errors = 0;          // NVMe 미디어 오류 수
};

struct DeviceFilters {
  std::vector<std::string> keyboard_upper;  // 키보드 클래스 UpperFilters
  std::vector<std::string> mouse_upper;     // 마우스 클래스 UpperFilters
  std::vector<std::string> ndis_filters;    // 네트워크 어댑터 필터 드라이버
};

struct BaselineDiff {  // 베이스라인 스냅샷 대비 변화 (제안서 5.1)
  bool available = false;
  std::vector<std::string> added;
  std::vector<std::string> removed;
  std::vector<std::string> changed;
};

// 수집 항목별 가용성. 값이 없으면 '확인 불가'로 표시하고 신뢰도를 낮춘다.
struct Availability {
  bool modules = false;
  bool drivers = false;
  bool device_filters = false;
  bool events = false;
  bool sensors = false;
  bool frametime = false;
  bool change_history = false;
  bool baseline = false;
  bool minidump = false;
  bool firewall = false;
  bool detections = false;
  bool processes = false;
  bool storage = false;

  int countCore() const;  // 신뢰도 계산에 쓰는 핵심 7항목 중 받은 개수
};

struct Snapshot {
  std::string schema_version;
  std::string snapshot_id;
  long long marked_at = 0;    // 마킹 시점
  int tz_offset_minutes = 0;  // 수집 시각 표기에 쓰인 시간대(한국이면 540)
  std::string trigger_kind;   // hotkey | auto_app_crash | auto_frametime | auto_system | verify
  std::string os_version;
  long long observed_from = 0;  // 이 스냅샷이 관찰한 구간의 시작(검증용 사후 스냅샷에서 사용)

  Symptom symptom = Symptom::Unknown;
  SymptomScope scope = SymptomScope::Unknown;
  std::string target_process;
  long long symptom_started_at = 0;           // 사용자가 기억하는 증상 시작 시점(없으면 0)
  std::vector<std::string> faulting_modules;  // 여러 번의 크래시에서 모인 오류 모듈

  std::vector<std::string> processes;  // 실행 중인 프로세스 이름
  std::vector<ModuleInfo> modules;
  std::vector<DriverInfo> drivers;
  DeviceFilters device_filters;
  std::vector<std::string> realtime_av;  // 실시간 감시 중인 백신
  std::vector<FirewallBlock> firewall_blocks;
  std::vector<SecurityDetection> detections;
  std::vector<ChangeItem> changes;
  std::vector<EventItem> events;
  std::vector<SensorSample> sensors;
  std::vector<FrameSample> frames;
  std::vector<MiniDumpInfo> minidumps;
  std::vector<DiskHealth> disks;
  BaselineDiff baseline;
  Availability available;

  // 벤치마크 전용. 실측 스냅샷에는 없고 실험용 시나리오 파일에만 들어간다.
  bool has_ground_truth = false;
  Layer truth_layer = Layer::Unknown;
  std::string truth_category;
  std::string truth_subject;

  // 편의 조회
  std::vector<int> moduleIndexesOf(const std::string& process) const;  // 빈 문자열이면 전체
  bool hasEvent(int id) const;
  int countEvents(int id) const;
  bool processRunning(const std::string& name) const;
};

// JSON -> Snapshot. 누락 필드는 경고에 남기고 계속 진행한다(부분 수집 허용).
Snapshot parseSnapshot(const json::Value& v, std::vector<std::string>* warnings);
bool loadSnapshotFile(const std::string& path, Snapshot* out, std::vector<std::string>* warnings,
                      std::string* error);

// 문자열 도우미 (여러 모듈에서 공통으로 사용)
std::string toLower(const std::string& s);
bool containsNoCase(const std::string& haystack, const std::string& needle);
bool equalsNoCase(const std::string& a, const std::string& b);

}  // namespace ce
