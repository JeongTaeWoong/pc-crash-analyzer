/* FaultHost — 네이티브 호스트. .NET 호스트에서는 CLR이 접근 위반을 가로채 오류 모듈이 coreclr.dll로 기록되므로(10/28 실측),
 * "오류 모듈 = 서드파티 플러그인 DLL"(s6 패턴)을 재현하려면 네이티브 exe가 DLL을 불러 죽어야 한다.
 * 사용: FaultHost.exe [FaultPlugin.dll 경로]  (기본: 같은 폴더의 FaultPlugin.dll)
 */
#include <windows.h>
#include <stdio.h>

typedef void (__cdecl *fault_fn)(void);

int main(int argc, char **argv)
{
    const char *dll = argc > 1 ? argv[1] : "FaultPlugin.dll";
    HMODULE h = LoadLibraryA(dll);
    if (!h) { printf("LoadLibrary 실패: %s (err %lu)\n", dll, GetLastError()); return 2; }
    fault_fn f = (fault_fn)GetProcAddress(h, "fault_crash");
    if (!f) { printf("fault_crash 없음\n"); return 3; }
    printf("FAULTHOST PID=%lu dll=%s\n", GetCurrentProcessId(), dll);
    fflush(stdout);
    f(); /* 여기서 DLL 안의 널 쓰기 → 0xC0000005, 오류 모듈 = FaultPlugin.dll */
    return 0;
}
