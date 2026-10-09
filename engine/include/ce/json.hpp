// json.hpp - 외부 의존성 없이 쓰는 최소 JSON 파서/직렬화기
// 엔진은 스냅샷·규칙·세션 상태·도구 입출력을 모두 JSON으로 주고받으므로
// 서드파티 라이브러리 없이 빌드되도록 직접 구현한다.
#pragma once
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace ce {
namespace json {

class Value;
using Object = std::map<std::string, Value>;
using Array = std::vector<Value>;

class Value {
 public:
  enum class Type { Null, Bool, Number, String, Array, Object };

  Value() : type_(Type::Null) {}
  Value(bool b) : type_(Type::Bool), bool_(b) {}
  Value(double n) : type_(Type::Number), num_(n) {}
  Value(int n) : type_(Type::Number), num_(static_cast<double>(n)) {}
  Value(long long n) : type_(Type::Number), num_(static_cast<double>(n)) {}
  Value(const char* s) : type_(Type::String), str_(s) {}
  Value(std::string s) : type_(Type::String), str_(std::move(s)) {}
  Value(Array a) : type_(Type::Array), arr_(std::move(a)) {}
  Value(Object o) : type_(Type::Object), obj_(std::move(o)) {}

  Type type() const { return type_; }
  bool isNull() const { return type_ == Type::Null; }
  bool isArray() const { return type_ == Type::Array; }
  bool isObject() const { return type_ == Type::Object; }
  bool isNumber() const { return type_ == Type::Number; }
  bool isString() const { return type_ == Type::String; }

  // 값이 없거나 형이 다르면 기본값을 돌려준다(수집 실패 = '확인 불가' 처리를 위해).
  bool asBool(bool def = false) const { return type_ == Type::Bool ? bool_ : def; }
  double asNumber(double def = 0.0) const { return type_ == Type::Number ? num_ : def; }
  int asInt(int def = 0) const {
    return type_ == Type::Number ? static_cast<int>(num_) : def;
  }
  std::string asString(const std::string& def = "") const {
    return type_ == Type::String ? str_ : def;
  }
  const Array& asArray() const;
  const Object& asObject() const;
  std::vector<std::string> asStringList() const;

  // obj["key"] / arr[i] : 없으면 Null 값을 반환한다(예외 없음).
  const Value& operator[](const std::string& key) const;
  const Value& operator[](std::size_t idx) const;
  bool has(const std::string& key) const;
  std::size_t size() const;

  // 객체의 키 목록 (map 이라 사전순)
  std::vector<std::string> keys() const;

  void set(const std::string& key, Value v);
  void push(Value v);

  std::string dump(int indent = 2) const;

  static Value parse(const std::string& text, std::string* error = nullptr);
  static Value parseFile(const std::string& path, std::string* error = nullptr);
  static bool writeFile(const std::string& path, const Value& v, std::string* error = nullptr);
  static Value makeObject() { return Value(Object{}); }
  static Value makeArray() { return Value(Array{}); }

 private:
  void dumpTo(std::string& out, int indent, int depth) const;

  Type type_;
  bool bool_ = false;
  double num_ = 0.0;
  std::string str_;
  Array arr_;
  Object obj_;
};

// ISO-8601(예: 2026-10-16T14:32:10+09:00) -> epoch 초. 실패하면 false.
// offsetMinutes 를 주면 문자열에 적혀 있던 시간대 오프셋(분)을 함께 돌려준다.
bool parseIso8601(const std::string& text, long long* epochSeconds, int* offsetMinutes = nullptr);
// epoch 초 -> ISO-8601. offsetMinutes 를 주면 그 시간대(예: 한국 +540)로 표기한다.
std::string formatIso8601(long long epochSeconds, int offsetMinutes = 0);

}  // namespace json
}  // namespace ce
