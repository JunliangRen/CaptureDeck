#include "capdvhs/directshow.hpp"

#include <atomic>
#include <array>
#include <chrono>
#include <cstring>
#include <future>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

using namespace capdvhs;

namespace {

#define CHECK(expression) do { if (!(expression)) throw std::runtime_error(std::string(#expression) + " at line " + std::to_string(__LINE__)); } while (false)

void free_type(AM_MEDIA_TYPE* type) {
    if (!type) return;
    CoTaskMemFree(type->pbFormat);
    if (type->pUnk) type->pUnk->Release();
    CoTaskMemFree(type);
}

AM_MEDIA_TYPE transport_type() {
    AM_MEDIA_TYPE result{};
    result.majortype = MEDIATYPE_Stream;
    result.subtype = MEDIASUBTYPE_MPEG2_TRANSPORT;
    result.formattype = FORMAT_None;
    result.bFixedSizeSamples = TRUE;
    result.lSampleSize = 188 * 256;
    return result;
}

class OutputPin final : public IPin {
public:
    PIN_DIRECTION direction = PINDIR_OUTPUT;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IPin) return E_NOINTERFACE;
        *result = static_cast<IPin*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG left = --references_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE Connect(IPin*, const AM_MEDIA_TYPE*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE ReceiveConnection(IPin*, const AM_MEDIA_TYPE*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Disconnect() override { return S_OK; }
    HRESULT STDMETHODCALLTYPE ConnectedTo(IPin**) override { return VFW_E_NOT_CONNECTED; }
    HRESULT STDMETHODCALLTYPE ConnectionMediaType(AM_MEDIA_TYPE*) override { return VFW_E_NOT_CONNECTED; }
    HRESULT STDMETHODCALLTYPE QueryPinInfo(PIN_INFO*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryDirection(PIN_DIRECTION* result) override { if (!result) return E_POINTER; *result = direction; return S_OK; }
    HRESULT STDMETHODCALLTYPE QueryId(LPWSTR*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryAccept(const AM_MEDIA_TYPE*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE EnumMediaTypes(IEnumMediaTypes**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE QueryInternalConnections(IPin**, ULONG*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EndOfStream() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE BeginFlush() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE EndFlush() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override { return E_NOTIMPL; }
private:
    std::atomic<ULONG> references_{1};
};

// Delegates normal sample behavior to the system allocator, while allowing
// malformed upstream values and COM failures to exercise defensive checks.
class BrokenSample final : public IMediaSample {
public:
    explicit BrokenSample(IMediaSample* sample) : sample_(sample) {}
    LONG actual = 188;
    LONG capacity = 4096;
    HRESULT pointer_status = S_OK;
    HRESULT type_status = S_FALSE;
    HRESULT discontinuity_status = S_FALSE;
    bool null_pointer = false;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid != IID_IUnknown && iid != IID_IMediaSample) return E_NOINTERFACE;
        *result = static_cast<IMediaSample*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references_; }
    ULONG STDMETHODCALLTYPE Release() override { const ULONG left = --references_; if (!left) delete this; return left; }
    HRESULT STDMETHODCALLTYPE GetPointer(BYTE** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (FAILED(pointer_status)) return pointer_status;
        return null_pointer ? S_OK : sample_->GetPointer(result);
    }
    LONG STDMETHODCALLTYPE GetSize() override { return capacity; }
    HRESULT STDMETHODCALLTYPE GetTime(REFERENCE_TIME*, REFERENCE_TIME*) override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE SetTime(REFERENCE_TIME* start, REFERENCE_TIME* end) override { return sample_->SetTime(start, end); }
    HRESULT STDMETHODCALLTYPE IsSyncPoint() override { return sample_->IsSyncPoint(); }
    HRESULT STDMETHODCALLTYPE SetSyncPoint(BOOL value) override { return sample_->SetSyncPoint(value); }
    HRESULT STDMETHODCALLTYPE IsPreroll() override { return sample_->IsPreroll(); }
    HRESULT STDMETHODCALLTYPE SetPreroll(BOOL value) override { return sample_->SetPreroll(value); }
    LONG STDMETHODCALLTYPE GetActualDataLength() override { return actual; }
    HRESULT STDMETHODCALLTYPE SetActualDataLength(LONG length) override { actual = length; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetMediaType(AM_MEDIA_TYPE** result) override { if (!result) return E_POINTER; *result = nullptr; return type_status; }
    HRESULT STDMETHODCALLTYPE SetMediaType(AM_MEDIA_TYPE* type) override { return sample_->SetMediaType(type); }
    HRESULT STDMETHODCALLTYPE IsDiscontinuity() override { return discontinuity_status; }
    HRESULT STDMETHODCALLTYPE SetDiscontinuity(BOOL value) override { return sample_->SetDiscontinuity(value); }
    HRESULT STDMETHODCALLTYPE GetMediaTime(LONGLONG*, LONGLONG*) override { return E_UNEXPECTED; }
    HRESULT STDMETHODCALLTYPE SetMediaTime(LONGLONG* start, LONGLONG* end) override { return sample_->SetMediaTime(start, end); }
private:
    std::atomic<ULONG> references_{1};
    ComPtr<IMediaSample> sample_;
};

struct Fixture {
    AM_MEDIA_TYPE type = transport_type();
    unsigned samples = 0;
    unsigned errors = 0;
    unsigned eos = 0;
    std::size_t bytes = 0;
    bool discontinuity = false;
    std::shared_ptr<SinkDiagnostics> diagnostics = std::make_shared<SinkDiagnostics>();
    ComPtr<IBaseFilter> filter;
    ComPtr<IPin> pin;
    ComPtr<IMemInputPin> input;
    ComPtr<OutputPin> output;
    ComPtr<IMemAllocator> allocator;
    ComPtr<IMediaSample> sample;

    Fixture() {
        filter = create_sink(type, [this](std::span<const std::uint8_t> data, bool disc) {
            ++samples; bytes += data.size(); discontinuity = disc; return S_OK;
        }, [this](const std::string&) { ++errors; }, [this] { ++eos; }, diagnostics);
        CHECK(filter->FindPin(L"Input", &pin) == S_OK);
        CHECK(pin.As(&input) == S_OK);
        output.Attach(new OutputPin());
        CHECK(input->GetAllocator(&allocator) == S_OK);
        ALLOCATOR_PROPERTIES requested{4, 4096, 1, 0}, actual{};
        CHECK(allocator->SetProperties(&requested, &actual) == S_OK);
        CHECK(actual.cbBuffer >= 4096);
        CHECK(allocator->Commit() == S_OK);
        CHECK(allocator->GetBuffer(&sample, nullptr, nullptr, 0) == S_OK);
        CHECK(sample->SetActualDataLength(188) == S_OK);
        BYTE* data = nullptr;
        CHECK(sample->GetPointer(&data) == S_OK);
        std::memset(data, 0x47, 188);
    }
    ~Fixture() {
        if (filter) filter->Stop();
        if (pin) pin->Disconnect();
        sample.Reset();
        if (allocator) allocator->Decommit();
    }
    void connect() { CHECK(pin->ReceiveConnection(output.Get(), &type) == S_OK); }
};

void test_identity_and_lifetime() {
    Fixture fixture;
    ComPtr<IUnknown> filter_identity, pin_identity, input_identity;
    CHECK(fixture.filter.As(&filter_identity) == S_OK);
    CHECK(fixture.pin.As(&pin_identity) == S_OK);
    CHECK(fixture.input.As(&input_identity) == S_OK);
    CHECK(filter_identity.Get() == pin_identity.Get());
    CHECK(filter_identity.Get() == input_identity.Get());
    ComPtr<IAMFilterMiscFlags> flags;
    CHECK(fixture.filter.As(&flags) == S_OK && flags->GetMiscFlags() == AM_FILTER_MISC_FLAGS_IS_RENDERER);
    CHECK(fixture.filter->QueryInterface(IID_IUnknown, nullptr) == E_POINTER);
    void* invalid = reinterpret_cast<void*>(1);
    CHECK(fixture.filter->QueryInterface(IID_IReferenceClock, &invalid) == E_NOINTERFACE);
    CHECK(invalid == nullptr);
    const ULONG references = fixture.filter->AddRef();
    CHECK(fixture.filter->Release() == references - 1);

    auto token = std::make_shared<int>(1);
    std::weak_ptr<int> weak(token);
    auto filter = create_sink(fixture.type, [token](auto, bool) { return S_OK; }, {}, {});
    token.reset();
    ComPtr<IEnumPins> enumerator;
    CHECK(filter->EnumPins(&enumerator) == S_OK);
    filter.Reset();
    CHECK(!weak.expired());
    ComPtr<IPin> pin;
    CHECK(enumerator->Next(1, &pin, nullptr) == S_OK);
    enumerator.Reset();
    CHECK(!weak.expired());
    PIN_INFO info{};
    CHECK(pin->QueryPinInfo(&info) == S_OK);
    pin.Reset();
    CHECK(!weak.expired());
    info.pFilter->Release();
    CHECK(weak.expired());
}

void test_enumerators_and_graph() {
    Fixture fixture;
    ComPtr<IEnumPins> pins, clone;
    CHECK(fixture.filter->EnumPins(&pins) == S_OK);
    IPin* listed[2]{};
    ULONG fetched = 99;
    CHECK(pins->Next(2, listed, &fetched) == S_FALSE);
    CHECK(fetched == 1 && listed[0] == fixture.pin.Get());
    listed[0]->Release();
    CHECK(pins->Clone(&clone) == S_OK);
    CHECK(clone->Next(1, listed, nullptr) == S_FALSE);
    CHECK(pins->Reset() == S_OK);
    CHECK(pins->Skip(0) == S_OK);
    CHECK(pins->Next(2, listed, nullptr) == E_POINTER);
    CHECK(pins->Skip(2) == S_FALSE);
    CHECK(pins->Reset() == S_OK);
    CHECK(pins->Next(1, listed, nullptr) == S_OK); listed[0]->Release();

    ComPtr<IEnumMediaTypes> types, types_clone;
    CHECK(fixture.pin->EnumMediaTypes(&types) == S_OK);
    AM_MEDIA_TYPE* listed_type[2]{};
    CHECK(types->Clone(&types_clone) == S_OK);
    CHECK(types->Next(2, listed_type, &fetched) == S_FALSE);
    CHECK(fetched == 1 && listed_type[0]->subtype == fixture.type.subtype);
    CHECK(fixture.pin->QueryAccept(listed_type[0]) == S_OK); free_type(listed_type[0]);
    CHECK(types_clone->Next(1, listed_type, nullptr) == S_OK); free_type(listed_type[0]);
    CHECK(types->Reset() == S_OK);
    CHECK(types->Skip(1) == S_OK);
    CHECK(types->Next(1, listed_type, nullptr) == S_FALSE);

    ComPtr<IGraphBuilder> graph;
    CHECK(CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&graph)) == S_OK);
    CHECK(graph->AddFilter(fixture.filter.Get(), L"Offline sink test") == S_OK);
    FILTER_INFO info{};
    CHECK(fixture.filter->QueryFilterInfo(&info) == S_OK);
    CHECK(std::wstring(info.achName) == L"Offline sink test");
    CHECK(info.pGraph != nullptr); info.pGraph->Release();
    CHECK(graph->RemoveFilter(fixture.filter.Get()) == S_OK);
    CHECK(fixture.filter->QueryFilterInfo(&info) == S_OK && !info.pGraph);
    const std::wstring long_name(MAX_FILTER_NAME * 2, L'N');
    CHECK(fixture.filter->JoinFilterGraph(nullptr, long_name.c_str()) == S_OK);
    CHECK(fixture.filter->QueryFilterInfo(&info) == S_OK);
    CHECK(std::wstring(info.achName) == std::wstring(MAX_FILTER_NAME - 1, L'N'));
    CHECK(info.achName[MAX_FILTER_NAME - 1] == L'\0');
}

void test_connection_state_and_allocator() {
    Fixture fixture;
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_NOT_CONNECTED);
    CHECK(fixture.pin->ReceiveConnection(nullptr, &fixture.type) == E_POINTER);
    auto wrong = fixture.type; wrong.subtype = MEDIASUBTYPE_NULL;
    CHECK(fixture.pin->QueryAccept(&wrong) == S_FALSE);
    CHECK(fixture.pin->ReceiveConnection(fixture.output.Get(), &wrong) == VFW_E_TYPE_NOT_ACCEPTED);
    fixture.output->direction = PINDIR_INPUT;
    CHECK(fixture.pin->ReceiveConnection(fixture.output.Get(), &fixture.type) == VFW_E_INVALID_DIRECTION);
    fixture.output->direction = PINDIR_OUTPUT;
    fixture.connect();
    CHECK(fixture.pin->ReceiveConnection(fixture.output.Get(), &fixture.type) == VFW_E_ALREADY_CONNECTED);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_WRONG_STATE);
    CHECK(fixture.filter->Pause() == S_OK);
    FILTER_STATE state{};
    CHECK(fixture.filter->GetState(0, &state) == S_OK && state == State_Paused);
    CHECK(fixture.pin->Disconnect() == VFW_E_NOT_STOPPED);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK && fixture.samples == 1);
    CHECK(fixture.filter->Run(987654321) == S_OK);
    CHECK(fixture.sample->SetDiscontinuity(TRUE) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK);
    CHECK(fixture.discontinuity && fixture.bytes == 376);
    CHECK(fixture.pin->BeginFlush() == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_FALSE && fixture.samples == 2);
    CHECK(fixture.pin->EndFlush() == S_OK);
    CHECK(fixture.pin->EndOfStream() == S_OK && fixture.eos == 1);
    CHECK(fixture.pin->EndOfStream() == S_OK && fixture.eos == 1);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_SAMPLE_REJECTED_EOS);
    CHECK(fixture.pin->NewSegment(100, 200, 1.0) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK && fixture.samples == 3);
    CHECK(fixture.filter->Stop() == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_WRONG_STATE);
    CHECK(fixture.pin->Disconnect() == S_OK);
    CHECK(fixture.pin->Disconnect() == S_FALSE);

    CHECK(fixture.input->NotifyAllocator(nullptr, FALSE) == E_POINTER);
    CHECK(fixture.input->NotifyAllocator(fixture.allocator.Get(), TRUE) == S_OK);
    ComPtr<IMemAllocator> allocator;
    CHECK(fixture.input->GetAllocator(&allocator) == S_OK && allocator.Get() == fixture.allocator.Get());
    ComPtr<IReferenceClock> clock, returned;
    CHECK(CoCreateInstance(CLSID_SystemClock, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&clock)) == S_OK);
    CHECK(fixture.filter->SetSyncSource(clock.Get()) == S_OK);
    CHECK(fixture.filter->GetSyncSource(&returned) == S_OK && returned.Get() == clock.Get());
    CHECK(fixture.filter->SetSyncSource(nullptr) == S_OK);
    returned.Reset();
    CHECK(fixture.filter->GetSyncSource(&returned) == S_OK && !returned);
}

void test_dynamic_types_and_malformed_samples() {
    Fixture fixture; fixture.connect(); CHECK(fixture.filter->Run(0) == S_OK);
    CHECK(fixture.sample->SetMediaType(&fixture.type) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK);
    auto changed = fixture.type; changed.lSampleSize += 188;
    CHECK(fixture.sample->SetMediaType(&changed) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_TYPE_NOT_ACCEPTED);
    CHECK(fixture.samples == 1 && fixture.errors == 1);
    CHECK(fixture.sample->SetMediaType(nullptr) == S_OK);
    CHECK(fixture.input->Receive(nullptr) == E_POINTER);

    ComPtr<BrokenSample> broken; broken.Attach(new BrokenSample(fixture.sample.Get()));
    broken->actual = -1;
    CHECK(fixture.input->Receive(broken.Get()) == E_INVALIDARG);
    broken->actual = 4097;
    CHECK(fixture.input->Receive(broken.Get()) == E_INVALIDARG);
    broken->actual = 188; broken->capacity = -1;
    CHECK(fixture.input->Receive(broken.Get()) == E_INVALIDARG);
    broken->capacity = 4096; broken->pointer_status = E_ACCESSDENIED;
    CHECK(fixture.input->Receive(broken.Get()) == E_ACCESSDENIED);
    broken->pointer_status = S_OK; broken->null_pointer = true;
    CHECK(fixture.input->Receive(broken.Get()) == E_POINTER);
    broken->null_pointer = false; broken->type_status = E_ABORT;
    CHECK(fixture.input->Receive(broken.Get()) == E_ABORT);
    broken->type_status = S_OK;
    CHECK(fixture.input->Receive(broken.Get()) == VFW_E_TYPE_NOT_ACCEPTED);
    broken->type_status = S_FALSE; broken->discontinuity_status = E_FAIL;
    CHECK(fixture.input->Receive(broken.Get()) == E_FAIL);
    broken->discontinuity_status = S_FALSE;
    CHECK(fixture.input->Receive(broken.Get()) == S_OK); // Sample timestamps are intentionally unavailable.
    CHECK(fixture.samples == 2);
    broken->actual = 0;
    CHECK(fixture.input->Receive(broken.Get()) == S_OK && fixture.samples == 2);
    broken->actual = 188;
    IMediaSample* multiple[]{fixture.sample.Get(), broken.Get(), nullptr};
    LONG processed = -1;
    CHECK(fixture.input->ReceiveMultiple(multiple, 3, &processed) == E_POINTER && processed == 2);
    CHECK(fixture.input->ReceiveMultiple(nullptr, -1, &processed) == E_INVALIDARG && processed == 0);
    CHECK(fixture.input->ReceiveMultiple(nullptr, 0, &processed) == S_OK && processed == 0);
}

void test_stride_format_and_unchanged_samples() {
    Fixture fixture;
    std::array<std::uint32_t, 3> fields{4, 188, 192};
    auto type = fixture.type;
    type.subtype = MEDIASUBTYPE_MPEG2_TRANSPORT_STRIDE;
    type.lSampleSize = 192 * 256;
    type.cbFormat = static_cast<ULONG>(sizeof(fields));
    type.pbFormat = reinterpret_cast<BYTE*>(fields.data());
    unsigned received = 0;
    auto filter = create_sink(type, [&](auto data, bool) {
        CHECK(data.size() == 192);
        CHECK(data[0] == 0x12 && data[1] == 0x34 && data[4] == 0x47);
        ++received;
        return received == 2 ? E_ABORT : S_OK;
    }, {}, {});
    ComPtr<IPin> pin; ComPtr<IMemInputPin> input;
    CHECK(filter->FindPin(L"Input", &pin) == S_OK && pin.As(&input) == S_OK);
    CHECK(pin->ReceiveConnection(fixture.output.Get(), &type) == S_OK);
    AM_MEDIA_TYPE connection{};
    CHECK(pin->ConnectionMediaType(&connection) == S_OK);
    CHECK(connection.pbFormat != type.pbFormat && connection.cbFormat == sizeof(fields));
    CHECK(std::memcmp(connection.pbFormat, fields.data(), sizeof(fields)) == 0);
    CoTaskMemFree(connection.pbFormat);
    if (connection.pUnk) connection.pUnk->Release();
    CHECK(fixture.sample->SetActualDataLength(192) == S_OK);
    BYTE* data = nullptr;
    CHECK(fixture.sample->GetPointer(&data) == S_OK);
    data[0] = 0x12; data[1] = 0x34; data[4] = 0x47;
    CHECK(fixture.sample->SetMediaType(&type) == S_OK);
    CHECK(filter->Run(0) == S_OK);
    CHECK(input->Receive(fixture.sample.Get()) == S_OK && received == 1);
    CHECK(input->Receive(fixture.sample.Get()) == E_ABORT && received == 2);
    fields[0] = 0; // A real layout change must fail even if the stride is unchanged.
    CHECK(fixture.sample->SetMediaType(&type) == S_OK);
    CHECK(input->Receive(fixture.sample.Get()) == VFW_E_TYPE_NOT_ACCEPTED && received == 2);
    CHECK(filter->Stop() == S_OK && pin->Disconnect() == S_OK);
}

void test_callback_failures_and_stop_serialization() {
    Fixture fixture;
    auto filter = create_sink(fixture.type, [](auto, bool) -> HRESULT { throw std::bad_alloc(); },
                              [](const std::string&) { throw std::runtime_error("error callback"); }, {});
    ComPtr<IPin> pin; ComPtr<IMemInputPin> input;
    CHECK(filter->FindPin(L"Input", &pin) == S_OK && pin.As(&input) == S_OK);
    CHECK(pin->ReceiveConnection(fixture.output.Get(), &fixture.type) == S_OK);
    CHECK(filter->Run(0) == S_OK);
    CHECK(input->Receive(fixture.sample.Get()) == E_OUTOFMEMORY);
    CHECK(filter->Stop() == S_OK && pin->Disconnect() == S_OK);

    std::promise<void> entered, release;
    auto entered_future = entered.get_future();
    auto release_future = release.get_future().share();
    filter = create_sink(fixture.type, [&](auto, bool) { entered.set_value(); release_future.wait(); return S_OK; }, {}, {});
    pin.Reset(); input.Reset();
    CHECK(filter->FindPin(L"Input", &pin) == S_OK && pin.As(&input) == S_OK);
    CHECK(pin->ReceiveConnection(fixture.output.Get(), &fixture.type) == S_OK && filter->Run(0) == S_OK);
    HRESULT receive_status = E_FAIL;
    std::thread receiver([&] { receive_status = input->Receive(fixture.sample.Get()); });
    const bool callback_entered = entered_future.wait_for(std::chrono::seconds(5)) == std::future_status::ready;
    if (!callback_entered) { release.set_value(); receiver.join(); CHECK(callback_entered); }
    std::promise<HRESULT> stopped;
    auto stopped_future = stopped.get_future();
    std::thread stopper([&] { stopped.set_value(filter->Stop()); });
    const bool waited_for_callback = stopped_future.wait_for(std::chrono::milliseconds(50)) == std::future_status::timeout;
    release.set_value(); receiver.join(); stopper.join();
    CHECK(waited_for_callback && receive_status == S_OK && stopped_future.get() == S_OK);
    CHECK(input->Receive(fixture.sample.Get()) == VFW_E_WRONG_STATE);
    CHECK(pin->Disconnect() == S_OK);
}

void test_receive_diagnostics() {
    Fixture fixture;
    const auto counts = [&](std::uint64_t calls, std::uint64_t empty, std::uint64_t rejected) {
        CHECK(fixture.diagnostics->receive_calls.load() == calls);
        CHECK(fixture.diagnostics->zero_length_samples.load() == empty);
        CHECK(fixture.diagnostics->rejected_samples.load() == rejected);
    };
    counts(0, 0, 0);
    CHECK(fixture.input->Receive(nullptr) == E_POINTER);
    counts(1, 0, 1);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_NOT_CONNECTED);
    counts(2, 0, 2);
    fixture.connect();
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_WRONG_STATE);
    counts(3, 0, 3);
    CHECK(fixture.filter->Run(0) == S_OK);
    CHECK(fixture.sample->SetActualDataLength(0) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK);
    counts(4, 1, 3);
    CHECK(fixture.samples == 0);
    CHECK(fixture.sample->SetActualDataLength(188) == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_OK);
    counts(5, 1, 3);
    CHECK(fixture.samples == 1);
    CHECK(fixture.pin->BeginFlush() == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == S_FALSE);
    counts(6, 1, 4); // S_FALSE means the sample was not accepted.
    CHECK(fixture.pin->EndFlush() == S_OK);
    CHECK(fixture.pin->EndOfStream() == S_OK);
    CHECK(fixture.input->Receive(fixture.sample.Get()) == VFW_E_SAMPLE_REJECTED_EOS);
    counts(7, 1, 5);
    CHECK(fixture.pin->NewSegment(0, 100, 1.0) == S_OK);
    ComPtr<BrokenSample> broken;
    broken.Attach(new BrokenSample(fixture.sample.Get()));
    broken->actual = -1;
    CHECK(fixture.input->Receive(broken.Get()) == E_INVALIDARG);
    counts(8, 1, 6);
    broken->actual = 0; broken->capacity = -1;
    CHECK(fixture.input->Receive(broken.Get()) == E_INVALIDARG);
    counts(9, 1, 7); // A malformed empty sample is not a legal zero-length sample.
    broken->capacity = 4096; broken->type_status = E_ABORT;
    CHECK(fixture.input->Receive(broken.Get()) == E_ABORT);
    counts(10, 1, 8);
    broken->type_status = S_FALSE;
    CHECK(fixture.input->Receive(broken.Get()) == S_OK);
    counts(11, 2, 8);
    CHECK(fixture.samples == 1);
    CHECK(fixture.filter->Stop() == S_OK);
    CHECK(fixture.input->Receive(broken.Get()) == VFW_E_WRONG_STATE);
    counts(12, 2, 9); // A zero-length sample rejected while stopped is not accepted.

    auto diagnostics = std::make_shared<SinkDiagnostics>();
    unsigned mode = 0;
    auto filter = create_sink(fixture.type, [&](auto, bool) -> HRESULT {
        if (mode == 0) return S_FALSE;
        if (mode == 1) return E_ABORT;
        if (mode == 2) throw std::bad_alloc();
        throw std::runtime_error("injected callback failure");
    }, {}, {}, diagnostics);
    ComPtr<IPin> pin;
    ComPtr<IMemInputPin> input;
    CHECK(filter->FindPin(L"Input", &pin) == S_OK && pin.As(&input) == S_OK);
    CHECK(pin->ReceiveConnection(fixture.output.Get(), &fixture.type) == S_OK);
    CHECK(filter->Run(0) == S_OK);
    const HRESULT expected[]{S_FALSE, E_ABORT, E_OUTOFMEMORY, E_FAIL};
    for (mode = 0; mode < 4; ++mode) {
        CHECK(input->Receive(fixture.sample.Get()) == expected[mode]);
        CHECK(diagnostics->receive_calls.load() == mode + 1);
        CHECK(diagnostics->rejected_samples.load() == mode + 1);
        CHECK(diagnostics->zero_length_samples.load() == 0);
    }
    CHECK(filter->Stop() == S_OK && pin->Disconnect() == S_OK);
}

} // namespace

int main() {
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) { std::cerr << "COM initialization failed\n"; return 1; }
    int result = 0;
    try {
        test_identity_and_lifetime();
        test_enumerators_and_graph();
        test_connection_state_and_allocator();
        test_dynamic_types_and_malformed_samples();
        test_stride_format_and_unchanged_samples();
        test_callback_failures_and_stop_serialization();
        test_receive_diagnostics();
        std::cout << "7 DirectShow sink test groups passed (offline; no device access)\n";
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; result = 1; }
    CoUninitialize();
    return result;
}
