// json.cpp - 외부 라이브러리 없이 직접 만든 JSON 읽기/쓰기, 그리고 ISO-8601 시각 변환.
//   [팀원용] 엔진 로직과는 상관없는 도구 코드라 처음 읽을 때는 건너뛰어도 된다.

#include "ce/json.hpp"

#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>

namespace ce {
namespace json {

namespace {
const Value kNull;
const Array kEmptyArray;
const Object kEmptyObject;
}  // namespace

const Array& Value::asArray() const { return type_ == Type::Array ? arr_ : kEmptyArray; }
const Object& Value::asObject() const { return type_ == Type::Object ? obj_ : kEmptyObject; }

std::vector<std::string> Value::asStringList() const {
  std::vector<std::string> out;
  const Array& a = asArray();
  for (std::size_t i = 0; i < a.size(); i++) {
    if (a[i].isString()) out.push_back(a[i].asString());
  }
  return out;
}

std::vector<std::string> Value::keys() const {
  std::vector<std::string> out;
  if (type_ != Type::Object) return out;
  for (Object::const_iterator it = obj_.begin(); it != obj_.end(); ++it) {
    out.push_back(it->first);
  }
  return out;
}

const Value& Value::operator[](const std::string& key) const {
  if (type_ != Type::Object) return kNull;
  Object::const_iterator it = obj_.find(key);
  if (it == obj_.end()) return kNull;
  return it->second;
}

const Value& Value::operator[](std::size_t idx) const {
  if (type_ != Type::Array || idx >= arr_.size()) return kNull;
  return arr_[idx];
}

bool Value::has(const std::string& key) const {
  return type_ == Type::Object && obj_.find(key) != obj_.end();
}

std::size_t Value::size() const {
  if (type_ == Type::Array) return arr_.size();
  if (type_ == Type::Object) return obj_.size();
  return 0;
}

void Value::set(const std::string& key, Value v) {
  if (type_ != Type::Object) {
    type_ = Type::Object;
    obj_.clear();
  }
  obj_[key] = std::move(v);
}

void Value::push(Value v) {
  if (type_ != Type::Array) {
    type_ = Type::Array;
    arr_.clear();
  }
  arr_.push_back(std::move(v));
}

// ---------------------------------------------------------------- 직렬화

namespace {

void escapeTo(std::string& out, const std::string& s) {
  out += '"';
  for (std::size_t i = 0; i < s.size(); i++) {
    unsigned char c = static_cast<unsigned char>(s[i]);
    if (c == '"') {
      out += "\\\"";
    } else if (c == '\\') {
      out += "\\\\";
    } else if (c == '\n') {
      out += "\\n";
    } else if (c == '\r') {
      out += "\\r";
    } else if (c == '\t') {
      out += "\\t";
    } else if (c < 0x20) {
      char buf[8];
      std::snprintf(buf, sizeof(buf), "\\u%04x", c);
      out += buf;
    } else {
      out += static_cast<char>(c);  // UTF-8은 그대로 통과
    }
  }
  out += '"';
}

void numberTo(std::string& out, double n) {
  if (std::isfinite(n) && n == static_cast<double>(static_cast<long long>(n)) &&
      std::fabs(n) < 1e15) {
    out += std::to_string(static_cast<long long>(n));
    return;
  }
  char buf[40];
  std::snprintf(buf, sizeof(buf), "%.6g", n);
  out += buf;
}

}  // namespace

void Value::dumpTo(std::string& out, int indent, int depth) const {
  const bool pretty = indent >= 0;
  const std::string pad = pretty ? std::string(indent * (depth + 1), ' ') : std::string();
  const std::string padEnd = pretty ? std::string(indent * depth, ' ') : std::string();

  if (type_ == Type::Null) {
    out += "null";
  } else if (type_ == Type::Bool) {
    out += bool_ ? "true" : "false";
  } else if (type_ == Type::Number) {
    numberTo(out, num_);
  } else if (type_ == Type::String) {
    escapeTo(out, str_);
  } else if (type_ == Type::Array) {
    if (arr_.empty()) {
      out += "[]";
      return;
    }
    out += '[';
    for (std::size_t i = 0; i < arr_.size(); i++) {
      if (i > 0) out += ',';
      if (pretty) {
        out += '\n';
        out += pad;
      }
      arr_[i].dumpTo(out, indent, depth + 1);
    }
    if (pretty) {
      out += '\n';
      out += padEnd;
    }
    out += ']';
  } else {
    if (obj_.empty()) {
      out += "{}";
      return;
    }
    out += '{';
    bool first = true;
    for (Object::const_iterator it = obj_.begin(); it != obj_.end(); ++it) {
      if (!first) out += ',';
      first = false;
      if (pretty) {
        out += '\n';
        out += pad;
      }
      escapeTo(out, it->first);
      out += pretty ? ": " : ":";
      it->second.dumpTo(out, indent, depth + 1);
    }
    if (pretty) {
      out += '\n';
      out += padEnd;
    }
    out += '}';
  }
}

std::string Value::dump(int indent) const {
  std::string out;
  dumpTo(out, indent, 0);
  return out;
}

// ---------------------------------------------------------------- 파서

namespace {

class Parser {
 public:
  explicit Parser(const std::string& text) : s_(text) {}

  bool parse(Value* out, std::string* error) {
    // UTF-8 BOM(메모장 저장 파일)이 있으면 건너뛴다.
    if (s_.size() >= 3 && static_cast<unsigned char>(s_[0]) == 0xEF &&
        static_cast<unsigned char>(s_[1]) == 0xBB && static_cast<unsigned char>(s_[2]) == 0xBF) {
      i_ = 3;
    }
    skip();
    if (!parseValue(out)) {
      if (error) *error = err_.empty() ? "JSON 파싱 실패" : err_;
      return false;
    }
    skip();
    if (i_ != s_.size()) {
      if (error) *error = "JSON 뒤에 불필요한 문자가 있습니다 (offset " + std::to_string(i_) + ")";
      return false;
    }
    return true;
  }

 private:
  void skip() {
    while (i_ < s_.size()) {
      char c = s_[i_];
      if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
        i_++;
      } else if (c == '/' && i_ + 1 < s_.size() && s_[i_ + 1] == '/') {
        // 규칙 파일에 주석을 달 수 있도록 //-주석만 허용한다.
        while (i_ < s_.size() && s_[i_] != '\n') i_++;
      } else {
        break;
      }
    }
  }

  bool fail(const std::string& msg) {
    if (err_.empty()) err_ = msg + " (offset " + std::to_string(i_) + ")";
    return false;
  }

  bool parseValue(Value* out) {
    skip();
    if (i_ >= s_.size()) return fail("입력이 갑자기 끝났습니다");
    char c = s_[i_];
    if (c == '{') return parseObject(out);
    if (c == '[') return parseArray(out);
    if (c == '"') {
      std::string str;
      if (!parseString(&str)) return false;
      *out = Value(str);
      return true;
    }
    if (c == 't') {
      if (s_.compare(i_, 4, "true") == 0) {
        i_ += 4;
        *out = Value(true);
        return true;
      }
      return fail("알 수 없는 리터럴");
    }
    if (c == 'f') {
      if (s_.compare(i_, 5, "false") == 0) {
        i_ += 5;
        *out = Value(false);
        return true;
      }
      return fail("알 수 없는 리터럴");
    }
    if (c == 'n') {
      if (s_.compare(i_, 4, "null") == 0) {
        i_ += 4;
        *out = Value();
        return true;
      }
      return fail("알 수 없는 리터럴");
    }
    return parseNumber(out);
  }

  bool parseObject(Value* out) {
    i_++;  // '{'
    Object obj;
    skip();
    if (i_ < s_.size() && s_[i_] == '}') {
      i_++;
      *out = Value(obj);
      return true;
    }
    while (true) {
      skip();
      std::string key;
      if (!parseString(&key)) return false;
      skip();
      if (i_ >= s_.size() || s_[i_] != ':') return fail("':' 가 필요합니다");
      i_++;
      Value v;
      if (!parseValue(&v)) return false;
      obj[key] = v;
      skip();
      if (i_ >= s_.size()) return fail("객체가 닫히지 않았습니다");
      if (s_[i_] == ',') {
        i_++;
        continue;
      }
      if (s_[i_] == '}') {
        i_++;
        break;
      }
      return fail("',' 또는 '}' 가 필요합니다");
    }
    *out = Value(obj);
    return true;
  }

  bool parseArray(Value* out) {
    i_++;  // '['
    Array arr;
    skip();
    if (i_ < s_.size() && s_[i_] == ']') {
      i_++;
      *out = Value(arr);
      return true;
    }
    while (true) {
      Value v;
      if (!parseValue(&v)) return false;
      arr.push_back(v);
      skip();
      if (i_ >= s_.size()) return fail("배열이 닫히지 않았습니다");
      if (s_[i_] == ',') {
        i_++;
        continue;
      }
      if (s_[i_] == ']') {
        i_++;
        break;
      }
      return fail("',' 또는 ']' 가 필요합니다");
    }
    *out = Value(arr);
    return true;
  }

  void appendUtf8(std::string& out, unsigned int cp) {
    if (cp < 0x80) {
      out += static_cast<char>(cp);
    } else if (cp < 0x800) {
      out += static_cast<char>(0xC0 | (cp >> 6));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
      out += static_cast<char>(0xE0 | (cp >> 12));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    } else {
      out += static_cast<char>(0xF0 | (cp >> 18));
      out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
      out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
      out += static_cast<char>(0x80 | (cp & 0x3F));
    }
  }

  bool parseHex4(unsigned int* out) {
    if (i_ + 4 > s_.size()) return fail("\\u 이스케이프가 짧습니다");
    unsigned int v = 0;
    for (int k = 0; k < 4; k++) {
      char c = s_[i_ + k];
      v <<= 4;
      if (c >= '0' && c <= '9') {
        v |= static_cast<unsigned>(c - '0');
      } else if (c >= 'a' && c <= 'f') {
        v |= static_cast<unsigned>(c - 'a' + 10);
      } else if (c >= 'A' && c <= 'F') {
        v |= static_cast<unsigned>(c - 'A' + 10);
      } else {
        return fail("16진수가 아닙니다");
      }
    }
    i_ += 4;
    *out = v;
    return true;
  }

  bool parseString(std::string* out) {
    if (i_ >= s_.size() || s_[i_] != '"') return fail("문자열이 아닙니다");
    i_++;
    std::string res;
    while (true) {
      if (i_ >= s_.size()) return fail("문자열이 닫히지 않았습니다");
      char c = s_[i_];
      i_++;
      if (c == '"') break;
      if (c != '\\') {
        res += c;
        continue;
      }
      if (i_ >= s_.size()) return fail("이스케이프가 끝났습니다");
      char e = s_[i_];
      i_++;
      if (e == '"') {
        res += '"';
      } else if (e == '\\') {
        res += '\\';
      } else if (e == '/') {
        res += '/';
      } else if (e == 'b') {
        res += '\b';
      } else if (e == 'f') {
        res += '\f';
      } else if (e == 'n') {
        res += '\n';
      } else if (e == 'r') {
        res += '\r';
      } else if (e == 't') {
        res += '\t';
      } else if (e == 'u') {
        unsigned int cp = 0;
        if (!parseHex4(&cp)) return false;
        if (cp >= 0xD800 && cp <= 0xDBFF && i_ + 1 < s_.size() && s_[i_] == '\\' &&
            s_[i_ + 1] == 'u') {
          i_ += 2;
          unsigned int lo = 0;
          if (!parseHex4(&lo)) return false;
          cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
        }
        appendUtf8(res, cp);
      } else {
        return fail("알 수 없는 이스케이프");
      }
    }
    *out = res;
    return true;
  }

  bool isDigitAt(std::size_t k) const {
    return k < s_.size() && std::isdigit(static_cast<unsigned char>(s_[k])) != 0;
  }

  bool parseNumber(Value* out) {
    std::size_t start = i_;
    if (i_ < s_.size() && (s_[i_] == '-' || s_[i_] == '+')) i_++;
    bool digits = false;
    while (isDigitAt(i_)) {
      i_++;
      digits = true;
    }
    if (i_ < s_.size() && s_[i_] == '.') {
      i_++;
      while (isDigitAt(i_)) {
        i_++;
        digits = true;
      }
    }
    if (digits && i_ < s_.size() && (s_[i_] == 'e' || s_[i_] == 'E')) {
      i_++;
      if (i_ < s_.size() && (s_[i_] == '-' || s_[i_] == '+')) i_++;
      while (isDigitAt(i_)) i_++;
    }
    if (!digits) return fail("숫자가 아닙니다");
    *out = Value(std::strtod(s_.substr(start, i_ - start).c_str(), nullptr));
    return true;
  }

  const std::string& s_;
  std::size_t i_ = 0;
  std::string err_;
};

}  // namespace

Value Value::parse(const std::string& text, std::string* error) {
  Value v;
  Parser p(text);
  if (!p.parse(&v, error)) return Value();
  return v;
}

Value Value::parseFile(const std::string& path, std::string* error) {
  std::ifstream in(path.c_str(), std::ios::binary);
  if (!in) {
    if (error) *error = "파일을 열 수 없습니다: " + path;
    return Value();
  }
  std::ostringstream ss;
  ss << in.rdbuf();
  return parse(ss.str(), error);
}

bool Value::writeFile(const std::string& path, const Value& v, std::string* error) {
  std::ofstream out(path.c_str(), std::ios::binary);
  if (!out) {
    if (error) *error = "파일을 쓸 수 없습니다: " + path;
    return false;
  }
  out << v.dump(2) << "\n";
  return true;
}

// ---------------------------------------------------------------- 시간

namespace {
long long daysFromCivil(long long y, unsigned m, unsigned d) {
  if (m <= 2) y -= 1;
  const long long era = (y >= 0 ? y : y - 399) / 400;
  const unsigned yoe = static_cast<unsigned>(y - era * 400);
  const unsigned doy = (153 * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1;
  const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
  return era * 146097 + static_cast<long long>(doe) - 719468;
}

void civilFromDays(long long z, int* y, unsigned* m, unsigned* d) {
  z += 719468;
  const long long era = (z >= 0 ? z : z - 146096) / 146097;
  const unsigned doe = static_cast<unsigned>(z - era * 146097);
  const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
  const long long yr = static_cast<long long>(yoe) + era * 400;
  const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
  const unsigned mp = (5 * doy + 2) / 153;
  *d = doy - (153 * mp + 2) / 5 + 1;
  *m = mp < 10 ? mp + 3 : mp - 9;
  *y = static_cast<int>(yr + (*m <= 2 ? 1 : 0));
}
}  // namespace

bool parseIso8601(const std::string& t, long long* epochSeconds, int* offsetMinutes) {
  if (t.size() < 10) return false;
  int year = 0, mon = 0, day = 0, hour = 0, min = 0, sec = 0;
  if (std::sscanf(t.c_str(), "%4d-%2d-%2d", &year, &mon, &day) != 3) return false;
  if (mon < 1 || mon > 12 || day < 1 || day > 31) return false;
  if (t.size() >= 19) std::sscanf(t.c_str() + 11, "%2d:%2d:%2d", &hour, &min, &sec);
  long long secs = daysFromCivil(year, static_cast<unsigned>(mon), static_cast<unsigned>(day)) *
                       86400LL +
                   hour * 3600LL + min * 60LL + sec;
  // 표준시 오프셋 처리: 'Z' 또는 +09:00 / -05:00
  std::size_t pos = t.find_first_of("Zz+", 10);
  if (pos == std::string::npos && t.size() > 19) {
    std::size_t minus = t.rfind('-');
    if (minus != std::string::npos && minus > 10) pos = minus;
  }
  if (offsetMinutes) *offsetMinutes = 0;
  if (pos != std::string::npos && (t[pos] == '+' || t[pos] == '-')) {
    int oh = 0, om = 0;
    std::sscanf(t.c_str() + pos + 1, "%2d:%2d", &oh, &om);
    const long long off = oh * 3600LL + om * 60LL;
    if (t[pos] == '+') {
      secs -= off;
    } else {
      secs += off;
    }
    if (offsetMinutes) *offsetMinutes = static_cast<int>((t[pos] == '+' ? off : -off) / 60);
  }
  *epochSeconds = secs;
  return true;
}

std::string formatIso8601(long long epochSeconds, int offsetMinutes) {
  const long long shifted = epochSeconds + offsetMinutes * 60LL;
  long long days = shifted / 86400;
  long long rem = shifted % 86400;
  if (rem < 0) {
    rem += 86400;
    days -= 1;
  }
  int y = 0;
  unsigned m = 0, d = 0;
  civilFromDays(days, &y, &m, &d);
  char buf[80];
  if (offsetMinutes == 0) {
    std::snprintf(buf, sizeof(buf), "%04d-%02u-%02uT%02lld:%02lld:%02lldZ", y, m, d, rem / 3600,
                  (rem % 3600) / 60, rem % 60);
  } else {
    const int absOff = offsetMinutes < 0 ? -offsetMinutes : offsetMinutes;
    std::snprintf(buf, sizeof(buf), "%04d-%02u-%02uT%02lld:%02lld:%02lld%c%02d:%02d", y, m, d,
                  rem / 3600, (rem % 3600) / 60, rem % 60, offsetMinutes < 0 ? '-' : '+',
                  absOff / 60, absOff % 60);
  }
  return buf;
}

}  // namespace json
}  // namespace ce
