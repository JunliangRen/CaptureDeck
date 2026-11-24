#pragma once
#include "capdvhs/directshow.hpp"
#include <atomic>
#include <filesystem>
#include <functional>
#include <string>

namespace capdvhs {
struct CaptureOptions {
    std::string device_id;
    std::filesystem::path output;
    TsFormat format = TsFormat::automatic;
    double duration = 0;
    double no_data_timeout = 30;
    bool json = false;
};
// Called on a dedicated control thread; no COM objects cross thread boundaries.
// Internal test seam: factory executes inside the control apartment. Production
// CLI never supplies it, and injected sources are explicitly labeled in reports.
struct CaptureSource {
    DeviceInfo device;
    ComPtr<IBaseFilter> filter;
};
using CaptureSourceFactory = std::function<CaptureSource()>;
int run_capture(const CaptureOptions& options, std::atomic<bool>& cancel,
                CaptureSourceFactory source_factory = {});
} // namespace capdvhs
