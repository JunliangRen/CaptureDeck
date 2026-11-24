#include "capdvhs/util.hpp"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <cstdio>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>

namespace capdvhs {
bool trace_enabled() noexcept {
    static const bool enabled = [] { wchar_t value[16]{}; return GetEnvironmentVariableW(L"CAPTUREDECK_FIREWIRE_TRACE", value, 16) > 0 && value[0] != L'0'; }();
    return enabled;
}
void trace_event(std::string_view message) noexcept {
    if (!trace_enabled()) return;
    try { static std::mutex mutex; std::lock_guard lock(mutex); std::cerr << "[trace " << GetTickCount64() << "] " << message << '\n'; }
    catch (...) {}
}
std::string json_quote(std::string_view value) {
    std::string result = "\"";
    constexpr char hex[] = "0123456789abcdef";
    for (unsigned char ch : value) {
        switch (ch) {
        case '"': result += "\\\""; break;
        case '\\': result += "\\\\"; break;
        case '\b': result += "\\b"; break;
        case '\f': result += "\\f"; break;
        case '\n': result += "\\n"; break;
        case '\r': result += "\\r"; break;
        case '\t': result += "\\t"; break;
        default:
            if (ch < 0x20) {
                result += "\\u00";
                result += hex[ch >> 4]; result += hex[ch & 15];
            } else result += static_cast<char>(ch);
        }
    }
    return result + '"';
}

std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    if (value.size() > INT_MAX) throw std::length_error("UTF-16 string too long");
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
                                    static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (!length) throw std::runtime_error("Invalid UTF-16 string");
    std::string result(static_cast<std::size_t>(length), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
                             result.data(), length, nullptr, nullptr)) throw std::runtime_error("UTF-16 conversion failed");
    return result;
}

std::wstring wide(std::string_view value) {
    if (value.empty()) return {};
    if (value.size() > INT_MAX) throw std::length_error("UTF-8 string too long");
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                                    static_cast<int>(value.size()), nullptr, 0);
    if (!length) throw std::runtime_error("Invalid UTF-8 string");
    std::wstring result(static_cast<std::size_t>(length), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
                             result.data(), length)) throw std::runtime_error("UTF-8 conversion failed");
    return result;
}

std::string hresult_string(long result) {
    std::ostringstream stream;
    stream << "0x" << std::hex << std::uppercase << std::setfill('0') << std::setw(8)
           << static_cast<std::uint32_t>(result);
    return stream.str();
}

void check_hr(long result, std::string_view operation) {
    if (FAILED(result)) throw AppError(3, std::string(operation) + " failed: " + hresult_string(result));
}

std::string utc_now() {
    SYSTEMTIME time{}; GetSystemTime(&time);
    char buffer[40]{};
    std::snprintf(buffer, sizeof(buffer), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
                  time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
    return buffer;
}
} // namespace capdvhs
