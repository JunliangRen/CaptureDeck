#include "capdvhs/capture.hpp"
#include "capdvhs/directshow.hpp"
#include "capdvhs/pipeline.hpp"
#include "capdvhs/util.hpp"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <sstream>
#include <thread>

namespace {
using namespace capdvhs;
std::atomic<bool> cancelled{false};
BOOL WINAPI console_handler(DWORD event) {
    if (event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT) { cancelled.store(true); return TRUE; }
    return FALSE;
}
struct ComApartment {
    ComApartment() { check_hr(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "CoInitializeEx"); }
    ~ComApartment() { CoUninitialize(); }
};
void help() {
    std::cout << "CaptureDeck FireWire Probe " << version << " (Windows x64)\n"
                 "Passive IEEE1394/MSTape transport-stream capture. Play the tape manually.\n\n"
                 "  CaptureDeck.FireWire.Probe list [--json]\n"
                 "  CaptureDeck.FireWire.Probe probe --device <ID> [--json]\n"
                 "  CaptureDeck.FireWire.Probe capture --device <ID> --output <path>\n"
                 "      [--format ts192|ts188] [--duration <seconds>]\n"
                 "      [--no-data-timeout <seconds>] [--json]\n"
                 "  CaptureDeck.FireWire.Probe analyze --input <path> [--format ts192|ts188] [--json]\n\n"
                 "Omit --format to select automatically (192 preferred for capture). Default no-data timeout: 30 seconds.\n"
                 "Duration starts at the first nonempty sample. Ctrl+C drains and closes.\n"
                 "Existing output, partial, and report files are never overwritten.\n"
                 "Capture reports: <output>.report.json. Failed data: <output>.partial.\n"
                 "Exit codes: 0 success, 2 arguments, 3 device/COM, 4 format,\n"
                 "            5 file I/O, 6 stream/queue/device loss, 7 no data.\n";
}
double seconds(const std::string& value, bool allow_zero) {
    std::size_t consumed = 0;
    double result = 0;
    try { result = std::stod(value, &consumed); }
    catch (...) { throw AppError(2, "Invalid number of seconds: " + value); }
    if (consumed != value.size() || !std::isfinite(result) || result < 0 || (!allow_zero && result == 0)
        || result > 31536000)
        throw AppError(2, "Seconds must be finite, " + std::string(allow_zero ? "nonnegative" : "positive") +
                          ", and no more than one year");
    return result;
}
std::string envelope(std::string_view type) {
    return "{\"schema_version\":" + std::to_string(schema_version) + ",\"version\":" + json_quote(version) +
           ",\"type\":" + json_quote(type);
}
bool same_id(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    return std::equal(a.begin(), a.end(), b.begin(), [](unsigned char x, unsigned char y) {
        return std::tolower(x) == std::tolower(y);
    });
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    using namespace capdvhs;
    SetConsoleOutputCP(CP_UTF8);
    bool json = false;
    for (int i = 1; i < argc; ++i) if (std::wstring_view(argv[i]) == L"--json") json = true;
    try {
        if (argc < 2 || (argc == 2 && (std::wstring_view(argv[1]) == L"--help" || std::wstring_view(argv[1]) == L"help"))) {
            help(); return 0;
        }
        if (argc == 2 && std::wstring_view(argv[1]) == L"--version") {
            std::cout << version << '\n'; return 0;
        }
        const std::string command = utf8(argv[1]);
        std::set<std::string> allowed;
        if (command == "list") allowed = {};
        else if (command == "probe") allowed = {"--device"};
        else if (command == "capture") allowed = {"--device", "--output", "--format", "--duration", "--no-data-timeout"};
        else if (command == "analyze") allowed = {"--input", "--format"};
        else throw AppError(2, "Unknown command: " + command);
        std::map<std::string, std::wstring> args;
        bool saw_json = false;
        for (int i = 2; i < argc; ++i) {
            auto option = utf8(argv[i]);
            if (option == "--json") {
                if (saw_json) throw AppError(2, "Duplicate option: --json");
                saw_json = true; continue;
            }
            if (!allowed.contains(option)) throw AppError(2, "Unknown option: " + option);
            if (args.contains(option)) throw AppError(2, "Duplicate option: " + option);
            if (i + 1 >= argc || std::wstring_view(argv[i + 1]).starts_with(L"--"))
                throw AppError(2, "Missing value for " + option);
            args.emplace(option, argv[++i]);
        }
        auto required = [&](const std::string& option) -> const std::wstring& {
            auto found = args.find(option);
            if (found == args.end() || found->second.empty()) throw AppError(2, "Required option: " + option);
            return found->second;
        };
        TsFormat format = TsFormat::automatic;
        if (args.contains("--format")) {
            const auto& value = args.at("--format");
            if (value == L"ts188") format = TsFormat::ts188;
            else if (value == L"ts192") format = TsFormat::ts192;
            else throw AppError(2, "Format must be ts192 or ts188");
        }
        if (command == "capture") {
            CaptureOptions options;
            options.device_id = utf8(required("--device"));
            options.output = std::filesystem::absolute(required("--output"));
            options.format = format;
            options.json = json;
            if (args.contains("--duration")) options.duration = seconds(utf8(args.at("--duration")), true);
            if (args.contains("--no-data-timeout")) options.no_data_timeout = seconds(utf8(args.at("--no-data-timeout")), false);
            if (!SetConsoleCtrlHandler(console_handler, TRUE)) throw AppError(6, "Cannot install Ctrl+C handler");
            int result = 6;
            std::thread control([&] {
                try { result = run_capture(options, cancelled); }
                catch (const std::exception& error) {
                    std::cerr << "Capture controller failure: " << error.what() << '\n'; result = 6;
                }
            });
            control.join();
            SetConsoleCtrlHandler(console_handler, FALSE);
            return result;
        }
        if (command == "analyze") {
            const std::filesystem::path input_path = std::filesystem::absolute(required("--input"));
            std::ifstream input(input_path, std::ios::binary);
            if (!input) throw AppError(5, "Cannot open input file: " + utf8(input_path.native()));
            std::vector<std::uint8_t> bytes(1024 * 1024);
            input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
            const auto initial = input.gcount();
            if (input.bad()) throw AppError(5, "Input file read failed");
            PacketLayout layout;
            if (format == TsFormat::ts188) layout = PacketLayout{0,188,188};
            else if (format == TsFormat::ts192) layout = PacketLayout{4,188,192};
            else {
                try { layout = detect_layout(std::span(bytes.data(), static_cast<std::size_t>(initial))); }
                catch (const std::exception& error) { throw AppError(4, error.what()); }
            }
            TsAnalyzer analyzer(layout);
            analyzer.feed(std::span(bytes.data(), static_cast<std::size_t>(initial)));
            if (!input.eof()) {
                for (;;) {
                    input.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
                    const auto count = input.gcount();
                    if (input.bad()) throw AppError(5, "Input file read failed");
                    if (count > 0) analyzer.feed(std::span(bytes.data(), static_cast<std::size_t>(count)));
                    if (input.eof()) break;
                    if (input.fail()) throw AppError(5, "Input file read failed");
                }
            }
            analyzer.finish();
            const auto& stats = analyzer.stats();
            const int code = (stats.sync_errors || stats.malformed_packets || stats.trailing_bytes || !stats.packets) ? 4 : 0;
            if (json) {
                std::cout << envelope("analysis_result") << ",\"success\":" << (code ? "false" : "true")
                          << ",\"exit_code\":" << code << ",\"input_path\":" << json_quote(utf8(input_path.native()))
                          << ",\"format\":" << json_quote(layout.stride == 192 ? "ts192" : "ts188")
                          << ",\"analysis\":" << ts_stats_json(stats) << "}\n";
            } else {
                std::cout << "format=ts" << layout.stride << " input=" << stats.input_bytes << " packets=" << stats.packets
                          << " sync_errors=" << stats.sync_errors << " malformed=" << stats.malformed_packets
                          << " TEI=" << stats.tei << " continuity_errors=" << stats.continuity_errors
                          << " pcr_backwards=" << stats.pcr_backwards
                          << " trailing=" << stats.trailing_bytes << '\n';
            }
            return code;
        }

        const std::string wanted_device = command == "probe" ? utf8(required("--device")) : std::string{};
        ComApartment apartment;
        auto devices = enumerate_devices();
        if (command == "list") {
            if (json) {
                std::cout << envelope("device_list") << ",\"devices\":[";
                bool first = true;
                for (const auto& device : devices) {
                    if (!first) std::cout << ','; first = false;
                    std::cout << device_json(device);
                }
                std::cout << "]}\n";
            } else {
                if (devices.empty()) std::cout << "No MSTape devices found.\n";
                for (const auto& device : devices) std::cout << device.id << "  " << device.vendor << ' ' << device.model
                                                          << "  " << device.name << '\n';
            }
            return 0;
        }
        auto found = std::find_if(devices.begin(), devices.end(), [&](const DeviceInfo& device) { return same_id(device.id, wanted_device); });
        if (found == devices.end()) throw AppError(3, "Tape device not found: " + wanted_device);
        auto source = bind_device(*found);
        auto pins = inspect_pins(source.Get());
        const auto has_interface = [&](const IID& iid) {
            ComPtr<IUnknown> pointer;
            HRESULT result = source->QueryInterface(iid, reinterpret_cast<void**>(pointer.GetAddressOf()));
            if (result == E_NOINTERFACE) return false;
            check_hr(result, "Query source interface"); return true;
        };
        const bool transport = has_interface(__uuidof(IAMExtTransport));
        const bool timecode = has_interface(__uuidof(IAMTimecodeReader));
        std::ostringstream result;
        result << envelope("device_probe") << ",\"device\":" << device_json(*found)
               << ",\"interfaces\":{\"IAMExtTransport\":" << (transport ? "true" : "false")
               << ",\"IAMTimecodeReader\":" << (timecode ? "true" : "false") << "},\"pins\":[";
        bool first_pin = true;
        for (const auto& pin : pins) {
            if (!first_pin) result << ','; first_pin = false;
            result << "{\"id\":" << json_quote(pin.id) << ",\"direction\":" << json_quote(pin.output ? "output" : "input")
                   << ",\"media_types\":[";
            bool first_type = true;
            for (const auto& type : pin.media_types) {
                if (!first_type) result << ','; first_type = false;
                result << media_json(type.value);
            }
            result << "]}";
        }
        result << "]}";
        if (json) std::cout << result.str() << '\n';
        else {
            std::cout << found->vendor << ' ' << found->model << " [" << found->id << "]\n"
                      << "IAMExtTransport=" << transport << " IAMTimecodeReader=" << timecode << '\n';
            for (const auto& pin : pins) {
                std::cout << (pin.output ? "Output" : "Input") << " pin " << pin.id << '\n';
                for (const auto& type : pin.media_types) std::cout << "  " << media_json(type.value) << '\n';
            }
        }
        return 0;
    } catch (const AppError& error) {
        std::cerr << error.what() << '\n';
        if (json) std::cout << envelope("error") << ",\"success\":false,\"exit_code\":" << error.code
                            << ",\"error\":" << json_quote(error.what()) << "}\n";
        return error.code;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        if (json) std::cout << envelope("error") << ",\"success\":false,\"exit_code\":5,\"error\":" << json_quote(error.what()) << "}\n";
        return 5;
    }
}
