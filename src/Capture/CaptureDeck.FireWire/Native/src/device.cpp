#include "capdvhs/directshow.hpp"
#include "capdvhs/util.hpp"

#include <cfgmgr32.h>
#include <setupapi.h>

#include <array>
#include <cstring>
#include <iomanip>
#include <limits>
#include <optional>
#include <sstream>
#include <utility>

namespace capdvhs {
namespace {

constexpr GUID mstape_guid{0x8c0f6af2, 0x0edb, 0x44c1,
                           {0x8a, 0xeb, 0x59, 0x04, 0x0b, 0xd8, 0x30, 0xed}};

void release_media(AM_MEDIA_TYPE& type) noexcept {
    CoTaskMemFree(type.pbFormat);
    if (type.pUnk) type.pUnk->Release();
    type = {};
}

AM_MEDIA_TYPE clone_media(const AM_MEDIA_TYPE& source) {
    if (source.cbFormat && !source.pbFormat) {
        throw AppError(3, "Media type has a nonzero format size and a null format block");
    }
    AM_MEDIA_TYPE result = source;
    result.pbFormat = nullptr;
    result.pUnk = nullptr;
    if (source.cbFormat) {
        result.pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(source.cbFormat));
        if (!result.pbFormat) throw std::bad_alloc();
        std::memcpy(result.pbFormat, source.pbFormat, source.cbFormat);
    }
    if (source.pUnk) {
        result.pUnk = source.pUnk;
        result.pUnk->AddRef();
    }
    return result;
}

struct VariantValue {
    VARIANT value;
    VariantValue() { VariantInit(&value); }
    ~VariantValue() { VariantClear(&value); }
};

bool missing_property(HRESULT hr) {
    return hr == E_INVALIDARG || hr == DISP_E_MEMBERNOTFOUND ||
           hr == TYPE_E_ELEMENTNOTFOUND ||
           hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ||
           hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}

std::optional<std::wstring> string_property(IPropertyBag* bag, const wchar_t* name) {
    VariantValue result;
    const HRESULT hr = bag->Read(name, &result.value, nullptr);
    if (missing_property(hr)) return std::nullopt;
    check_hr(hr, "Read device property " + utf8(name));
    if (result.value.vt == VT_EMPTY || result.value.vt == VT_NULL) return std::nullopt;
    if (result.value.vt != VT_BSTR) {
        throw AppError(3, "Unexpected type for device property " + utf8(name));
    }
    if (!result.value.bstrVal) return std::wstring{};
    return std::wstring(result.value.bstrVal, SysStringLen(result.value.bstrVal));
}

std::optional<std::uint32_t> uint_property(IPropertyBag* bag, const wchar_t* name) {
    VariantValue result;
    const HRESULT hr = bag->Read(name, &result.value, nullptr);
    if (missing_property(hr)) return std::nullopt;
    check_hr(hr, "Read device property " + utf8(name));
    switch (result.value.vt) {
    case VT_EMPTY:
    case VT_NULL: return std::nullopt;
    case VT_I4: return static_cast<std::uint32_t>(result.value.lVal);
    case VT_UI4: return result.value.ulVal;
    case VT_INT: return static_cast<std::uint32_t>(result.value.intVal);
    case VT_UINT: return result.value.uintVal;
    default: throw AppError(3, "Unexpected type for device property " + utf8(name));
    }
}

std::string optional_string(IPropertyBag* bag, const wchar_t* name) {
    return utf8(string_property(bag, name).value_or(L""));
}

std::string moniker_name(IMoniker* moniker) {
    LPOLESTR value = nullptr;
    const HRESULT hr = moniker->GetDisplayName(nullptr, nullptr, &value);
    check_hr(hr, "Get device moniker display name");
    if (!value) throw AppError(3, "Device moniker returned a null display name");
    try {
        std::string result = utf8(value);
        CoTaskMemFree(value);
        return result;
    } catch (...) {
        CoTaskMemFree(value);
        throw;
    }
}

std::vector<MediaType> pin_media_types(IPin* pin) {
    ComPtr<IEnumMediaTypes> enumerator;
    check_hr(pin->EnumMediaTypes(&enumerator), "Enumerate pin media types");
    if (!enumerator) throw AppError(3, "Pin returned a null media type enumerator");
    std::vector<MediaType> result;
    for (;;) {
        AM_MEDIA_TYPE* raw = nullptr;
        ULONG fetched = 0;
        const HRESULT hr = enumerator->Next(1, &raw, &fetched);
        if (hr == S_FALSE && fetched == 0 && !raw) break;
        if (FAILED(hr) || hr != S_OK || fetched != 1 || !raw) {
            if (raw) {
                release_media(*raw);
                CoTaskMemFree(raw);
            }
            check_hr(hr, "Read enumerated pin media type");
            throw AppError(3, "Media type enumerator returned an incomplete result");
        }
        MediaType owned;
        owned.value = *raw;
        CoTaskMemFree(raw);
        result.push_back(std::move(owned));
    }
    return result;
}

std::string pin_id(IPin* pin) {
    LPWSTR raw = nullptr;
    check_hr(pin->QueryId(&raw), "Query pin identifier");
    if (!raw) throw AppError(3, "Pin returned a null identifier");
    try {
        std::string result = utf8(raw);
        CoTaskMemFree(raw);
        return result;
    } catch (...) {
        CoTaskMemFree(raw);
        throw;
    }
}

bool is_capture_pin(IPin* pin) {
    ComPtr<IKsPropertySet> properties;
    const HRESULT query = pin->QueryInterface(IID_PPV_ARGS(&properties));
    if (query == E_NOINTERFACE) return false;
    check_hr(query, "Query pin property interface");
    GUID category{};
    DWORD returned = 0;
    const HRESULT hr = properties->Get(AMPROPSETID_Pin, AMPROPERTY_PIN_CATEGORY,
                                       nullptr, 0, &category, sizeof(category), &returned);
    // Pin categories are optional. An otherwise usable output remains eligible.
    return hr == S_OK && returned == sizeof(category) && category == PIN_CATEGORY_CAPTURE;
}

struct DeviceSet {
    HDEVINFO value = SetupDiCreateDeviceInfoList(nullptr, nullptr);
    ~DeviceSet() { if (value != INVALID_HANDLE_VALUE) SetupDiDestroyDeviceInfoList(value); }
};

std::string driver_unavailable(const std::string& operation, DWORD error) {
    return "{\"available\":false,\"operation\":" + json_quote(operation) +
           ",\"win32_error\":" + std::to_string(error) + "}";
}

std::optional<std::wstring> device_registry_string(HDEVINFO devices, SP_DEVINFO_DATA& device,
                                                   DWORD property, DWORD& error) {
    DWORD type = 0;
    DWORD bytes = 0;
    if (!SetupDiGetDeviceRegistryPropertyW(devices, &device, property, &type, nullptr, 0, &bytes)) {
        error = GetLastError();
        if (error != ERROR_INSUFFICIENT_BUFFER) return std::nullopt;
    }
    if (bytes > 1024 * 1024) {
        error = ERROR_INVALID_DATA;
        return std::nullopt;
    }
    std::vector<wchar_t> buffer((bytes + sizeof(wchar_t) - 1) / sizeof(wchar_t) + 1, L'\0');
    if (!SetupDiGetDeviceRegistryPropertyW(devices, &device, property, &type,
            reinterpret_cast<PBYTE>(buffer.data()), bytes, nullptr)) {
        error = GetLastError();
        return std::nullopt;
    }
    if (type != REG_SZ && type != REG_EXPAND_SZ) {
        error = ERROR_DATATYPE_MISMATCH;
        return std::nullopt;
    }
    error = ERROR_SUCCESS;
    return std::wstring(buffer.data());
}

std::optional<std::wstring> registry_string(HKEY key, const wchar_t* name, DWORD& error) {
    DWORD type = 0;
    DWORD bytes = 0;
    error = RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes);
    if (error != ERROR_SUCCESS) return std::nullopt;
    if (bytes > 1024 * 1024) {
        error = ERROR_INVALID_DATA;
        return std::nullopt;
    }
    std::vector<wchar_t> buffer((bytes + sizeof(wchar_t) - 1) / sizeof(wchar_t) + 1, L'\0');
    error = RegQueryValueExW(key, name, nullptr, &type,
                            reinterpret_cast<LPBYTE>(buffer.data()), &bytes);
    if (error != ERROR_SUCCESS) return std::nullopt;
    if (type != REG_SZ && type != REG_EXPAND_SZ) {
        error = ERROR_DATATYPE_MISMATCH;
        return std::nullopt;
    }
    return std::wstring(buffer.data());
}

std::string driver_json(const DeviceInfo& device) {
    if (device.device_path.empty()) return driver_unavailable("DevicePath is unavailable", ERROR_NOT_FOUND);
    DeviceSet devices;
    if (devices.value == INVALID_HANDLE_VALUE) {
        return driver_unavailable("SetupDiCreateDeviceInfoList", GetLastError());
    }
    SP_DEVICE_INTERFACE_DATA interface_data{};
    interface_data.cbSize = sizeof(interface_data);
    const auto path = wide(device.device_path);
    if (!SetupDiOpenDeviceInterfaceW(devices.value, path.c_str(), 0, &interface_data)) {
        return driver_unavailable("SetupDiOpenDeviceInterface", GetLastError());
    }
    DWORD bytes = 0;
    if (!SetupDiGetDeviceInterfaceDetailW(devices.value, &interface_data, nullptr, 0, &bytes, nullptr) &&
        GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        return driver_unavailable("Size device interface detail", GetLastError());
    }
    if (bytes < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || bytes > 1024 * 1024) {
        return driver_unavailable("Validate device interface detail size", ERROR_INVALID_DATA);
    }
    std::vector<std::uint8_t> detail_storage(bytes);
    auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(detail_storage.data());
    detail->cbSize = sizeof(*detail);
    SP_DEVINFO_DATA info{};
    info.cbSize = sizeof(info);
    if (!SetupDiGetDeviceInterfaceDetailW(devices.value, &interface_data, detail, bytes, nullptr, &info)) {
        return driver_unavailable("Read device interface detail", GetLastError());
    }
    std::string errors = "[";
    auto record_error = [&](const std::string& operation, DWORD error) {
        if (errors.size() > 1) errors += ',';
        errors += "{\"operation\":" + json_quote(operation) + ",\"code\":" + std::to_string(error) + "}";
    };
    DWORD count = 0;
    std::string instance;
    if (!SetupDiGetDeviceInstanceIdW(devices.value, &info, nullptr, 0, &count) &&
        GetLastError() != ERROR_INSUFFICIENT_BUFFER) {
        record_error("Size instance ID", GetLastError());
    } else if (count > 0 && count < 1024 * 1024) {
        std::vector<wchar_t> buffer(count, L'\0');
        if (SetupDiGetDeviceInstanceIdW(devices.value, &info, buffer.data(), count, nullptr)) {
            instance = utf8(buffer.data());
        } else {
            record_error("Read instance ID", GetLastError());
        }
    } else {
        record_error("Validate instance ID size", ERROR_INVALID_DATA);
    }
    DWORD error = ERROR_SUCCESS;
    const auto service = device_registry_string(devices.value, info, SPDRP_SERVICE, error);
    if (!service) record_error("Read driver service", error);
    std::string inf;
    std::string driver_version;
    std::string provider;
    HKEY driver_key = SetupDiOpenDevRegKey(devices.value, &info, DICS_FLAG_GLOBAL, 0,
                                          DIREG_DRV, KEY_QUERY_VALUE);
    if (driver_key != reinterpret_cast<HKEY>(INVALID_HANDLE_VALUE)) {
        struct CloseKey { HKEY value; ~CloseKey() { RegCloseKey(value); } } close_key{driver_key};
        const auto inf_value = registry_string(driver_key, L"InfPath", error);
        if (inf_value) inf = utf8(*inf_value); else record_error("Read driver INF", error);
        const auto version_value = registry_string(driver_key, L"DriverVersion", error);
        if (version_value) driver_version = utf8(*version_value); else record_error("Read driver version", error);
        const auto provider_value = registry_string(driver_key, L"ProviderName", error);
        if (provider_value) provider = utf8(*provider_value); else record_error("Read driver provider", error);
    } else {
        record_error("Open driver registry key", GetLastError());
    }
    ULONG status = 0;
    ULONG problem = 0;
    const CONFIGRET config = CM_Get_DevNode_Status(&status, &problem, info.DevInst, 0);
    if (config != CR_SUCCESS) record_error("CM_Get_DevNode_Status", config);
    errors += ']';
    return "{\"available\":true,\"instance_id\":" + json_quote(instance) +
           ",\"service\":" + json_quote(utf8(service.value_or(L""))) +
           ",\"driver_inf\":" + json_quote(inf) +
           ",\"driver_version\":" + json_quote(driver_version) +
           ",\"driver_provider\":" + json_quote(provider) +
           ",\"status\":" + (config == CR_SUCCESS ? std::to_string(status) : "null") +
           ",\"problem_code\":" + (config == CR_SUCCESS ? std::to_string(problem) : "null") +
           ",\"errors\":" + errors + "}";
}

} // namespace

MediaType::MediaType(const AM_MEDIA_TYPE& source) : value(clone_media(source)) {}
MediaType::MediaType(const MediaType& source) : MediaType(source.value) {}
MediaType& MediaType::operator=(const MediaType& source) {
    if (this != &source) {
        MediaType copy(source);
        *this = std::move(copy);
    }
    return *this;
}
MediaType::MediaType(MediaType&& source) noexcept : value(std::exchange(source.value, AM_MEDIA_TYPE{})) {}
MediaType& MediaType::operator=(MediaType&& source) noexcept {
    if (this != &source) {
        release_media(value);
        value = std::exchange(source.value, AM_MEDIA_TYPE{});
    }
    return *this;
}
MediaType::~MediaType() { release_media(value); }

std::string guid_string(const GUID& guid) {
    std::array<wchar_t, 39> buffer{};
    if (!StringFromGUID2(guid, buffer.data(), static_cast<int>(buffer.size()))) {
        throw AppError(3, "Cannot format GUID");
    }
    return utf8(buffer.data());
}

std::vector<DeviceInfo> enumerate_devices() {
    ComPtr<ICreateDevEnum> devices;
    check_hr(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER,
                              IID_PPV_ARGS(&devices)), "Create system device enumerator");
    ComPtr<IEnumMoniker> enumerator;
    const HRESULT enumeration = devices->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &enumerator, 0);
    if (enumeration == S_FALSE) return {};
    check_hr(enumeration, "Enumerate video capture devices");
    if (!enumerator) throw AppError(3, "System returned a null device enumerator");
    std::vector<DeviceInfo> result;
    for (;;) {
        ComPtr<IMoniker> moniker;
        ULONG fetched = 0;
        const HRESULT hr = enumerator->Next(1, &moniker, &fetched);
        if (hr == S_FALSE && fetched == 0 && !moniker) break;
        check_hr(hr, "Read enumerated capture device");
        if (hr != S_OK || fetched != 1 || !moniker) {
            throw AppError(3, "Device enumerator returned an incomplete result");
        }
        ComPtr<IPropertyBag> bag;
        check_hr(moniker->BindToStorage(nullptr, nullptr, IID_PPV_ARGS(&bag)), "Read device property bag");
        const auto class_text = string_property(bag.Get(), L"DeviceClassGUID");
        if (!class_text || class_text->empty()) continue;
        GUID class_id{};
        const HRESULT parsed = CLSIDFromString(class_text->c_str(), &class_id);
        if (FAILED(parsed) || class_id != mstape_guid) continue;
        // Never instantiate unrelated cameras while looking for an MSTape source.
        DeviceInfo item;
        item.moniker = moniker;
        item.class_guid = guid_string(class_id);
        item.name = optional_string(bag.Get(), L"FriendlyName");
        item.description = optional_string(bag.Get(), L"Description");
        item.device_path = optional_string(bag.Get(), L"DevicePath");
        item.vendor = optional_string(bag.Get(), L"VendorText");
        item.model = optional_string(bag.Get(), L"UnitModelText");
        if (item.model.empty()) item.model = optional_string(bag.Get(), L"ModelText");
        item.opcr_payload = uint_property(bag.Get(), L"DeviceOPcr0Payload").value_or(0);
        item.opcr_data_rate = uint_property(bag.Get(), L"DeviceOPcr0DataRate").value_or(0);
        const auto low = uint_property(bag.Get(), L"UniqueID_Low");
        const auto high = uint_property(bag.Get(), L"UniqueID_High");
        if (low && high) {
            std::ostringstream id;
            id << std::hex << std::setfill('0') << std::setw(8) << *high << std::setw(8) << *low;
            item.id = id.str();
        } else {
            item.id = moniker_name(moniker.Get());
        }
        if (item.name.empty()) item.name = item.description.empty() ? item.model : item.description;
        if (item.name.empty()) item.name = "Microsoft AV/C Tape Subunit Device";
        result.push_back(std::move(item));
    }
    return result;
}

ComPtr<IBaseFilter> bind_device(const DeviceInfo& device) {
    if (!device.moniker) throw AppError(3, "Device does not have a moniker");
    ComPtr<IBaseFilter> result;
    check_hr(device.moniker->BindToObject(nullptr, nullptr, IID_PPV_ARGS(&result)), "Bind MSTape capture source");
    if (!result) throw AppError(3, "MSTape returned a null source filter");
    return result;
}

std::vector<PinInfo> inspect_pins(IBaseFilter* filter) {
    if (!filter) throw AppError(3, "Cannot inspect a null source filter");
    ComPtr<IEnumPins> enumerator;
    check_hr(filter->EnumPins(&enumerator), "Enumerate source pins");
    if (!enumerator) throw AppError(3, "Source returned a null pin enumerator");
    std::vector<PinInfo> result;
    for (;;) {
        ComPtr<IPin> pin;
        ULONG fetched = 0;
        const HRESULT hr = enumerator->Next(1, &pin, &fetched);
        if (hr == S_FALSE && fetched == 0 && !pin) break;
        check_hr(hr, "Read enumerated source pin");
        if (hr != S_OK || fetched != 1 || !pin) throw AppError(3, "Pin enumerator returned an incomplete result");
        PIN_DIRECTION direction{};
        check_hr(pin->QueryDirection(&direction), "Query source pin direction");
        result.push_back(PinInfo{pin_id(pin.Get()), direction == PINDIR_OUTPUT, pin_media_types(pin.Get())});
    }
    return result;
}

bool media_layout(const AM_MEDIA_TYPE& type, PacketLayout& layout) {
    if (type.majortype != MEDIATYPE_Stream) return false;
    PacketLayout candidate;
    if (type.subtype == MEDIASUBTYPE_MPEG2_TRANSPORT) {
        if (type.cbFormat != 0) return false;
    } else if (type.subtype == MEDIASUBTYPE_MPEG2_TRANSPORT_STRIDE) {
        if (type.cbFormat) {
            if (!type.pbFormat || type.cbFormat < 3 * sizeof(std::uint32_t)) return false;
            std::array<std::uint32_t, 3> fields{};
            std::memcpy(fields.data(), type.pbFormat, sizeof(fields));
            candidate = PacketLayout{fields[0], fields[1], fields[2]};
        }
    } else {
        return false;
    }
    if (candidate.packet_length != 188 || (candidate.stride != 188 && candidate.stride != 192) ||
        candidate.offset > candidate.stride - candidate.packet_length) return false;
    layout = candidate;
    return true;
}

std::string device_json(const DeviceInfo& device) {
    const bool uid = device.id.size() == 16 && device.id.find_first_not_of("0123456789abcdefABCDEF") == std::string::npos;
    return "{\"id\":" + json_quote(device.id) + ",\"id_kind\":" + json_quote(uid ? "uid64" : "moniker") +
           ",\"name\":" + json_quote(device.name) + ",\"description\":" + json_quote(device.description) +
           ",\"device_path\":" + json_quote(device.device_path) + ",\"vendor\":" + json_quote(device.vendor) +
           ",\"model\":" + json_quote(device.model) + ",\"class_guid\":" + json_quote(device.class_guid) +
           ",\"opcr_payload\":" + std::to_string(device.opcr_payload) +
           ",\"opcr_data_rate\":" + std::to_string(device.opcr_data_rate) +
           ",\"driver\":" + driver_json(device) + "}";
}

std::string media_json(const AM_MEDIA_TYPE& type) {
    PacketLayout layout;
    const bool supported = media_layout(type, layout);
    std::string format_hex;
    if (type.cbFormat && type.pbFormat) {
        constexpr char digits[] = "0123456789abcdef";
        format_hex.reserve(static_cast<std::size_t>(type.cbFormat) * 2);
        for (ULONG i = 0; i < type.cbFormat; ++i) {
            format_hex += digits[type.pbFormat[i] >> 4];
            format_hex += digits[type.pbFormat[i] & 15];
        }
    }
    return "{\"major_type\":" + json_quote(guid_string(type.majortype)) +
           ",\"subtype\":" + json_quote(guid_string(type.subtype)) +
           ",\"format_type\":" + json_quote(guid_string(type.formattype)) +
           ",\"sample_size\":" + std::to_string(type.lSampleSize) +
           ",\"fixed_size_samples\":" + (type.bFixedSizeSamples ? "true" : "false") +
           ",\"temporal_compression\":" + (type.bTemporalCompression ? "true" : "false") +
           ",\"format_size\":" + std::to_string(type.cbFormat) +
           ",\"format_hex\":" + json_quote(format_hex) +
           ",\"supported\":" + (supported ? "true" : "false") +
           ",\"layout\":" + (supported ? "{\"offset\":" + std::to_string(layout.offset) +
                 ",\"packet_length\":" + std::to_string(layout.packet_length) +
                 ",\"stride\":" + std::to_string(layout.stride) + "}" : "null") + "}";
}

std::string_view format_name(TsFormat format) {
    switch (format) {
        case TsFormat::ts188: return "ts188";
        case TsFormat::ts192: return "ts192";
        case TsFormat::automatic: return "auto";
    }
    return "auto";
}

ComPtr<IPin> find_output_pin(IBaseFilter* source, TsFormat format, MediaType& selected) {
    if (!source) throw AppError(3, "Cannot select a pin from a null source");
    ComPtr<IEnumPins> enumerator;
    check_hr(source->EnumPins(&enumerator), "Enumerate capture output pins");
    if (!enumerator) throw AppError(3, "Source returned a null pin enumerator");
    ComPtr<IPin> best;
    MediaType best_type;
    int best_score = std::numeric_limits<int>::max();
    for (;;) {
        ComPtr<IPin> pin;
        ULONG fetched = 0;
        const HRESULT hr = enumerator->Next(1, &pin, &fetched);
        if (hr == S_FALSE && fetched == 0 && !pin) break;
        check_hr(hr, "Read capture output pin");
        if (hr != S_OK || fetched != 1 || !pin) throw AppError(3, "Pin enumerator returned an incomplete result");
        PIN_DIRECTION direction{};
        check_hr(pin->QueryDirection(&direction), "Query capture pin direction");
        if (direction != PINDIR_OUTPUT) continue;
        const bool capture = is_capture_pin(pin.Get());
        for (auto& type : pin_media_types(pin.Get())) {
            PacketLayout layout;
            if (!media_layout(type.value, layout)) continue;
            const bool native192 = layout == PacketLayout{4, 188, 192};
            const bool native188 = layout == PacketLayout{0, 188, 188};
            const bool allow_192 = format != TsFormat::ts188;
            const bool allow_188 = format != TsFormat::ts192;
            if (!(native192 && allow_192) && !(native188 && allow_188)) continue;
            const int score = (native192 ? 0 : 2) + (capture ? 0 : 1);
            if (score < best_score) {
                best = pin;
                best_type = std::move(type);
                best_score = score;
            }
        }
    }
    if (!best) {
        if (format == TsFormat::automatic)
            throw AppError(4, "No native 188/192-byte MSTape output pin is available");
        throw AppError(4, "No native " + std::string(format_name(format)) + " MSTape output pin is available");
    }
    selected = std::move(best_type);
    return best;
}

} // namespace capdvhs
