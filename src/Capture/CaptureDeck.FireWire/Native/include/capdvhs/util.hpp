#pragma once

#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>

namespace capdvhs {
inline constexpr char version[] = "0.1.1";
inline constexpr unsigned schema_version = 1;
std::string json_quote(std::string_view value);
std::string utf8(std::wstring_view value);
std::wstring wide(std::string_view value);
std::string hresult_string(long result);
void check_hr(long result, std::string_view operation);
std::string utc_now();
bool trace_enabled() noexcept;
void trace_event(std::string_view message) noexcept;
class AppError : public std::runtime_error {
public:
    AppError(int exit_code, const std::string& message) : std::runtime_error(message), code(exit_code) {}
    int code;
};
} // namespace capdvhs
