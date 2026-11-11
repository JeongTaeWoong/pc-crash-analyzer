/* FaultPlugin — FaultLab의 "서드파티 플러그인 DLL 크래시" 재현용.
 * 호출하면 DLL 코드 안에서 널 포인터에 쓰기 → 접근 위반(0xC0000005).
 * 이벤트 1000의 오류 모듈이 FaultPlugin.dll(비시스템 경로)로 기록되어 s6(백신 후킹 DLL 크래시) 패턴을 흉내 낸다.
 */
#include <stddef.h>

__declspec(dllexport) void __cdecl fault_crash(void)
{
    volatile int *p = (volatile int *)0;
    *p = 1;
}
