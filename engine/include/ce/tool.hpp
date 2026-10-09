// tool.hpp - 에이전트(LLM)·UI·수집기가 엔진을 '도구'로 부르기 위한 JSON 인터페이스
//
// 멘토링 1차 반영: 추론 엔진은 팀이 직접 구현하는 공학적 핵심으로 두고, LLM 에이전트
// (LangGraph 등)는 이 도구를 호출해 결과를 해석·설명·조율한다.
//
//   crash_engine tool <이름> [--input args.json]     (인자가 없으면 표준입력에서 JSON 을 읽음)
//   출력: {"ok":true,"tool":"<이름>","result":{...}}  또는  {"ok":false,"error":"..."}
//
// 프로세스는 매번 새로 떠도 된다. 세션 상태는 session_path 의 JSON 파일에 저장된다.
#pragma once
#include <string>

#include "ce/json.hpp"
#include "ce/rules.hpp"

namespace ce {

// 도구 목록과 입력 스키마(JSON Schema). LangGraph/Google ADK 의 tool 정의로 그대로 옮길 수 있다.
json::Value toolSpecs();

// 도구 하나를 실행한다. 성공하면 true 와 result, 실패하면 false 와 error.
bool runTool(const std::string& name, const json::Value& args, json::Value* result,
             std::string* error);

// runTool 결과를 표준 응답 형태로 감싼다.
json::Value toolResponse(const std::string& name, bool ok, const json::Value& result,
                         const std::string& error);

}  // namespace ce
