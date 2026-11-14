cmake_minimum_required(VERSION 3.20)
project(FaultPlugin C)
# 정적 CRT(/MT): VC++ 재배포 패키지가 없는 PC에서도 실행되어야 한다.
# 11/12 실험실 게스트(design1)에서 /MD 빌드가 0xC0000135(DLL 없음)로 시작도 못 했다 — VCRUNTIME140.dll 이 없었다.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_library(FaultPlugin SHARED FaultPlugin.c)
add_executable(FaultHost FaultHost.c)
# 빌드: cmake -S plugin -B plugin/build -G "Visual Studio 17 2022" -A x64 && cmake --build plugin/build --config Release
# cmake 없이(VS 개발자 명령 프롬프트, plugin 폴더에서):
#   cl /nologo /utf-8 /O2 /MT FaultHost.c /Fe:build\Release\FaultHost.exe
#   cl /nologo /utf-8 /O2 /MT /LD FaultPlugin.c /Fe:build\Release\FaultPlugin.dll
# 결과: plugin/build/Release/FaultPlugin.dll, FaultHost.exe
