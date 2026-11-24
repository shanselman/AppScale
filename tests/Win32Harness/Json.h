// Minimal JSON object builder (UTF-8) for JSON Lines logs and self-test results.
#pragma once

#include <windows.h>

#include <cstdint>
#include <string>
#include <string_view>

namespace json {

inline std::string Utf8(std::wstring_view w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

inline std::string Escape(std::string_view s) {
    std::string o;
    o.reserve(s.size() + 2);
    o += '"';
    for (char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) {
                    char b[8];
                    sprintf_s(b, "\\u%04x", c);
                    o += b;
                } else {
                    o += c;
                }
        }
    }
    o += '"';
    return o;
}

inline std::string Hex(uint64_t v) {
    char b[24];
    sprintf_s(b, "\"0x%llX\"", static_cast<unsigned long long>(v));
    return b;
}

// Builds either an object ({...}) or an array ([...]).
class Builder {
public:
    explicit Builder(bool array = false) : array_(array) {}

    Builder& Raw(std::string_view key, std::string_view rawJson) {
        Sep();
        if (!array_) {
            body_ += Escape(key);
            body_ += ':';
        }
        body_ += rawJson;
        return *this;
    }
    Builder& Add(std::string_view key, const std::string& v) { return Raw(key, Escape(v)); }
    Builder& Add(std::string_view key, const char* v) { return Raw(key, Escape(v)); }
    Builder& Add(std::string_view key, std::wstring_view v) { return Raw(key, Escape(Utf8(v))); }
    Builder& Add(std::string_view key, const wchar_t* v) { return Raw(key, Escape(Utf8(v ? v : L""))); }
    Builder& Add(std::string_view key, bool v) { return Raw(key, v ? "true" : "false"); }
    Builder& Add(std::string_view key, int v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, long v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, unsigned v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, unsigned long v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, long long v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, unsigned long long v) { return Raw(key, std::to_string(v)); }
    Builder& Add(std::string_view key, double v) {
        char b[48];
        sprintf_s(b, "%.3f", v);
        return Raw(key, b);
    }
    Builder& Add(std::string_view key, const RECT& r) {
        char b[96];
        sprintf_s(b, "[%ld,%ld,%ld,%ld]", r.left, r.top, r.right, r.bottom);
        return Raw(key, b);
    }
    Builder& Add(std::string_view key, const Builder& nested) { return Raw(key, nested.Str()); }
    Builder& Null(std::string_view key) { return Raw(key, "null"); }
    // Appends all members of another object builder into this one.
    Builder& Merge(const Builder& other) {
        if (other.body_.empty()) return *this;
        Sep();
        body_ += other.body_;
        return *this;
    }

    // Array helpers (key ignored).
    template <typename T>
    Builder& Push(const T& v) { return Add("", v); }

    std::string Str() const { return (array_ ? "[" : "{") + body_ + (array_ ? "]" : "}"); }
    bool Empty() const { return body_.empty(); }

private:
    void Sep() {
        if (!body_.empty()) body_ += ',';
    }
    bool array_;
    std::string body_;
};

}  // namespace json
