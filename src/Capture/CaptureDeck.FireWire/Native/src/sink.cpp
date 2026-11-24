#include "capdvhs/directshow.hpp"
#include "capdvhs/util.hpp"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwchar>
#include <memory>
#include <mutex>
#include <new>
#include <sstream>
#include <stdexcept>
#include <utility>

namespace capdvhs {
namespace {

template<class BuildMessage>
void sink_trace(BuildMessage&& build_message) noexcept {
    if (!trace_enabled()) return;
    try { trace_event(build_message()); }
    catch (...) { /* Diagnostics must not change a COM method's result. */ }
}

void trace_allocator(std::string_view operation, IMemAllocator* allocator,
                     std::string_view origin, int read_only = -1) noexcept {
    if (!trace_enabled()) return;
    sink_trace([&] {
        ALLOCATOR_PROPERTIES properties{};
        const HRESULT status = allocator ? allocator->GetProperties(&properties) : E_POINTER;
        std::ostringstream message;
        message << "sink." << operation << " allocator=" << static_cast<void*>(allocator)
                << " origin=" << origin << " GetProperties=" << hresult_string(status);
        if (read_only >= 0) message << " read_only=" << read_only;
        if (SUCCEEDED(status)) {
            message << " cBuffers=" << properties.cBuffers << " cbBuffer=" << properties.cbBuffer
                    << " cbAlign=" << properties.cbAlign << " cbPrefix=" << properties.cbPrefix;
        }
        if (allocator) {
            ComPtr<IPersist> persist;
            const HRESULT query = allocator->QueryInterface(IID_PPV_ARGS(&persist));
            message << " IPersist=" << hresult_string(query);
            if (SUCCEEDED(query) && persist) {
                CLSID class_id{};
                const HRESULT class_result = persist->GetClassID(&class_id);
                message << " GetClassID=" << hresult_string(class_result);
                if (SUCCEEDED(class_result)) message << " class_id=" << guid_string(class_id);
            }
        }
        return message.str();
    });
}

void clear_media_type(AM_MEDIA_TYPE& type) noexcept {
    CoTaskMemFree(type.pbFormat);
    if (type.pUnk) type.pUnk->Release();
    type = {};
}

struct FreeMediaType {
    void operator()(AM_MEDIA_TYPE* type) const noexcept {
        if (type) {
            clear_media_type(*type);
            CoTaskMemFree(type);
        }
    }
};

using MediaTypePointer = std::unique_ptr<AM_MEDIA_TYPE, FreeMediaType>;

bool same_type(const AM_MEDIA_TYPE& left, const AM_MEDIA_TYPE& right) noexcept {
    return left.majortype == right.majortype && left.subtype == right.subtype &&
           left.formattype == right.formattype && left.bFixedSizeSamples == right.bFixedSizeSamples &&
           left.bTemporalCompression == right.bTemporalCompression &&
           left.lSampleSize == right.lSampleSize && left.cbFormat == right.cbFormat &&
           left.pUnk == right.pUnk &&
           (!left.cbFormat || (left.pbFormat && right.pbFormat &&
                              std::memcmp(left.pbFormat, right.pbFormat, left.cbFormat) == 0));
}

HRESULT copy_type(AM_MEDIA_TYPE* destination, const AM_MEDIA_TYPE& source) noexcept {
    if (!destination) return E_POINTER;
    *destination = source;
    destination->pbFormat = nullptr;
    destination->pUnk = nullptr;
    if (source.cbFormat) {
        if (!source.pbFormat) { *destination = {}; return E_INVALIDARG; }
        destination->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(source.cbFormat));
        if (!destination->pbFormat) { *destination = {}; return E_OUTOFMEMORY; }
        std::memcpy(destination->pbFormat, source.pbFormat, source.cbFormat);
    }
    destination->pUnk = source.pUnk;
    if (destination->pUnk) destination->pUnk->AddRef();
    return S_OK;
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
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }
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
        const ULONG available = position_ ? 0 : 1;
        position_ = 1;
        return count <= available ? S_OK : S_FALSE;
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
    ULONG position_ = 0;
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
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, AM_MEDIA_TYPE** types, ULONG* fetched) override {
        if (!types || (count != 1 && !fetched)) return E_POINTER;
        if (fetched) *fetched = 0;
        if (!count) return S_OK;
        if (position_) return S_FALSE;
        types[0] = nullptr;
        MediaTypePointer type(static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE))));
        if (!type) return E_OUTOFMEMORY;
        *type = {};
        const HRESULT status = copy_type(type.get(), type_.value);
        if (FAILED(status)) return status;
        types[0] = type.release(); position_ = 1;
        if (fetched) *fetched = 1;
        return count == 1 ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override {
        if (!count) return S_OK;
        const ULONG available = position_ ? 0 : 1;
        position_ = 1;
        return count <= available ? S_OK : S_FALSE;
    }
    HRESULT STDMETHODCALLTYPE Reset() override { position_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumMediaTypes** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        try { *result = new TypeEnumerator(type_.value, position_); return S_OK; }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
private:
    std::atomic<ULONG> references_{1};
    MediaType type_;
    ULONG position_ = 0;
};

// The filter and its sole input pin share one COM lifetime. Enumerators and
// returned pin pointers keep the filter alive, without an internal reference cycle.
class CaptureSink final : public IBaseFilter, public IPin, public IMemInputPin, public IAMFilterMiscFlags {
public:
    CaptureSink(const AM_MEDIA_TYPE& expected, SampleHandler on_sample,
                StreamErrorHandler on_error, EndOfStreamHandler on_eos,
                std::shared_ptr<SinkDiagnostics> diagnostics)
        : expected_(expected), on_sample_(std::move(on_sample)),
          on_error_(std::move(on_error)), on_eos_(std::move(on_eos)),
          diagnostics_(std::move(diagnostics)) {
        if (!media_layout(expected, layout_)) throw std::invalid_argument("Unsupported capture media type");
        if (!on_sample_) throw std::invalid_argument("Capture sample handler is required");
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IPersist || iid == IID_IMediaFilter || iid == IID_IBaseFilter)
            *result = static_cast<IBaseFilter*>(this);
        else if (iid == IID_IPin) *result = static_cast<IPin*>(this);
        else if (iid == IID_IMemInputPin) *result = static_cast<IMemInputPin*>(this);
        else if (iid == IID_IAMFilterMiscFlags) *result = static_cast<IAMFilterMiscFlags*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining = --references_;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetClassID(CLSID* result) override {
        if (!result) return E_POINTER;
        static constexpr CLSID identifier{0x4a4fc139, 0x7958, 0x4eb7, {0xb4,0x9a,0x66,0x7c,0x3a,0x6e,0x7b,0x0b}};
        *result = identifier; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Stop() override {
        std::lock_guard lock(mutex_);
        sink_trace([&] { return "sink.Stop previous_state=" + std::to_string(state_) +
                               " traced_receive_calls=" + std::to_string(receive_trace_count_.load()); });
        state_ = State_Stopped; flushing_ = false; ended_ = false;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Pause() override {
        std::lock_guard lock(mutex_);
        sink_trace([&] { return "sink.Pause previous_state=" + std::to_string(state_); });
        if (state_ == State_Stopped) ended_ = false;
        state_ = State_Paused; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Run(REFERENCE_TIME) override {
        std::lock_guard lock(mutex_);
        sink_trace([&] { return "sink.Run previous_state=" + std::to_string(state_); });
        if (state_ == State_Stopped) ended_ = false;
        state_ = State_Running; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(DWORD, FILTER_STATE* result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); *result = state_; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetSyncSource(IReferenceClock* clock) override {
        std::lock_guard lock(mutex_);
        sink_trace([&] { return std::string("sink.SetSyncSource clock=") + (clock ? "non_null" : "null"); });
        clock_ = clock; return S_OK;
    }
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
        if (std::wcscmp(id, L"Input") != 0) return VFW_E_NOT_FOUND;
        *result = static_cast<IPin*>(this); AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryFilterInfo(FILTER_INFO* result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_);
        *result = {};
        std::wmemcpy(result->achName, name_, std::wcslen(name_));
        result->pGraph = graph_;
        if (graph_) graph_->AddRef();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE JoinFilterGraph(IFilterGraph* graph, LPCWSTR name) override {
        std::lock_guard lock(mutex_);
        graph_ = graph;
        std::wmemset(name_, 0, MAX_FILTER_NAME);
        if (name) {
            const auto length = std::min<std::size_t>(std::wcslen(name), MAX_FILTER_NAME - 1);
            std::wmemcpy(name_, name, length);
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryVendorInfo(LPWSTR* result) override {
        if (!result) return E_POINTER;
        *result = nullptr; return E_NOTIMPL;
    }
    ULONG STDMETHODCALLTYPE GetMiscFlags() override { return AM_FILTER_MISC_FLAGS_IS_RENDERER; }

    HRESULT STDMETHODCALLTYPE Connect(IPin*, const AM_MEDIA_TYPE*) override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE ReceiveConnection(IPin* connector, const AM_MEDIA_TYPE* type) override {
        if (!connector || !type) return E_POINTER;
        try {
            std::lock_guard lock(mutex_);
            if (state_ != State_Stopped) return VFW_E_NOT_STOPPED;
            if (peer_) return VFW_E_ALREADY_CONNECTED;
            PIN_DIRECTION direction{};
            const HRESULT status = connector->QueryDirection(&direction);
            if (FAILED(status)) return status;
            if (direction != PINDIR_OUTPUT) return VFW_E_INVALID_DIRECTION;
            if (!accepts(*type)) return VFW_E_TYPE_NOT_ACCEPTED;
            connection_ = MediaType(*type); peer_ = connector;
            flushing_ = false; ended_ = false;
            return S_OK;
        } catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE Disconnect() override {
        std::lock_guard lock(mutex_);
        if (state_ != State_Stopped) return VFW_E_NOT_STOPPED;
        if (!peer_) return S_FALSE;
        peer_.Reset(); allocator_.Reset(); connection_ = MediaType();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ConnectedTo(IPin** result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); *result = nullptr;
        if (!peer_) return VFW_E_NOT_CONNECTED;
        return peer_.CopyTo(result);
    }
    HRESULT STDMETHODCALLTYPE ConnectionMediaType(AM_MEDIA_TYPE* result) override {
        if (!result) return E_POINTER;
        std::lock_guard lock(mutex_); *result = {};
        return peer_ ? copy_type(result, connection_.value) : VFW_E_NOT_CONNECTED;
    }
    HRESULT STDMETHODCALLTYPE QueryPinInfo(PIN_INFO* result) override {
        if (!result) return E_POINTER;
        *result = {}; result->pFilter = static_cast<IBaseFilter*>(this);
        result->dir = PINDIR_INPUT; std::wmemcpy(result->achName, L"Input", 6); AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryDirection(PIN_DIRECTION* result) override {
        if (!result) return E_POINTER;
        *result = PINDIR_INPUT; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryId(LPWSTR* result) override {
        if (!result) return E_POINTER;
        *result = static_cast<LPWSTR>(CoTaskMemAlloc(sizeof(L"Input")));
        if (!*result) return E_OUTOFMEMORY;
        std::memcpy(*result, L"Input", sizeof(L"Input")); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE QueryAccept(const AM_MEDIA_TYPE* type) override {
        if (!type) return E_POINTER;
        try { return accepts(*type) ? S_OK : S_FALSE; }
        catch (...) { return S_FALSE; }
    }
    HRESULT STDMETHODCALLTYPE EnumMediaTypes(IEnumMediaTypes** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        try { *result = new TypeEnumerator(expected_.value); return S_OK; }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE QueryInternalConnections(IPin**, ULONG*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EndOfStream() override {
        std::lock_guard lock(mutex_);
        if (!peer_) return VFW_E_NOT_CONNECTED;
        if (state_ == State_Stopped) return VFW_E_WRONG_STATE;
        if (flushing_) return S_FALSE;
        if (ended_) return S_OK;
        ended_ = true;
        try { if (on_eos_) on_eos_(); return S_OK; }
        catch (...) { report_error("End-of-stream callback failed"); return E_FAIL; }
    }
    HRESULT STDMETHODCALLTYPE BeginFlush() override {
        std::lock_guard lock(mutex_); flushing_ = true; ended_ = false; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EndFlush() override {
        std::lock_guard lock(mutex_); flushing_ = false; ended_ = false; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override {
        std::lock_guard lock(mutex_); ended_ = false; return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetAllocator(IMemAllocator** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        std::lock_guard lock(mutex_);
        const bool created = !allocator_;
        if (!allocator_) {
            const HRESULT status = CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
                                                    IID_PPV_ARGS(allocator_.GetAddressOf()));
            if (FAILED(status)) {
                sink_trace([&] { return "sink.GetAllocator create_CLSID_MemoryAllocator=" + hresult_string(status); });
                return status;
            }
        }
        trace_allocator("GetAllocator", allocator_.Get(), created ? "new_CLSID_MemoryAllocator" : "existing_allocator");
        return allocator_.CopyTo(result);
    }
    HRESULT STDMETHODCALLTYPE NotifyAllocator(IMemAllocator* allocator, BOOL read_only) override {
        if (!allocator) return E_POINTER;
        std::lock_guard lock(mutex_);
        const bool same = allocator_.Get() == allocator;
        allocator_ = allocator;
        trace_allocator("NotifyAllocator", allocator, same ? "previously_offered_allocator" : "upstream_chosen_allocator",
                        read_only ? 1 : 0);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetAllocatorRequirements(ALLOCATOR_PROPERTIES* result) override {
        if (!result) return E_POINTER;
        sink_trace([] { return std::string("sink.GetAllocatorRequirements result=E_NOTIMPL"); });
        *result = {}; return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE Receive(IMediaSample* sample) override {
        if (diagnostics_) diagnostics_->receive_calls.fetch_add(1, std::memory_order_relaxed);
        const auto trace_index = trace_enabled() ? receive_trace_count_.fetch_add(1) : 10;
        auto traced_result = [&](HRESULT result) noexcept {
            if (diagnostics_ && result != S_OK)
                diagnostics_->rejected_samples.fetch_add(1, std::memory_order_relaxed);
            if (trace_index < 10) {
                sink_trace([&] { return "sink.Receive index=" + std::to_string(trace_index + 1) +
                                       " result=" + hresult_string(result); });
            }
            return result;
        };
        if (!sample) {
            if (trace_index < 10) {
                sink_trace([&] { return "sink.Receive index=" + std::to_string(trace_index + 1) + " sample=null"; });
            }
            return traced_result(E_POINTER);
        }
        // Callbacks must copy/enqueue without waiting for the graph thread. The
        // recursive lock also permits a callback to inspect or stop this sink.
        std::lock_guard lock(mutex_);
        if (trace_index < 10) {
            sink_trace([&] {
                return "sink.Receive index=" + std::to_string(trace_index + 1) +
                       " state=" + std::to_string(state_) + " connected=" + (peer_ ? "1" : "0") +
                       " flushing=" + (flushing_ ? "1" : "0") + " ended=" + (ended_ ? "1" : "0") +
                       " length=" + std::to_string(sample->GetActualDataLength()) +
                       " capacity=" + std::to_string(sample->GetSize());
            });
        }
        if (!peer_) return traced_result(VFW_E_NOT_CONNECTED);
        if (state_ == State_Stopped) return traced_result(VFW_E_WRONG_STATE);
        if (flushing_) return traced_result(S_FALSE);
        if (ended_) return traced_result(VFW_E_SAMPLE_REJECTED_EOS);
        try {
            AM_MEDIA_TYPE* dynamic_raw = nullptr;
            const HRESULT dynamic_status = sample->GetMediaType(&dynamic_raw);
            MediaTypePointer dynamic(dynamic_raw);
            if (FAILED(dynamic_status)) return traced_result(sample_error("Cannot read sample media type", dynamic_status));
            if ((dynamic_status == S_OK && !dynamic) || (dynamic && !same_type(*dynamic, connection_.value)))
                return traced_result(sample_error("Capture media type changed during recording", VFW_E_TYPE_NOT_ACCEPTED));
            const LONG length = sample->GetActualDataLength();
            const LONG capacity = sample->GetSize();
            if (length < 0 || capacity < 0 || length > capacity)
                return traced_result(sample_error("Invalid capture sample length", E_INVALIDARG));
            if (!length) {
                if (diagnostics_) diagnostics_->zero_length_samples.fetch_add(1, std::memory_order_relaxed);
                return traced_result(S_OK);
            }
            BYTE* bytes = nullptr;
            const HRESULT pointer_status = sample->GetPointer(&bytes);
            if (FAILED(pointer_status)) return traced_result(sample_error("Cannot access capture sample", pointer_status));
            if (!bytes) return traced_result(sample_error("Capture sample has a null buffer", E_POINTER));
            const HRESULT discontinuity = sample->IsDiscontinuity();
            if (FAILED(discontinuity)) return traced_result(sample_error("Cannot read capture discontinuity flag", discontinuity));
            const HRESULT status = on_sample_(std::span<const std::uint8_t>(bytes, static_cast<std::size_t>(length)),
                                              discontinuity == S_OK);
            if (FAILED(status)) report_error("Capture sample queue rejected data");
            return traced_result(status);
        } catch (const std::bad_alloc&) { return traced_result(sample_error("Capture sample allocation failed", E_OUTOFMEMORY)); }
        catch (...) { return traced_result(sample_error("Capture sample callback failed", E_FAIL)); }
    }
    HRESULT STDMETHODCALLTYPE ReceiveMultiple(IMediaSample** samples, LONG count, LONG* processed) override {
        if (!processed) return E_POINTER;
        *processed = 0;
        if (count < 0) return E_INVALIDARG;
        if (count && !samples) return E_POINTER;
        for (LONG index = 0; index < count; ++index) {
            const HRESULT status = Receive(samples[index]);
            if (status != S_OK) return status;
            ++*processed;
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE ReceiveCanBlock() override { return S_OK; }

private:
    bool accepts(const AM_MEDIA_TYPE& type) const {
        PacketLayout layout;
        return same_type(type, expected_.value) && media_layout(type, layout) && layout == layout_;
    }
    void report_error(const char* message) noexcept {
        try { if (on_error_) on_error_(message); } catch (...) {}
    }
    HRESULT sample_error(const char* message, HRESULT result) noexcept { report_error(message); return result; }

    std::atomic<ULONG> references_{1};
    std::atomic<std::uint64_t> receive_trace_count_{0};
    std::recursive_mutex mutex_;
    FILTER_STATE state_ = State_Stopped;
    bool flushing_ = false;
    bool ended_ = false;
    IFilterGraph* graph_ = nullptr; // The graph owns the filter; this pointer is weak.
    wchar_t name_[MAX_FILTER_NAME] = L"CaptureDeck FireWire sink";
    MediaType expected_;
    PacketLayout layout_;
    MediaType connection_;
    ComPtr<IPin> peer_;
    ComPtr<IMemAllocator> allocator_;
    ComPtr<IReferenceClock> clock_;
    SampleHandler on_sample_;
    StreamErrorHandler on_error_;
    EndOfStreamHandler on_eos_;
    std::shared_ptr<SinkDiagnostics> diagnostics_;
};

} // namespace

ComPtr<IBaseFilter> create_sink(const AM_MEDIA_TYPE& expected, SampleHandler on_sample,
                               StreamErrorHandler on_error, EndOfStreamHandler on_eos,
                               std::shared_ptr<SinkDiagnostics> diagnostics) {
    ComPtr<IBaseFilter> result;
    result.Attach(new CaptureSink(expected, std::move(on_sample), std::move(on_error), std::move(on_eos),
                                  std::move(diagnostics)));
    return result;
}

} // namespace capdvhs
