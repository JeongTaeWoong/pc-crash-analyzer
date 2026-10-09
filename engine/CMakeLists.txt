cmake_minimum_required(VERSION 3.15)
project(pc_crash_analyzer_engine CXX)

# 추론 엔진 (담당: 송민석) — C++17 표준만 사용, 외부 라이브러리 없음
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(MSVC)
  add_compile_options(/W4 /utf-8)
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX)
else()
  add_compile_options(-Wall -Wextra)
endif()

add_library(ce_core
  src/json.cpp
  src/snapshot.cpp
  src/rules.cpp
  src/rules_default.cpp
  src/evidence.cpp
  src/signals.cpp
  src/layer.cpp
  src/candidates.cpp
  src/planner.cpp
  src/remedy.cpp
  src/engine.cpp
  src/feedback.cpp
  src/session.cpp
  src/tool.cpp
  src/bench.cpp
)
target_include_directories(ce_core PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)

add_executable(crash_engine src/main.cpp)
target_link_libraries(crash_engine PRIVATE ce_core)

enable_testing()
add_executable(ce_tests tests/test_main.cpp)
target_link_libraries(ce_tests PRIVATE ce_core)
add_test(NAME ce_tests COMMAND ce_tests ${CMAKE_CURRENT_SOURCE_DIR}/data)
