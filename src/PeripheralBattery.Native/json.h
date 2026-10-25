#pragma once
#include <string>
#include <map>
#include <vector>
#include <stdexcept>
#include <cmath>
#include <cstring>

// Small bounded JSON document reader for our own worker/config files. No code execution.
struct Json {
    enum Type { Null, Bool, Number, String, Object, Array } type = Null;
    bool boolean = false; double number = 0;
    std::string string; std::map<std::string, Json> object; std::vector<Json> array;
    Json() = default;
    Json(const char* s) : type(String), string(s) {}
    Json(const std::string& s) : type(String), string(s) {}
    Json(bool b) : type(Bool), boolean(b) {}
    Json(int n) : type(Number), number(n) {}
    static Json obj() { Json j; j.type = Object; return j; }
    static Json arr() { Json j; j.type = Array; return j; }
    Json& operator[](const std::string& key) { type = Object; return object[key]; }
    const Json& at(const std::string& key) const { static const Json nil; auto i = object.find(key); return i == object.end() ? nil : i->second; }
    std::string text(std::string fallback = "") const { return type == String ? string : fallback; }
    int integer(int fallback = 0) const { return type == Number && number >= -2147483648. && number <= 2147483647. ? (int)number : fallback; }
    bool flag(bool fallback = false) const { return type == Bool ? boolean : fallback; }
    static void utf8(std::string& s, unsigned cp) {
        if (cp <= 0x7f) s += (char)cp;
        else if (cp <= 0x7ff) { s += (char)(0xc0 | (cp >> 6)); s += (char)(0x80 | (cp & 63)); }
        else if (cp <= 0xffff) { s += (char)(0xe0 | (cp >> 12)); s += (char)(0x80 | ((cp >> 6) & 63)); s += (char)(0x80 | (cp & 63)); }
        else if (cp <= 0x10ffff) { s += (char)(0xf0 | (cp >> 18)); s += (char)(0x80 | ((cp >> 12) & 63)); s += (char)(0x80 | ((cp >> 6) & 63)); s += (char)(0x80 | (cp & 63)); }
        else throw std::runtime_error("Invalid Unicode");
    }
    struct Parser {
        const std::string& s; size_t p = 0;
        void ws() { while (p < s.size() && (s[p] == ' ' || s[p] == '\r' || s[p] == '\n' || s[p] == '\t')) ++p; }
        char take() { if (p >= s.size()) throw std::runtime_error("Truncated JSON"); return s[p++]; }
        unsigned hex4() { unsigned n = 0; for (int i = 0; i < 4; ++i) { char c = take(); int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; if (d < 0) throw std::runtime_error("Invalid escape"); n = n * 16 + d; } return n; }
        std::string str() {
            if (take() != '"') throw std::runtime_error("Expected string"); std::string out;
            for (;;) {
                unsigned char c = (unsigned char)take(); if (c == '"') return out;
                if (c < 32) throw std::runtime_error("Invalid string");
                if (c != '\\') { out += (char)c; continue; }
                switch (take()) {
                case '"': out += '"'; break; case '\\': out += '\\'; break; case '/': out += '/'; break;
                case 'b': out += '\b'; break; case 'f': out += '\f'; break; case 'n': out += '\n'; break; case 'r': out += '\r'; break; case 't': out += '\t'; break;
                case 'u': { unsigned cp = hex4(); if (cp >= 0xd800 && cp <= 0xdbff) { if (take() != '\\' || take() != 'u') throw std::runtime_error("Invalid surrogate"); unsigned lo = hex4(); if (lo < 0xdc00 || lo > 0xdfff) throw std::runtime_error("Invalid surrogate"); cp = 0x10000 + ((cp - 0xd800) << 10) + lo - 0xdc00; } else if (cp >= 0xdc00 && cp <= 0xdfff) throw std::runtime_error("Invalid surrogate"); utf8(out, cp); break; }
                default: throw std::runtime_error("Invalid escape");
                }
            }
        }
        Json value(int depth = 0) {
            if (depth > 64) throw std::runtime_error("JSON nesting limit"); ws(); if (p >= s.size()) throw std::runtime_error("Empty JSON");
            if (s[p] == '"') return Json(str());
            if (s[p] == '{' || s[p] == '[') {
                bool obj = take() == '{'; Json j = obj ? Json::obj() : Json::arr(); char end = obj ? '}' : ']'; ws();
                if (p < s.size() && s[p] == end) { ++p; return j; }
                for (;;) {
                    if (obj) { ws(); auto key = str(); ws(); if (take() != ':') throw std::runtime_error("Expected colon"); j.object[key] = value(depth + 1); }
                    else j.array.push_back(value(depth + 1));
                    ws(); char c = take(); if (c == end) return j; if (c != ',') throw std::runtime_error("Expected comma");
                }
            }
            for (auto literal : { "true", "false", "null" }) { size_t n = strlen(literal); if (s.compare(p, n, literal) == 0) { p += n; return n == 4 && literal[0] == 't' ? Json(true) : literal[0] == 'f' ? Json(false) : Json(); } }
            size_t start = p; if (s[p] == '-') ++p;
            if (p >= s.size() || s[p] < '0' || s[p] > '9') throw std::runtime_error("Invalid number");
            if (s[p] == '0') ++p; else while (p < s.size() && s[p] >= '0' && s[p] <= '9') ++p;
            if (p < s.size() && s[p] == '.') { ++p; size_t q = p; while (p < s.size() && s[p] >= '0' && s[p] <= '9') ++p; if (p == q) throw std::runtime_error("Invalid fraction"); }
            if (p < s.size() && (s[p] == 'e' || s[p] == 'E')) { ++p; if (p < s.size() && (s[p] == '-' || s[p] == '+')) ++p; size_t q = p; while (p < s.size() && s[p] >= '0' && s[p] <= '9') ++p; if (p == q) throw std::runtime_error("Invalid exponent"); }
            Json j; j.type = Number; j.number = std::stod(s.substr(start, p - start)); if (!std::isfinite(j.number)) throw std::runtime_error("Invalid number"); return j;
        }
    };
    static Json parse(const std::string& s) { if (s.size() > 4 * 1024 * 1024) throw std::runtime_error("JSON size limit"); Parser p{ s }; if (s.compare(0, 3, "\xef\xbb\xbf") == 0) p.p = 3; auto j = p.value(); p.ws(); if (p.p != s.size()) throw std::runtime_error("Trailing JSON"); return j; }
    static std::string quote(const std::string& s) {
        std::string out = "\""; const char* h = "0123456789abcdef";
        for (unsigned char c : s) { if (c == '"' || c == '\\') { out += '\\'; out += (char)c; } else if (c < 32) { out += "\\u00"; out += h[c >> 4]; out += h[c & 15]; } else out += (char)c; }
        return out + '"';
    }
    std::string dump() const {
        switch (type) {
        case Bool: return boolean ? "true" : "false"; case Number: return std::to_string((long long)number); case String: return quote(string);
        case Object: { std::string s = "{"; for (auto& kv : object) { if (s.size() > 1) s += ','; s += quote(kv.first) + ':' + kv.second.dump(); } return s + '}'; }
        case Array: { std::string s = "["; for (auto& v : array) { if (s.size() > 1) s += ','; s += v.dump(); } return s + ']'; }
        default: return "null";
        }
    }
};
