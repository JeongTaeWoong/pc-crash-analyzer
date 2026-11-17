// signals.hpp - 스냅샷에서 층 판정과 점수화가 공통으로 쓰는 신호 추출 함수
#pragma once
#include <string>
#include <vector>

#include "ce/rules.hpp"
#include "ce/snapshot.hpp"

namespace ce {
namespace signals {

// 증상 기준 시각: 사용자가 말한 시작 시점이 있으면 그것, 없으면 마킹 시각
long long referenceTime(const Snapshot& s);

// 두 시각의 가까움을 0~1로 환산한다. 완전 일치 근처면 1, 창 밖이면 0.
// 변경이 증상보다 '뒤'에 일어난 경우는 원인일 수 없으므로 크게 깎는다.
double timeCorrelation(long long changeTime, long long symptomTime, const Rules& r);

bool isLiveKernelEvent(const EventItem& e);  // WER 1001 LiveKernelEvent (수집기가 subject 를 "LiveKernelEvent 0x141" 로 넣는다)
bool isAppCrashEvent(const EventItem& e);    // 1000/1001 (라이브 커널 이벤트 제외)
bool isDisplayEvent(const EventItem& e);     // 4101 등 디스플레이 드라이버 복구
bool isKernelPower41(const EventItem& e);    // 예고 없는 종료
bool isWheaEvent(const EventItem& e);        // 하드웨어 오류
bool isDiskEvent(const EventItem& e);        // 저장장치 오류
bool isDisconnectEvent(const EventItem& e);  // 모니터/USB 연결 해제
bool isMemoryEvent(const EventItem& e);
bool isBugcheckEvent(const EventItem& e);     // 블루스크린 후 기록되는 BugCheck / WER-SystemErrorReporting
bool isNetworkLinkEvent(const EventItem& e);  // 네트워크 어댑터 링크 끊김 (NDIS·랜카드 드라이버)
std::string wheaComponent(const EventItem& e);  // WHEA 오류 부품: memory | cpu | pcie | cache | ""

// Kernel-Power 41 은 '갑자기 전원이 나간 것'과 '블루스크린 뒤 재부팅' 둘 다에서 기록된다.
// 41 의 BugcheckCode 가 0이고, 앞뒤 10분 안에 블루스크린 흔적이 없을 때만 전원 문제의 근거로 쓴다.
bool isPowerLossEvent(const Snapshot& s, const EventItem& e);
bool hasNonZeroBugcheckCode(const std::string& subject);  // "BugcheckCode 278" → true

bool isKernelDump(const MiniDumpInfo& d);  // 블루스크린 커널 덤프인가
int kernelDumpCount(const Snapshot& s);
// 블루스크린이 하드웨어 오류 때문인가 (0x124 WHEA_UNCORRECTABLE_ERROR 등)
bool isHardwareBugcheck(const std::string& bugcheck);

// SMART 경고가 있는 드라이브 수 (재할당·대기 섹터, 고칠 수 없는 오류, 미디어 오류, 도구 판정 warning/bad)
int disksWithWarning(const Snapshot& s);
bool diskHasWarning(const DiskHealth& d);

// 원인 카테고리별로 '그 원인이 아직 남아 있다'는 신호가 since 이후 몇 번 있었는지 센다.
// 세션 검증에서 증상 재발과 함께 본다. 해당 카테고리에 객관적 신호가 없으면 -1.
int causeSignalsSince(const Snapshot& s, const std::string& category, long long since);

// 여러 크래시에서 모인 오류 모듈(소문자, 중복 제거). 매번 다르면 HW 가설의 근거가 된다.
// 모듈 이름처럼 생기지 않은 값("0", "506d7054" 등 — 크래시가 아닌 WER 1001 보고의 다른 칸)은 세지 않는다.
std::vector<std::string> distinctFaultingModules(const Snapshot& s);

// 파일 이름처럼 생겼는가: 마지막 '.' 뒤에 글자가 든 2~5자 확장자가 있다 (foo.exe, python313.dll, x.node).
bool looksLikeModuleName(const std::string& name);

// '이번 일'의 오류 모듈(소문자). 지목된 프로그램 자신의 크래시 기록만 본다: 증상에 적힌 오류 모듈과, 대상 프로그램의
// 크래시 이벤트·앱 덤프. 같은 시간대에 다른 프로그램이 남긴 기록은 섞지 않는다. 한 가지로 정해지지 않으면 "".
// (.sys 는 제외 — 블루스크린 쪽에서 따로 다룬다)
std::string targetFaultModule(const Snapshot& s);

// 지목된 프로그램의 크래시가 오류 모듈과 함께 기록되어 있는가 (증상의 오류 모듈, 또는 그 프로그램의 크래시 이벤트).
// 대상이 없으면 창 안의 아무 크래시 기록이나 센다.
bool targetHasCrashRecord(const Snapshot& s);

// 이름이 증상이 난 프로그램 자신인가: 실행 파일("Godot.exe" == target "godot.exe") 또는 그와 이름이 같은 DLL
// (.NET 프로그램의 본체 "App.dll"). 대상이 없으면 false.
bool isTargetProgram(const Snapshot& s, const std::string& name);

// Windows 에 들어 있는 필터·기본 드라이버인가. 어느 PC 에나 있으므로 '상주 중'이라는 것만으로는 후보가 아니다.
// 제조사가 Microsoft 로 온 드라이버, 또는 이름이 확인된 목록(signals.cpp)에 있는 드라이버.
bool isWindowsInboxDriver(const DriverInfo& d);

double maxCpuTemp(const Snapshot& s);  // 값이 없으면 -1
double maxGpuTemp(const Snapshot& s);
bool throttlingSeen(const Snapshot& s);
// 프레임 저하 구간과 클럭 하락이 같은 시각에 일어났는가
bool clockDropMatchesFrameDrop(const Snapshot& s, long long* whenOut);

bool isMicrosoftModule(const Snapshot& s, const std::string& moduleName);
bool isDriverModuleName(const std::string& moduleName);  // .sys 로 끝나면 커널 드라이버

// UpperFilters 값에 원래 들어 있는 Windows 기본 클래스 드라이버(kbdclass, mouclass)인가.
// 이 둘은 서드파티 필터가 아니므로 후보·근거에서 뺀다.
bool isWindowsClassFilter(const std::string& name);
// 기본 클래스 드라이버를 뺀 서드파티 입력 필터 개수
int thirdPartyInputFilterCount(const Snapshot& s);

// 보안 제품 탐지 기록 중 대상 프로그램을 건드린 것의 인덱스 목록
std::vector<int> detectionsOnTarget(const Snapshot& s);
// 대상 프로그램 크래시·응답 없음 이벤트(1000/1001/1002)가 기준 시각 이후 몇 번 있었는가 (검증 단계에서 사용)
int crashEventsSince(const Snapshot& s, long long since);

}  // namespace signals
}  // namespace ce
