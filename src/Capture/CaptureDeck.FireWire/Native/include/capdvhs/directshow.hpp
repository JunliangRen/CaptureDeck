#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dshow.h>
#include <wrl/client.h>

#include "capdvhs/core.hpp"

#include <functional>
#include <atomic>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace capdvhs {

template<class T> using ComPtr = Microsoft::WRL::ComPtr<T>;

struct MediaType {
    AM_MEDIA_TYPE value{};
    MediaType() = default;
    explicit MediaType(const AM_MEDIA_TYPE& source);
    MediaType(const MediaType& source);
    MediaType& operator=(const MediaType& source);
    MediaType(MediaType&& source) noexcept;
    MediaType& operator=(MediaType&& source) noexcept;
    ~MediaType();
};

struct DeviceInfo {
    std::string id;
    std::string name;
    std::string description;
    std::string device_path;
    std::string vendor;
    std::string model;
    std::string class_guid;
    std::uint32_t opcr_payload = 0;
    std::uint32_t opcr_data_rate = 0;
    ComPtr<IMoniker> moniker;
};

struct PinInfo {
    std::string id;
    bool output = false;
    std::vector<MediaType> media_types;
};

std::vector<DeviceInfo> enumerate_devices();
ComPtr<IBaseFilter> bind_device(const DeviceInfo& device);
std::vector<PinInfo> inspect_pins(IBaseFilter* filter);
bool media_layout(const AM_MEDIA_TYPE& type, PacketLayout& layout);
std::string guid_string(const GUID& guid);
std::string device_json(const DeviceInfo& device);
std::string media_json(const AM_MEDIA_TYPE& type);
enum class TsFormat { automatic, ts188, ts192 };
std::string_view format_name(TsFormat format);
ComPtr<IPin> find_output_pin(IBaseFilter* source, TsFormat format, MediaType& selected);

using SampleHandler = std::function<HRESULT(std::span<const std::uint8_t>, bool)>;
using StreamErrorHandler = std::function<void(const std::string&)>;
using EndOfStreamHandler = std::function<void()>;
struct SinkDiagnostics {
    std::atomic<std::uint64_t> receive_calls{0};
    std::atomic<std::uint64_t> zero_length_samples{0};
    std::atomic<std::uint64_t> rejected_samples{0};
};
ComPtr<IBaseFilter> create_sink(const AM_MEDIA_TYPE& expected,
                               SampleHandler on_sample,
                               StreamErrorHandler on_error,
                               EndOfStreamHandler on_eos,
                               std::shared_ptr<SinkDiagnostics> diagnostics = {});

} // namespace capdvhs
