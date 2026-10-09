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

bool isAppCrashEvent(const EventItem& e);    // 1000/1001
bool isDisplayEvent(const EventItem& e);     // 4101 등 디스플레이 드라이버 복구
bool isKernelPower41(const EventItem& e);    // 예고 없는 종료
bool isWheaEvent(const EventItem& e);        // 하드웨어 오류
bool isDiskEvent(const EventItem& e);        // 저장장치 오류
bool isDisconnectEvent(const EventItem& e);  // 모니터/USB 연결 해제
bool isMemoryEvent(const EventItem& e);

// 여러 크래시에서 모인 오류 모듈(소문자, 중복 제거). 매번 다르면 HW 가설의 근거가 된다.
std::vector<std::string> distinctFaultingModules(const Snapshot& s);

double maxCpuTemp(const Snapshot& s);  // 값이 없으면 -1
double maxGpuTemp(const Snapshot& s);
bool throttlingSeen(const Snapshot& s);
// 프레임 저하 구간과 클럭 하락이 같은 시각에 일어났는가
bool clockDropMatchesFrameDrop(const Snapshot& s, long long* whenOut);

bool isMicrosoftModule(const Snapshot& s, const std::string& moduleName);

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
