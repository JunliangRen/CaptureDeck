#include "capdvhs/capture.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

using namespace capdvhs;
using namespace std::chrono_literals;

namespace {

#define CHECK(expression) do { if (!(expression)) throw std::runtime_error(std::string(#expression) + " at line " + std::to_string(__LINE__)); } while (false)

enum class Mode { eos, hold, no_data, dynamic_change, stop_failure, pin_in_use };

struct SourceState {
    std::atomic<bool> started{false}, produced{false}, destroyed{false};
    std::atomic<bool> first_stop_failed{false}, late_attempted{false};
    std::atomic<HRESULT> late_status{E_UNEXPECTED};
};

void free_type(AM_MEDIA_TYPE* type) {
    if (!type) return;
    CoTaskMemFree(type->pbFormat);
    if (type->pUnk) type->pUnk->Release();
    CoTaskMemFree(type);
}

class PinEnumerator final : public IEnumPins {
public:
    explicit PinEnumerator(IPin* pin, ULONG position = 0) : pin_(pin), position_(position) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IEnumPins) return E_NOINTERFACE;
        *result = static_cast<IEnumPins*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG left = --references_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IPin** pins, ULONG* fetched) override {
        if (!pins || (count != 1 && !fetched)) return E_POINTER;
        if (fetched) *fetched = 0;
        if (!count) return S_OK;
        if (position_) return S_FALSE;
        pins[0] = pin_.Get(); pins[0]->AddRef(); position_ = 1;
        if (fetched) *fetched = 1;
        return count == 1 ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {
        if (!count) return S_OK;
        const bool available = position_ == 0; position_ = 1;
        return available && count == 1 ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { position_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumPins** result) override {
        if (!result) return E_POINTER;
        *result = new (std::nothrow) PinEnumerator(pin_.Get(), position_);
        return *result ? S_OK : E_OUTOFMEMORY;
    }
private:
    std::atomic<ULONG> references_{1};
    ComPtr<IPin> pin_;
    ULONG position_;
};

class TypeEnumerator final : public IEnumMediaTypes {
public:
    explicit TypeEnumerator(const AM_MEDIA_TYPE& type, ULONG position = 0) : type_(type), position_(position) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IEnumMediaTypes) return E_NOINTERFACE;
        *result = static_cast<IEnumMediaTypes*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG left = --references_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, AM_MEDIA_TYPE** types, ULONG* fetched) override {
        if (!types || (count != 1 && !fetched)) return E_POINTER;
        if (fetched) *fetched = 0;
        if (!count) return S_OK;
        if (position_) return S_FALSE;
        types[0] = nullptr;
        try {
            MediaType copy(type_);
            auto* result = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
            if (!result) return E_OUTOFMEMORY;
            *result = std::exchange(copy.value, AM_MEDIA_TYPE{});
            types[0] = result; position_ = 1;
            if (fetched) *fetched = 1;
            return count == 1 ? S_OK : S_FALSE;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {
        if (!count) return S_OK;
        const bool available = position_ == 0; position_ = 1;
        return available && count == 1 ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { position_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumMediaTypes** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        try { *result = new TypeEnumerator(type_.value, position_); return S_OK; }
        catch (...) { return E_OUTOFMEMORY; }
    }
private:
    std::atomic<ULONG> references_{1};
    MediaType type_;
    ULONG position_;
};

std::vector<std::uint8_t> test_stream(PacketLayout layout = {4, 188, 192}) {
    std::vector<std::uint8_t> bytes(layout.stride * 12);
    for (std::size_t packet = 0; packet < 12; ++packet) {
        const auto start = packet * layout.stride;
        for (std::size_t index = 0; index < layout.stride; ++index)
            bytes[start + index] = static_cast<std::uint8_t>((packet * 13 + index) & 255);
        const auto header = start + layout.offset;
        bytes[header] = 0x47;
        bytes[header + 1] = 0x01;
        bytes[header + 2] = 0x00;
        bytes[header + 3] = static_cast<std::uint8_t>(0x10 | packet);
    }
    return bytes;
}

// An in-process DirectShow source with one MPEG-TS output. It exercises the
// production graph, allocator, sink, queue, writer and report/finalize paths.
class SyntheticSource final : public IBaseFilter, public IPin {
public:
    SyntheticSource(Mode mode, std::shared_ptr<SourceState> state, PacketLayout layout)
        : mode_(mode), observed_(std::move(state)), layout_(layout), bytes_(test_stream(layout)) {
        std::array<std::uint32_t, 3> format{layout.offset, layout.packet_length, layout.stride};
        AM_MEDIA_TYPE type{};
        type.majortype = MEDIATYPE_Stream;
        type.subtype = MEDIASUBTYPE_MPEG2_TRANSPORT_STRIDE;
        type.formattype = FORMAT_None;
        type.bFixedSizeSamples = TRUE;
        type.lSampleSize = layout.stride * 256;
        type.cbFormat = static_cast<ULONG>(sizeof(format));
        type.pbFormat = reinterpret_cast<BYTE*>(format.data());
        type_ = MediaType(type);
    }
    ~SyntheticSource() {
        force_stop();
        observed_->destroyed.store(true);
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IPersist || iid == IID_IMediaFilter || iid == IID_IBaseFilter)
            *result = static_cast<IBaseFilter*>(this);
        else if (iid == IID_IPin) *result = static_cast<IPin*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG left = --references_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE GetClassID(CLSID* result) override {
        if (!result) return E_POINTER;
        *result = GUID{0x5602c909,0x283f,0x4f56,{0x9b,0x57,0x16,0x7f,0x09,0xdf,0x9d,0xc2}}; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Stop() override {
        if (mode_ == Mode::stop_failure && observed_->started.load() && !observed_->first_stop_failed.exchange(true))
            return E_FAIL;
        force_stop(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Pause() override { std::lock_guard lock(mutex_); state_ = State_Paused; return S_OK; }
    HRESULT STDMETHODCALLTYPE Run(REFERENCE_TIME) override {
        try {
            std::lock_guard lock(mutex_);
            state_ = State_Running;
            if (!worker_.joinable()) {
                stopping_.store(false);
                observed_->started.store(true);
                worker_ = std::thread([this] { produce(); });
            }
            return S_OK;
        } catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE GetState(DWORD, FILTER_STATE* result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); *result = state_; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSyncSource(IReferenceClock* clock) override { std::lock_guard lock(mutex_); clock_ = clock; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetSyncSource(IReferenceClock** result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); return clock_.CopyTo(result);
    }
    HRESULT STDMETHODCALLTYPE EnumPins(IEnumPins** result) override {
        if (!result) return E_POINTER;
        *result = new (std::nothrow) PinEnumerator(static_cast<IPin*>(this));
        return *result ? S_OK : E_OUTOFMEMORY;
    }
    HRESULT STDMETHODCALLTYPE FindPin(LPCWSTR id, IPin** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (!id) return E_POINTER;
        if (std::wcscmp(id, L"Output") != 0) return VFW_E_NOT_FOUND;
        *result = static_cast<IPin*>(this); AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryFilterInfo(FILTER_INFO* result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_);
        *result = {}; std::wmemcpy(result->achName, name_, std::wcslen(name_));
        result->pGraph = graph_; if (graph_) graph_->AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE JoinFilterGraph(IFilterGraph* graph, LPCWSTR name) override {
        std::lock_guard lock(mutex_); graph_ = graph;
        std::wmemset(name_, 0, MAX_FILTER_NAME);
        if (name) std::wmemcpy(name_, name, std::min<std::size_t>(std::wcslen(name), MAX_FILTER_NAME - 1));
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryVendorInfo(LPWSTR* result) override { if (!result) return E_POINTER; *result = nullptr; return E_NOTIMPL; }

    HRESULT STDMETHODCALLTYPE Connect(IPin* receiver, const AM_MEDIA_TYPE* type) override {
        if (!receiver) return E_POINTER;
        if (mode_ == Mode::pin_in_use) return HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES);
        std::lock_guard lock(mutex_);
        if (peer_) return VFW_E_ALREADY_CONNECTED;
        if (state_ != State_Stopped) return VFW_E_NOT_STOPPED;
        const AM_MEDIA_TYPE& selected = type ? *type : type_.value;
        PacketLayout layout;
        if (!media_layout(selected, layout) || layout != layout_) return VFW_E_TYPE_NOT_ACCEPTED;
        ComPtr<IMemInputPin> input;
        HRESULT status = receiver->QueryInterface(IID_PPV_ARGS(&input));
        if (FAILED(status)) return status;
        status = receiver->ReceiveConnection(static_cast<IPin*>(this), &selected);
        if (FAILED(status)) return status;
        ComPtr<IMemAllocator> allocator;
        status = input->GetAllocator(&allocator);
        ALLOCATOR_PROPERTIES requested{4, 4096, 1, 0}, actual{};
        if (SUCCEEDED(status)) status = allocator->SetProperties(&requested, &actual);
        if (SUCCEEDED(status) && actual.cbBuffer < 4096) status = E_FAIL;
        if (SUCCEEDED(status)) status = input->NotifyAllocator(allocator.Get(), TRUE);
        if (SUCCEEDED(status)) status = allocator->Commit();
        if (FAILED(status)) { receiver->Disconnect(); return status; }
        input_ = std::move(input); allocator_ = std::move(allocator); peer_ = receiver;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ReceiveConnection(IPin*, const AM_MEDIA_TYPE*) override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE Disconnect() override {
        force_stop();
        std::lock_guard lock(mutex_);
        const bool connected = !!peer_;
        input_.Reset(); peer_.Reset(); allocator_.Reset();
        return connected ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE ConnectedTo(IPin** result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); *result = nullptr;
        return peer_ ? peer_.CopyTo(result) : VFW_E_NOT_CONNECTED;
    }
    HRESULT STDMETHODCALLTYPE ConnectionMediaType(AM_MEDIA_TYPE* result) override {
        if (!result) return E_POINTER;
        *result = {};
        try {
            std::lock_guard lock(mutex_);
            if (!peer_) return VFW_E_NOT_CONNECTED;
            MediaType copy(type_); *result = std::exchange(copy.value, AM_MEDIA_TYPE{}); return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE QueryPinInfo(PIN_INFO* result) override {
        if (!result) return E_POINTER;
        *result = {}; result->pFilter = static_cast<IBaseFilter*>(this); result->dir = PINDIR_OUTPUT;
        std::wmemcpy(result->achName, L"Output", 7); AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryDirection(PIN_DIRECTION* result) override { if (!result) return E_POINTER; *result = PINDIR_OUTPUT; return S_OK; }
    HRESULT STDMETHODCALLTYPE QueryId(LPWSTR* result) override {
        if (!result) return E_POINTER;
        *result = static_cast<LPWSTR>(CoTaskMemAlloc(sizeof(L"Output")));
        if (!*result) return E_OUTOFMEMORY;
        std::memcpy(*result, L"Output", sizeof(L"Output")); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryAccept(const AM_MEDIA_TYPE* type) override {
        if (!type) return E_POINTER;
        PacketLayout layout;
        return media_layout(*type, layout) && layout == layout_ ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE EnumMediaTypes(IEnumMediaTypes** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        try { *result = new TypeEnumerator(type_.value); return S_OK; }
        catch (...) { return E_OUTOFMEMORY; }
    }
    HRESULT STDMETHODCALLTYPE QueryInternalConnections(IPin**, ULONG*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EndOfStream() override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE BeginFlush() override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE EndFlush() override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override { return E_UNEXPECTED; }

private:
    void force_stop() noexcept {
        stopping_.store(true);
        if (worker_.joinable()) worker_.join();
        std::lock_guard lock(mutex_); state_ = State_Stopped;
        if (allocator_) allocator_->Decommit();
    }
    HRESULT send(IMemInputPin* input, IMemAllocator* allocator, std::span<const std::uint8_t> data, bool changed = false) {
        ComPtr<IMediaSample> sample;
        HRESULT status = allocator->GetBuffer(&sample, nullptr, nullptr, 0);
        if (FAILED(status)) return status;
        BYTE* buffer = nullptr;
        status = sample->GetPointer(&buffer);
        if (FAILED(status)) return status;
        if (data.size() > static_cast<std::size_t>(sample->GetSize())) return E_INVALIDARG;
        std::memcpy(buffer, data.data(), data.size());
        sample->SetActualDataLength(static_cast<LONG>(data.size()));
        sample->SetDiscontinuity(FALSE);
        sample->SetMediaType(nullptr);
        if (changed) {
            auto different = type_.value; ++different.lSampleSize;
            sample->SetMediaType(&different);
        }
        return input->Receive(sample.Get());
    }
    void produce() noexcept {
        try {
            ComPtr<IMemInputPin> input;
            ComPtr<IMemAllocator> allocator;
            ComPtr<IPin> peer;
            {
                std::lock_guard lock(mutex_);
                input = input_; allocator = allocator_; peer = peer_;
            }
            if (!input || !allocator || !peer) return;
            if (mode_ == Mode::no_data) {
                while (!stopping_.load()) std::this_thread::sleep_for(5ms);
                return;
            }
            if (mode_ == Mode::dynamic_change) {
                if (send(input.Get(), allocator.Get(), std::span(bytes_).first(192)) == S_OK)
                    send(input.Get(), allocator.Get(), std::span(bytes_).subspan(192, 192), true);
                return;
            }
            // Deliberately split transport packets across IMediaSample boundaries.
            constexpr std::array<std::size_t, 5> chunks{37, 151, 401, 13, 1024};
            std::size_t offset = 0, index = 0;
            while (offset < bytes_.size() && !stopping_.load()) {
                const auto count = std::min(chunks[index++ % chunks.size()], bytes_.size() - offset);
                if (send(input.Get(), allocator.Get(), std::span(bytes_).subspan(offset, count)) != S_OK) return;
                offset += count;
            }
            observed_->produced.store(offset == bytes_.size());
            if (mode_ == Mode::eos) { peer->EndOfStream(); return; }
            if (mode_ == Mode::stop_failure) {
                while (!observed_->first_stop_failed.load() && !stopping_.load()) std::this_thread::sleep_for(1ms);
                if (observed_->first_stop_failed.load()) {
                    // Even a failed Stop may leave a driver delivering. Keep the
                    // sink reference alive and attempt delivery during teardown.
                    std::this_thread::sleep_for(30ms);
                    observed_->late_status.store(send(input.Get(), allocator.Get(), std::span(bytes_).first(192)));
                    observed_->late_attempted.store(true);
                }
                return;
            }
            while (!stopping_.load()) std::this_thread::sleep_for(5ms);
        } catch (...) {}
    }

    std::atomic<ULONG> references_{1};
    std::mutex mutex_;
    FILTER_STATE state_ = State_Stopped;
    Mode mode_;
    std::shared_ptr<SourceState> observed_;
    PacketLayout layout_;
    MediaType type_;
    std::vector<std::uint8_t> bytes_;
    IFilterGraph* graph_ = nullptr;
    wchar_t name_[MAX_FILTER_NAME] = L"Synthetic D-VHS source";
    ComPtr<IReferenceClock> clock_;
    ComPtr<IPin> peer_;
    ComPtr<IMemInputPin> input_;
    ComPtr<IMemAllocator> allocator_;
    std::atomic<bool> stopping_{false};
    std::thread worker_;
};

CaptureSourceFactory factory(Mode mode, const std::shared_ptr<SourceState>& state,
                             PacketLayout layout = {4, 188, 192}) {
    return [mode, state, layout] {
        CaptureSource source;
        source.device.id = "0123456789abcdef";
        source.device.name = "Synthetic capture integration test";
        source.device.description = "Offline generated source; no hardware";
        source.filter.Attach(new SyntheticSource(mode, state, layout));
        return source;
    };
}

std::vector<std::uint8_t> read_bytes(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    CHECK(input.good());
    return std::vector<std::uint8_t>(std::istreambuf_iterator<char>(input), {});
}

std::string read_report(const std::filesystem::path& output) {
    const auto path = std::filesystem::path(output.native() + L".report.json");
    const auto bytes = read_bytes(path);
    const std::string text(bytes.begin(), bytes.end());
    CHECK(text.find("\"source_kind\":\"injected_test_source\"") != std::string::npos);
    CHECK(text.ends_with("\r\n"));
    return text;
}

std::uint64_t number(const std::string& json, const std::string& key) {
    const auto start = json.find('"' + key + "\":");
    CHECK(start != std::string::npos);
    return std::stoull(json.substr(start + key.size() + 3));
}

void expect(const std::string& report, const std::string& fragment) { CHECK(report.find(fragment) != std::string::npos); }

void verify_sink_diagnostics(const std::string& report) {
    const auto calls = number(report, "sink_receive_calls");
    CHECK(calls >= number(report, "received_samples"));
    CHECK(number(report, "sink_zero_length_samples") == 0);
    CHECK(number(report, "sink_rejected_samples") <= calls);
}

void verify_running_startup(const std::string& report, bool data_received) {
    expect(report, "\"startup_state\":{");
    CHECK(number(report, "graph_state") == State_Running);
    CHECK(number(report, "source_state") == State_Running);
    CHECK(number(report, "sink_state") == State_Running);
    expect(report, data_received ? "\"data_received\":true" : "\"data_received\":false");
    verify_sink_diagnostics(report);
}

CaptureOptions options(const std::filesystem::path& folder, const wchar_t* name) {
    CaptureOptions result;
    result.output = folder / name;
    result.no_data_timeout = 2;
    return result;
}

void verify_success(const CaptureOptions& selected, const std::vector<std::uint8_t>& expected,
                    const std::string& reason, const std::shared_ptr<SourceState>& state) {
    CHECK(state->destroyed.load());
    CHECK(read_bytes(selected.output) == expected);
    CHECK(!std::filesystem::exists(selected.output.native() + L".partial"));
    const auto report = read_report(selected.output);
    verify_running_startup(report, true);
    expect(report, "\"success\":true");
    expect(report, "\"data_finalized\":true");
    expect(report, "\"analysis_complete\":true");
    expect(report, "\"stop_reason\":\"" + reason + '"');
    CHECK(number(report, "exit_code") == 0);
    CHECK(number(report, "received_bytes") == expected.size());
    CHECK(number(report, "written_bytes") == expected.size());
    CHECK(number(report, "queue_remaining_bytes") == 0);
    CHECK(number(report, "not_queued_bytes") == 0);
    CHECK(number(report, "packets") == 12);
}

void test_raw_eos(const std::filesystem::path& folder) {
    auto selected = options(folder, L"raw-eos.ts");
    auto state = std::make_shared<SourceState>();
    std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::eos, state)) == 0);
    verify_success(selected, test_stream(), "end_of_stream", state);
}

void test_ts188_eos(const std::filesystem::path& folder) {
    constexpr PacketLayout native188{0, 188, 188};
    auto selected = options(folder, L"native-eos.ts");
    selected.format = TsFormat::ts188;
    auto state = std::make_shared<SourceState>();
    std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::eos, state, native188)) == 0);
    verify_success(selected, test_stream(native188), "end_of_stream", state);
    expect(read_report(selected.output), "\"output_format\":\"ts188\"");

    selected.output = folder / L"unsupported-188.ts";
    CHECK(run_capture(selected, cancel, factory(Mode::eos, std::make_shared<SourceState>())) == 4);
    CHECK(!std::filesystem::exists(selected.output));
    expect(read_report(selected.output), "\"output_format\":null");
}

void test_duration_and_cancel(const std::filesystem::path& folder) {
    auto selected = options(folder, L"duration.ts"); selected.duration = 0.1;
    auto state = std::make_shared<SourceState>(); std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::hold, state)) == 0);
    verify_success(selected, test_stream(), "duration", state);

    selected = options(folder, L"cancel.ts"); state = std::make_shared<SourceState>();
    std::thread canceller([state, &cancel] {
        const auto deadline = std::chrono::steady_clock::now() + 5s;
        while (!state->produced.load() && std::chrono::steady_clock::now() < deadline) std::this_thread::sleep_for(1ms);
        cancel.store(true);
    });
    const int status = run_capture(selected, cancel, factory(Mode::hold, state));
    canceller.join();
    CHECK(status == 0);
    verify_success(selected, test_stream(), "ctrl_c", state);
}

void test_no_data_timeout(const std::filesystem::path& folder) {
    auto selected = options(folder, L"no-data.ts"); selected.no_data_timeout = 0.15;
    auto state = std::make_shared<SourceState>(); std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::no_data, state)) == 7);
    CHECK(state->destroyed.load());
    CHECK(!std::filesystem::exists(selected.output));
    CHECK(read_bytes(selected.output.native() + L".partial").empty());
    const auto report = read_report(selected.output);
    verify_running_startup(report, false);
    CHECK(number(report, "sink_receive_calls") == 0);
    CHECK(number(report, "sink_rejected_samples") == 0);
    expect(report, "\"success\":false"); expect(report, "\"data_finalized\":false");
    CHECK(number(report, "exit_code") == 7 && number(report, "received_bytes") == 0);
}

void test_dynamic_media_failure(const std::filesystem::path& folder) {
    auto selected = options(folder, L"dynamic-media.ts");
    auto state = std::make_shared<SourceState>(); std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::dynamic_change, state)) == 6);
    CHECK(state->destroyed.load() && !std::filesystem::exists(selected.output));
    const auto raw = test_stream();
    CHECK(read_bytes(selected.output.native() + L".partial") == std::vector<std::uint8_t>(raw.begin(), raw.begin() + 192));
    const auto report = read_report(selected.output);
    verify_running_startup(report, true);
    CHECK(number(report, "sink_rejected_samples") >= 1);
    expect(report, "Capture media type changed during recording");
    expect(report, "\"success\":false"); expect(report, "\"analysis_complete\":false");
    CHECK(number(report, "exit_code") == 6 && number(report, "received_bytes") == 192);
}

void test_failed_stop_and_late_delivery(const std::filesystem::path& folder) {
    auto selected = options(folder, L"failed-stop.ts"); selected.duration = 0.1;
    auto state = std::make_shared<SourceState>(); std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::stop_failure, state)) == 6);
    CHECK(state->destroyed.load() && state->first_stop_failed.load() && state->late_attempted.load());
    const HRESULT late_status = state->late_status.load();
    CHECK(late_status == VFW_E_WRONG_STATE || late_status == VFW_E_NOT_CONNECTED);
    CHECK(!std::filesystem::exists(selected.output));
    CHECK(read_bytes(selected.output.native() + L".partial") == test_stream());
    const auto report = read_report(selected.output);
    verify_running_startup(report, true);
    expect(report, "Stop capture graph failed"); expect(report, "\"data_finalized\":false");
    CHECK(number(report, "exit_code") == 6);
    CHECK(number(report, "received_bytes") == test_stream().size());
    CHECK(number(report, "queue_remaining_bytes") == 0);
}

void test_pin_in_use_failure(const std::filesystem::path& folder) {
    auto selected = options(folder, L"pin-in-use.ts");
    auto state = std::make_shared<SourceState>(); std::atomic<bool> cancel{false};
    CHECK(run_capture(selected, cancel, factory(Mode::pin_in_use, state)) == 3);
    CHECK(state->destroyed.load() && !state->started.load());
    CHECK(!std::filesystem::exists(selected.output));
    CHECK(read_bytes(selected.output.native() + L".partial").empty());
    const auto report = read_report(selected.output);
    expect(report, "Close other capture applications completely, including CapDVHS");
    expect(report, "pressing Stop in CapDVHS can leave its device connection open");
    expect(report, "\"startup_state\":null");
    expect(report, "\"stream_started\":false");
    expect(report, "\"data_received\":false");
    expect(report, "\"data_file_created\":true");
    expect(report, "\"data_finalized\":false");
    CHECK(number(report, "exit_code") == 3);
    CHECK(number(report, "received_bytes") == 0 && number(report, "written_bytes") == 0);
    verify_sink_diagnostics(report);
    CHECK(number(report, "sink_receive_calls") == 0 && number(report, "sink_rejected_samples") == 0);
}

} // namespace

int main(int argc, char** argv) {
    try {
        CHECK(argc == 2);
        const auto folder = std::filesystem::absolute(std::filesystem::path(argv[1])) /
            ("capture-tests-" + std::to_string(GetCurrentProcessId()) + '-' + std::to_string(GetTickCount64()));
        CHECK(std::filesystem::create_directories(folder));
        test_raw_eos(folder);
        test_ts188_eos(folder);
        test_duration_and_cancel(folder);
        test_no_data_timeout(folder);
        test_dynamic_media_failure(folder);
        test_failed_stop_and_late_delivery(folder);
        test_pin_in_use_failure(folder);
        std::cout << "7 capture integration groups passed (synthetic DirectShow source; no hardware)\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
