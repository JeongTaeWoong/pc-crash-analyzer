cmake_minimum_required(VERSION 3.20)
project(FaultPlugin C)
add_library(FaultPlugin SHARED FaultPlugin.c)
add_executable(FaultHost FaultHost.c)
# 빌드: cmake -S plugin -B plugin/build -G "Visual Studio 17 2022" -A x64 && cmake --build plugin/build --config Release
# 결과: plugin/build/Release/FaultPlugin.dll, FaultHost.exe
