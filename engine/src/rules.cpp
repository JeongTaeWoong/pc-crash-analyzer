// rules.cpp - data/rules.json(가중치·점검·조치·해결책·사례 DB)을 읽고 쓰는 코드.
//   [팀원용] 엔진의 모든 숫자는 rules.json 에 있다. 코드를 고치지 않고 값만 바꿔 실험할 수 있게 하려는 것.
//   rules.json 을 고친 뒤에는 python tools/gen_rules_default.py 로 내장본(rules_default.cpp)도 다시 만들어야
//   --rules 옵션 없이 실행할 때도 같은 값이 쓰인다.

#include "ce/rules.hpp"

#include <cctype>

namespace ce {

namespace internal {
extern const char* const kDefaultRulesParts[];  // rules_default.cpp (자동 생성)
}

namespace {

bool listHas(const std::vector<std::string>& list, const std::string& x) {
  for (std::size_t i = 0; i < list.size(); i++) {
    if (list[i] == x) return true;
  }
  return false;
}

json::Value stringListToJson(const std::vector<std::string>& list) {
  json::Value a = json::Value::makeArray();
  for (std::size_t i = 0; i < list.size(); i++) a.push(json::Value(list[i]));
  return a;
}

}  // namespace

// ---------------------------------------------------------------- 이름 정규화

std::string normalizeName(const std::string& s) {
  std::string t = toLower(s);
  const char* exts[] = {".sys", ".dll", ".exe"};
  for (int k = 0; k < 3; k++) {
    const std::string ext = exts[k];
    if (t.size() > ext.size() && t.compare(t.size() - ext.size(), ext.size(), ext) == 0) {
      t.erase(t.size() - ext.size());
    }
  }
  std::string out;
  for (std::size_t i = 0; i < t.size(); i++) {
    unsigned char c = static_cast<unsigned char>(t[i]);
    if (c >= 0x80 || std::isalnum(c)) out += t[i];  // 한글 등 비ASCII 문자는 남긴다
  }
  return out;
}

bool sameSubject(const std::string& a, const std::string& b) {
  const std::string x = normalizeName(a);
  const std::string y = normalizeName(b);
  if (x.empty() || y.empty()) return false;
  if (x.size() < 3 || y.size() < 3) return x == y;
  return x.find(y) != std::string::npos || y.find(x) != std::string::npos;
}

Layer layerOfCategory(const std::string& category) {
  const char* sw[] = {"anticheat", "antivirus", "fin_security", "security_firewall",
                      "overlay",   "input_util", "other_sw"};
  const char* drv[] = {"gpu_driver",     "display_util", "peripheral_driver",
                       "network_driver", "os_update",    "other_driver"};
  const char* hw[] = {"thermal", "power", "connection", "storage", "memory"};
  for (int i = 0; i < 7; i++) {
    if (category == sw[i]) return Layer::Software;
  }
  for (int i = 0; i < 6; i++) {
    if (category == drv[i]) return Layer::Driver;
  }
  for (int i = 0; i < 5; i++) {
    if (category == hw[i]) return Layer::Hardware;
  }
  return Layer::Unknown;
}

// ---------------------------------------------------------------- 템플릿

bool CheckTemplate::appliesTo(const std::string& category, Layer layer) const {
  if (listHas(categories, category)) return true;
  if (listHas(layers, toString(layer))) return true;
  return false;
}

bool RemedyTemplate::appliesTo(const std::string& category, const std::string& subject,
                               const std::string& symptom) const {
  if (!listHas(categories, category)) return false;
  if (!symptoms.empty() && !listHas(symptoms, symptom)) return false;
  if (subjects.empty()) return true;
  for (std::size_t i = 0; i < subjects.size(); i++) {
    if (sameSubject(subjects[i], subject)) return true;
  }
  return false;
}

// ---------------------------------------------------------------- 조회

double Rules::layerWeight(const std::string& name, double def) const {
  std::map<std::string, double>::const_iterator it = layer_weights.find(name);
  if (it == layer_weights.end()) return def;
  return it->second;
}

double Rules::prior(const std::string& symptom, const std::string& category) const {
  std::map<std::string, std::map<std::string, double> >::const_iterator s = priors.find(symptom);
  if (s == priors.end()) return 0.05;
  std::map<std::string, double>::const_iterator c = s->second.find(category);
  if (c == s->second.end()) return 0.03;  // 표에 없는 카테고리도 완전히 배제하지는 않는다
  return c->second;
}

int Rules::findCaseIndex(const std::string& symptom, const std::string& category,
                         const std::string& subject) const {
  int generic = -1;
  for (std::size_t i = 0; i < cases.size(); i++) {
    const CaseEntry& c = cases[i];
    if (c.symptom != symptom || c.category != category) continue;
    if (!c.subject.empty()) {
      if (equalsNoCase(c.subject, subject)) return static_cast<int>(i);  // 구체 일치 우선
    } else if (generic < 0) {
      generic = static_cast<int>(i);
    }
  }
  return generic;
}

const CheckTemplate* Rules::findCheck(const std::string& id) const {
  for (std::size_t i = 0; i < checks.size(); i++) {
    if (checks[i].id == id) return &checks[i];
  }
  return nullptr;
}

const ActionTemplate* Rules::findAction(const std::string& id) const {
  for (std::size_t i = 0; i < actions.size(); i++) {
    if (actions[i].id == id) return &actions[i];
  }
  return nullptr;
}

bool Rules::inFocus(const std::string& category) const {
  if (focus_categories.empty()) return true;  // 범위를 정하지 않았으면 전부 범위 안
  return listHas(focus_categories, category);
}

void Rules::classifyName(const std::string& haystack, const std::string& display,
                         const std::string& fallback, std::string* category,
                         std::string* label) const {
  const std::string n = toLower(haystack);
  std::size_t bestLen = 0;
  for (std::size_t i = 0; i < hints.size(); i++) {
    const std::string m = toLower(hints[i].match);
    if (m.empty() || n.find(m) == std::string::npos) continue;
    if (m.size() > bestLen) {  // 더 길게 일치하는 규칙이 이긴다
      bestLen = m.size();
      *category = hints[i].category;
      *label = hints[i].label;
    }
  }
  if (bestLen == 0) {
    *category = fallback;
    *label = display;
    return;
  }
  if (label->empty()) {
    *label = display;
  } else if (toLower(*label).find(toLower(display)) == std::string::npos) {
    *label += " (" + display + ")";  // 표시명이 규칙 이름에 없으면 덧붙인다
  }
}

// ---------------------------------------------------------------- JSON 입출력

Rules Rules::fromJson(const json::Value& v) {
  Rules r;
  r.version = v["version"].asInt(2);

  const json::Value& lw = v["layer_weights"];
  std::vector<std::string> lwKeys = lw.keys();
  for (std::size_t i = 0; i < lwKeys.size(); i++) {
    r.layer_weights[lwKeys[i]] = lw[lwKeys[i]].asNumber(0);
  }

  const json::Value& c = v["correlation"];
  r.w_prior = c["w_prior"].asNumber(r.w_prior);
  r.w_time = c["w_time"].asNumber(r.w_time);
  r.w_coexist = c["w_coexist"].asNumber(r.w_coexist);
  r.w_case = c["w_case"].asNumber(r.w_case);
  r.w_layer = c["w_layer"].asNumber(r.w_layer);
  r.time_window_minutes = c["time_window_minutes"].asNumber(r.time_window_minutes);
  r.time_full_credit_minutes = c["time_full_credit_minutes"].asNumber(r.time_full_credit_minutes);

  const json::Value& focus = v["focus"];
  r.focus_name = focus["name"].asString("");
  r.focus_categories = focus["categories"].asStringList();

  const json::Value& ses = v["session"];
  r.skip_layer_threshold = ses["skip_layer_threshold"].asNumber(r.skip_layer_threshold);
  r.confirm_threshold = ses["confirm_threshold"].asNumber(r.confirm_threshold);
  r.verify_min_observe_minutes =
      ses["verify_min_observe_minutes"].asNumber(r.verify_min_observe_minutes);

  const json::Value& pr = v["priors"];
  std::vector<std::string> symKeys = pr.keys();
  for (std::size_t i = 0; i < symKeys.size(); i++) {
    const json::Value& row = pr[symKeys[i]];
    std::vector<std::string> catKeys = row.keys();
    for (std::size_t k = 0; k < catKeys.size(); k++) {
      r.priors[symKeys[i]][catKeys[k]] = row[catKeys[k]].asNumber(0);
    }
  }

  const json::Array& hs = v["hints"].asArray();
  for (std::size_t i = 0; i < hs.size(); i++) {
    CategoryHint hint;
    hint.match = hs[i]["match"].asString("");
    hint.category = hs[i]["category"].asString("other_sw");
    hint.label = hs[i]["label"].asString("");
    if (!hint.match.empty()) r.hints.push_back(hint);
  }

  const json::Array& as = v["actions"].asArray();
  for (std::size_t i = 0; i < as.size(); i++) {
    const json::Value& a = as[i];
    ActionTemplate t;
    t.id = a["id"].asString("");
    t.label = a["label"].asString(t.id);
    t.kind = a["kind"].asString("inspect");
    t.steps = a["steps"].asStringList();
    t.rollback = a["rollback"].asString("");
    t.reversible = a["reversible"].asBool(false);
    t.requires_reboot = a["requires_reboot"].asBool(false);
    t.risk = a["risk"].asString("low");
    t.automation = a["automation"].asString("");
    t.auto_default = a["auto_default"].asBool(false);
    if (!t.id.empty()) r.actions.push_back(t);
  }

  const json::Array& ks = v["checks"].asArray();
  for (std::size_t i = 0; i < ks.size(); i++) {
    const json::Value& k = ks[i];
    CheckTemplate t;
    t.id = k["id"].asString("");
    t.label = k["label"].asString(t.id);
    t.advice = k["advice"].asString("");
    t.action = k["action"].asString("");
    t.cost_seconds = k["cost_seconds"].asNumber(60);
    t.reliability = k["reliability"].asNumber(0.85);
    t.group = k["group"].asBool(false);
    t.categories = k["categories"].asStringList();
    t.layers = k["layers"].asStringList();
    if (!t.id.empty()) r.checks.push_back(t);
  }

  const json::Array& rs = v["remedies"].asArray();
  for (std::size_t i = 0; i < rs.size(); i++) {
    const json::Value& k = rs[i];
    RemedyTemplate t;
    t.id = k["id"].asString("");
    t.label = k["label"].asString(t.id);
    t.description = k["description"].asString("");
    t.categories = k["categories"].asStringList();
    t.subjects = k["subjects"].asStringList();
    t.symptoms = k["symptoms"].asStringList();
    t.actions = k["actions"].asStringList();
    t.success_seed = k["success_seed"].asNumber(0.6);
    if (!t.id.empty()) r.remedies.push_back(t);
  }

  r.baseline_checklist = v["baseline_checklist"].asStringList();

  const json::Array& cs = v["cases"].asArray();
  for (std::size_t i = 0; i < cs.size(); i++) {
    CaseEntry e;
    e.symptom = cs[i]["symptom"].asString("");
    e.category = cs[i]["category"].asString("");
    e.subject = cs[i]["subject"].asString("");
    e.weight = cs[i]["weight"].asNumber(0.5);
    e.solved = cs[i]["solved"].asInt(0);
    e.rejected = cs[i]["rejected"].asInt(0);
    if (!e.symptom.empty() && !e.category.empty()) r.cases.push_back(e);
  }
  return r;
}

json::Value Rules::toJson() const {
  json::Value out = json::Value::makeObject();
  out.set("version", json::Value(version));

  json::Value lw = json::Value::makeObject();
  for (std::map<std::string, double>::const_iterator it = layer_weights.begin();
       it != layer_weights.end(); ++it) {
    lw.set(it->first, json::Value(it->second));
  }
  out.set("layer_weights", lw);

  json::Value corr = json::Value::makeObject();
  corr.set("w_prior", json::Value(w_prior));
  corr.set("w_time", json::Value(w_time));
  corr.set("w_coexist", json::Value(w_coexist));
  corr.set("w_case", json::Value(w_case));
  corr.set("w_layer", json::Value(w_layer));
  corr.set("time_window_minutes", json::Value(time_window_minutes));
  corr.set("time_full_credit_minutes", json::Value(time_full_credit_minutes));
  out.set("correlation", corr);

  json::Value focus = json::Value::makeObject();
  focus.set("name", json::Value(focus_name));
  focus.set("categories", stringListToJson(focus_categories));
  out.set("focus", focus);

  json::Value ses = json::Value::makeObject();
  ses.set("skip_layer_threshold", json::Value(skip_layer_threshold));
  ses.set("confirm_threshold", json::Value(confirm_threshold));
  ses.set("verify_min_observe_minutes", json::Value(verify_min_observe_minutes));
  out.set("session", ses);

  json::Value pr = json::Value::makeObject();
  for (std::map<std::string, std::map<std::string, double> >::const_iterator s = priors.begin();
       s != priors.end(); ++s) {
    json::Value row = json::Value::makeObject();
    for (std::map<std::string, double>::const_iterator c = s->second.begin(); c != s->second.end();
         ++c) {
      row.set(c->first, json::Value(c->second));
    }
    pr.set(s->first, row);
  }
  out.set("priors", pr);

  json::Value hs = json::Value::makeArray();
  for (std::size_t i = 0; i < hints.size(); i++) {
    json::Value o = json::Value::makeObject();
    o.set("match", json::Value(hints[i].match));
    o.set("category", json::Value(hints[i].category));
    o.set("label", json::Value(hints[i].label));
    hs.push(o);
  }
  out.set("hints", hs);

  json::Value as = json::Value::makeArray();
  for (std::size_t i = 0; i < actions.size(); i++) {
    const ActionTemplate& t = actions[i];
    json::Value o = json::Value::makeObject();
    o.set("id", json::Value(t.id));
    o.set("label", json::Value(t.label));
    o.set("kind", json::Value(t.kind));
    o.set("steps", stringListToJson(t.steps));
    o.set("rollback", json::Value(t.rollback));
    o.set("reversible", json::Value(t.reversible));
    o.set("requires_reboot", json::Value(t.requires_reboot));
    o.set("risk", json::Value(t.risk));
    o.set("automation", json::Value(t.automation));
    o.set("auto_default", json::Value(t.auto_default));
    as.push(o);
  }
  out.set("actions", as);

  json::Value cs = json::Value::makeArray();
  for (std::size_t i = 0; i < checks.size(); i++) {
    const CheckTemplate& t = checks[i];
    json::Value o = json::Value::makeObject();
    o.set("id", json::Value(t.id));
    o.set("label", json::Value(t.label));
    o.set("advice", json::Value(t.advice));
    o.set("action", json::Value(t.action));
    o.set("cost_seconds", json::Value(t.cost_seconds));
    o.set("reliability", json::Value(t.reliability));
    if (t.group) o.set("group", json::Value(true));
    if (!t.categories.empty()) o.set("categories", stringListToJson(t.categories));
    if (!t.layers.empty()) o.set("layers", stringListToJson(t.layers));
    cs.push(o);
  }
  out.set("checks", cs);

  json::Value rs = json::Value::makeArray();
  for (std::size_t i = 0; i < remedies.size(); i++) {
    const RemedyTemplate& t = remedies[i];
    json::Value o = json::Value::makeObject();
    o.set("id", json::Value(t.id));
    o.set("label", json::Value(t.label));
    o.set("description", json::Value(t.description));
    o.set("categories", stringListToJson(t.categories));
    if (!t.subjects.empty()) o.set("subjects", stringListToJson(t.subjects));
    if (!t.symptoms.empty()) o.set("symptoms", stringListToJson(t.symptoms));
    o.set("actions", stringListToJson(t.actions));
    o.set("success_seed", json::Value(t.success_seed));
    rs.push(o);
  }
  out.set("remedies", rs);

  out.set("baseline_checklist", stringListToJson(baseline_checklist));

  json::Value ce = json::Value::makeArray();
  for (std::size_t i = 0; i < cases.size(); i++) {
    json::Value o = json::Value::makeObject();
    o.set("symptom", json::Value(cases[i].symptom));
    o.set("category", json::Value(cases[i].category));
    o.set("subject", json::Value(cases[i].subject));
    o.set("weight", json::Value(cases[i].weight));
    o.set("solved", json::Value(cases[i].solved));
    o.set("rejected", json::Value(cases[i].rejected));
    ce.push(o);
  }
  out.set("cases", ce);
  return out;
}

Rules Rules::defaults() {
  std::string text;
  for (int i = 0; internal::kDefaultRulesParts[i] != nullptr; i++) {
    text += internal::kDefaultRulesParts[i];
  }
  std::string err;
  json::Value v = json::Value::parse(text, &err);
  return fromJson(v);
}

bool Rules::load(const std::string& path, Rules* out, std::string* error) {
  std::string err;
  json::Value v = json::Value::parseFile(path, &err);
  if (!err.empty() || !v.isObject()) {
    if (error) *error = err.empty() ? "규칙 파일이 JSON 객체가 아닙니다: " + path : err;
    return false;
  }
  *out = fromJson(v);
  return true;
}

bool Rules::save(const std::string& path, std::string* error) const {
  return json::Value::writeFile(path, toJson(), error);
}

}  // namespace ce
