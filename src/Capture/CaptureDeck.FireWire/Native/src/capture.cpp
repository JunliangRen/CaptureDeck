#include "capdvhs/capture.hpp"
#include "capdvhs/directshow.hpp"
#include "capdvhs/pipeline.hpp"
#include "capdvhs/util.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <thread>

namespace capdvhs {
namespace {
using Clock = std::chrono::steady_clock;
std::int64_t now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count();
}
std::string lower(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}
struct Failure {
    std::mutex mutex;
    std::atomic<bool> failed{false};
    int code = 0;
    std::string message;
    void set(int new_code, const std::string& new_message) {
        std::lock_guard lock(mutex);
        if (failed.load()) return;
        code = new_code; message = new_message; failed.store(true);
    }
};
void write_text(ByteOutput& output, const std::string& text) {
    auto bytes = std::span(reinterpret_cast<const std::uint8_t*>(text.data()), text.size());
    while (!bytes.empty()) {
        auto count = output.write(bytes);
        if (!count || count > bytes.size()) throw AppError(5, "Invalid report write length");
        bytes = bytes.subspan(count);
    }
    output.flush();
}
} // namespace

int run_capture(const CaptureOptions& options, std::atomic<bool>& cancel, CaptureSourceFactory source_factory) {
    Failure failure;
    const auto started_utc = utc_now();
    const auto started_ms = now_ms();
    std::int64_t stream_started_ms = 0;
    std::atomic<std::int64_t> first_sample_ms{0}, last_sample_ms{0};
    std::atomic<std::uint64_t> received_bytes{0}, samples{0}, queued_bytes{0}, not_queued_bytes{0};
    std::atomic<std::uint64_t> written_bytes{0}, discontinuity_samples{0};
    std::atomic<bool> initial_sample_discontinuity{false};
    std::atomic<bool> eos{false};
    BoundedQueue queue;
    const auto sink_diagnostics = std::make_shared<SinkDiagnostics>();
    std::string device = "null", media = "null", reason = "error";
    std::string startup_state = "null";
    PacketLayout layout;
    std::string_view output_format;
    TsStats stats;
    std::uint64_t processed_input_bytes = 0;
    bool stream_was_started = false, finalized = false;
    bool com_initialized = false;
    std::unique_ptr<FileOutput> output;
    std::unique_ptr<FileOutput> report_output;
    std::unique_ptr<StreamProcessor> processor;
    ComPtr<IGraphBuilder> graph;
    ComPtr<IMediaControl> control;
    ComPtr<IMediaEvent> events;
    ComPtr<IBaseFilter> source, sink;
    ComPtr<IPin> source_pin;
    std::thread writer;
    const std::filesystem::path partial = options.output.native() + L".partial";
    const std::filesystem::path report_path = options.output.native() + L".report.json";
    const std::filesystem::path report_partial = report_path.native() + L".partial";

    try {
        for (const auto& path : {options.output, partial, report_path, report_partial}) {
            if (std::filesystem::exists(path)) throw AppError(5, "Refusing to overwrite existing path: " + utf8(path.native()));
        }
        // Reserve the report name before opening the device. CREATE_NEW also
        // protects against concurrent captures racing on the same output name.
        report_output = std::make_unique<FileOutput>(report_partial);
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        check_hr(hr, "CoInitializeEx"); com_initialized = true;
        if (source_factory) {
            auto injected = source_factory();
            device = device_json(injected.device);
            source = std::move(injected.filter);
            if (!source) throw AppError(3, "Injected test source is null");
        } else {
            auto devices = enumerate_devices();
            auto found = std::find_if(devices.begin(), devices.end(), [&](const DeviceInfo& entry) {
                return lower(entry.id) == lower(options.device_id);
            });
            if (found == devices.end()) throw AppError(3, "Tape device not found: " + options.device_id);
            device = device_json(*found);
            source = bind_device(*found);
        }
        MediaType selected;
        source_pin = find_output_pin(source.Get(), options.format, selected);
        if (!media_layout(selected.value, layout)) throw AppError(4, "Selected media type has an unsupported packet layout");
        media = media_json(selected.value);
        output_format = format_name(layout.stride == 188 ? TsFormat::ts188 : TsFormat::ts192);
        check_hr(CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_PPV_ARGS(graph.GetAddressOf())), "Create FilterGraph");
        check_hr(graph->AddFilter(source.Get(), L"MSTape Source"), "Add MSTape source");
        output = std::make_unique<FileOutput>(partial);
        processor = std::make_unique<StreamProcessor>(layout, *output);

        sink = create_sink(selected.value,
            [&](std::span<const std::uint8_t> bytes, bool discontinuity) -> HRESULT {
                if (bytes.empty()) return S_OK;
                const auto time = now_ms();
                std::int64_t zero = 0; first_sample_ms.compare_exchange_strong(zero, time);
                last_sample_ms.store(time); received_bytes.fetch_add(bytes.size());
                const auto sample_index = samples.fetch_add(1);
                if (discontinuity) {
                    discontinuity_samples.fetch_add(1);
                    if (!sample_index) initial_sample_discontinuity.store(true);
                }
                if (failure.failed.load()) { not_queued_bytes.fetch_add(bytes.size()); return E_ABORT; }
                try {
                    const auto pushed = queue.push(bytes);
                    if (pushed == PushResult::accepted) { queued_bytes.fetch_add(bytes.size()); return S_OK; }
                    not_queued_bytes.fetch_add(bytes.size());
                    failure.set(6, pushed == PushResult::full ? "Capture queue overflow (64 MiB); samples were not silently overwritten"
                                                            : "Capture input queue is closed");
                    return pushed == PushResult::full ? E_OUTOFMEMORY : E_ABORT;
                } catch (const std::exception& error) {
                    not_queued_bytes.fetch_add(bytes.size()); failure.set(6, error.what()); return E_OUTOFMEMORY;
                } catch (...) {
                    not_queued_bytes.fetch_add(bytes.size()); failure.set(6, "Unknown receive callback error"); return E_FAIL;
                }
            },
            [&](const std::string& error) { failure.set(6, error); },
            [&] { eos.store(true); }, sink_diagnostics);
        check_hr(graph->AddFilter(sink.Get(), L"CaptureDeck FireWire Sequential Sink"), "Add sequential sink");
        ComPtr<IEnumPins> pins; check_hr(sink->EnumPins(&pins), "Enum sink pins");
        ComPtr<IPin> sink_pin;
        if (pins->Next(1, &sink_pin, nullptr) != S_OK) throw AppError(3, "Sink has no input pin");
        const HRESULT connection = graph->ConnectDirect(source_pin.Get(), sink_pin.Get(), &selected.value);
        if (connection == HRESULT_FROM_WIN32(ERROR_TOO_MANY_OPEN_FILES) ||
            connection == HRESULT_FROM_WIN32(ERROR_BUSY) || connection == VFW_E_ALREADY_CONNECTED)
            throw AppError(3, "Cannot open the tape capture pin (" + hresult_string(connection) +
                             "). Close other capture applications completely, including CapDVHS; "
                             "pressing Stop in CapDVHS can leave its device connection open.");
        if (connection == VFW_E_TYPE_NOT_ACCEPTED || connection == VFW_E_NO_ACCEPTABLE_TYPES ||
            connection == VFW_E_CANNOT_CONNECT || connection == VFW_E_INVALIDMEDIATYPE)
            throw AppError(4, "MSTape rejected the selected TS format: " + hresult_string(connection));
        check_hr(connection, "Connect MSTape directly to sink");
        check_hr(graph.As(&control), "Query IMediaControl");
        check_hr(graph.As(&events), "Query IMediaEvent");
        ComPtr<IMediaFilter> clock_control;
        check_hr(graph.As(&clock_control), "Query IMediaFilter");
        check_hr(graph->SetDefaultSyncSource(), "Select capture graph clock");
        writer = std::thread([&] {
            try {
                std::vector<std::uint8_t> bytes;
                while (queue.pop(bytes)) {
                    processor->process(bytes);
                    written_bytes.store(processor->written_bytes());
                }
                processor->finish();
            } catch (const AppError& error) { failure.set(error.code, error.what()); }
            catch (const std::exception& error) { failure.set(5, error.what()); }
            catch (...) { failure.set(5, "Unknown writer failure"); }
            // finish is idempotent at the analyzer level; retain final trailing
            // byte statistics even when an earlier write or flush failed.
            if (failure.failed.load()) { try { processor->finish(); } catch (...) {} }
            written_bytes.store(processor->written_bytes());
        });
        if (cancel.load()) reason = "cancelled";
        else {
            const HRESULT run_result = control->Run();
            trace_event("graph Run returned " + hresult_string(run_result));
            check_hr(run_result, "Run capture graph");
            OAFilterState graph_state = State_Stopped;
            FILTER_STATE source_state = State_Stopped, sink_state = State_Stopped;
            const HRESULT graph_state_hr = control->GetState(0, &graph_state);
            const HRESULT source_state_hr = source->GetState(0, &source_state);
            const HRESULT sink_state_hr = sink->GetState(0, &sink_state);
            startup_state = "{\"run_hresult\":" + json_quote(hresult_string(run_result)) +
                ",\"graph_state\":" + std::to_string(graph_state) + ",\"graph_state_hresult\":" + json_quote(hresult_string(graph_state_hr)) +
                ",\"source_state\":" + std::to_string(source_state) + ",\"source_state_hresult\":" + json_quote(hresult_string(source_state_hr)) +
                ",\"sink_state\":" + std::to_string(sink_state) + ",\"sink_state_hresult\":" + json_quote(hresult_string(sink_state_hr)) + "}";
            trace_event("startup_state=" + startup_state);
            if (trace_enabled()) {
                ComPtr<IAMExtTransport> transport;
                if (SUCCEEDED(source.As(&transport))) {
                    long mode = 0;
                    const HRESULT mode_result = transport->get_Mode(&mode);
                    trace_event("transport get_Mode=" + std::to_string(mode) + " result=" + hresult_string(mode_result));
                }
            }
            stream_was_started = true; stream_started_ms = now_ms();
            std::cerr << (graph_state_hr == S_OK && graph_state == State_Running ? "Capture graph running. " : "Capture graph starting. ")
                      << "Waiting for TS; play the tape manually. Ctrl+C stops computer capture only.\n";
            auto next_progress = stream_started_ms;
            while (!failure.failed.load()) {
                if (cancel.load()) { reason = "ctrl_c"; break; }
                long code = 0; LONG_PTR first = 0, second = 0;
                while (events->GetEvent(&code, &first, &second, 0) == S_OK) {
                    if (code == EC_ERRORABORT)
                        failure.set(6, "DirectShow EC_ERRORABORT: " + hresult_string(static_cast<long>(first)));
                    else if (code == EC_DEVICE_LOST && second == 0) failure.set(6, "Capture device disconnected");
                    else if (code == EC_COMPLETE) eos.store(true);
                    events->FreeEventParams(code, first, second);
                }
                if (failure.failed.load()) break;
                const auto time = now_ms();
                const auto first_time = first_sample_ms.load();
                const auto last_time = last_sample_ms.load();
                const auto idle_ms = time - (last_time ? last_time : stream_started_ms);
                if (options.no_data_timeout > 0 && idle_ms >= options.no_data_timeout * 1000.0) {
                    failure.set(7, "No nonempty samples received for " + std::to_string(options.no_data_timeout) +
                                   " seconds. Check normal tape playback and close other capture applications "
                                   "completely. Graph Running alone does not establish that TS data is arriving."); break;
                }
                if (first_time && options.duration > 0 && time - first_time >= options.duration * 1000.0) {
                    reason = "duration"; break;
                }
                if (eos.load()) { reason = "end_of_stream"; break; }
                if (time >= next_progress) {
                    const double elapsed = first_time ? (time - first_time) / 1000.0 : 0.0;
                    if (options.json) {
                        std::cout << "{\"schema_version\":" << schema_version << ",\"version\":" << json_quote(version)
                                  << ",\"type\":\"progress\",\"state\":\"recording\",\"elapsed_seconds\":" << elapsed
                                  << ",\"received_bytes\":" << received_bytes.load()
                                  << ",\"written_bytes\":" << written_bytes.load()
                                  << ",\"queue_bytes\":" << queue.size()
                                  << ",\"no_data_seconds\":" << idle_ms / 1000.0 << "}" << std::endl;
                    } else {
                        std::cerr << "received=" << received_bytes.load() << " written=" << written_bytes.load()
                                  << " queue=" << queue.size() << " idle=" << idle_ms / 1000.0 << "s\n";
                    }
                    next_progress = time + 1000;
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(50));
            }
        }
    } catch (const AppError& error) { failure.set(error.code, error.what()); }
    catch (const std::exception& error) { failure.set(5, error.what()); }
    catch (...) { failure.set(6, "Unknown capture error"); }

    // Stop must finish before closing the queue: the sink serializes Stop with
    // Receive, and no callback can race destruction of the queue or file.
    if (control) {
        const HRESULT stopped = control->Stop();
        if (FAILED(stopped)) failure.set(6, "Stop capture graph failed: " + hresult_string(stopped));
    }
    // A failed driver/graph Stop does not establish quiescence. Our own sink
    // always serializes Stop with Receive, independently of driver behavior.
    if (sink) {
        const HRESULT stopped = sink->Stop();
        if (FAILED(stopped)) failure.set(6, "Stop sequential sink failed: " + hresult_string(stopped));
    }
    const auto capture_stopped_ms = now_ms();
    queue.close();
    if (writer.joinable()) writer.join();
    if (processor) { stats = processor->stats(); processed_input_bytes = processor->processed_input_bytes(); }
    if (output) {
        try { output->close(); } catch (const AppError& error) { failure.set(error.code, error.what()); }
    }
    if (!failure.failed.load() && !received_bytes.load()) failure.set(7, "Capture ended without receiving any data");
    if (!failure.failed.load() && written_bytes.load() == 0) failure.set(4, "Capture ended without a complete output packet");
    if (!failure.failed.load()) {
        if (!MoveFileExW(partial.c_str(), options.output.c_str(), MOVEFILE_WRITE_THROUGH))
            failure.set(5, "Cannot finalize output: Win32 error " + std::to_string(GetLastError()));
        else finalized = true;
    }

    // Release every COM interface before uninitializing the control apartment.
    source_pin.Reset(); events.Reset(); control.Reset(); graph.Reset(); sink.Reset(); source.Reset();
    if (com_initialized) CoUninitialize();

    const auto ended_ms = now_ms();
    const double elapsed = first_sample_ms.load() ? (capture_stopped_ms - first_sample_ms.load()) / 1000.0 : 0.0;
    auto make_report = [&] {
        std::ostringstream text; text << std::setprecision(12);
        text << "{\"schema_version\":" << schema_version << ",\"version\":" << json_quote(version)
             << ",\"type\":\"capture_result\",\"state\":" << json_quote(failure.failed.load() ? "failed" : "finished")
             << ",\"success\":" << (failure.failed.load() ? "false" : "true")
             << ",\"exit_code\":" << failure.code << ",\"stop_reason\":" << json_quote(failure.failed.load() ? "error" : reason)
             << ",\"error\":" << (failure.failed.load() ? json_quote(failure.message) : "null")
             << ",\"started_utc\":" << json_quote(started_utc) << ",\"ended_utc\":" << json_quote(utc_now())
             << ",\"wall_seconds\":" << (ended_ms - started_ms) / 1000.0
             << ",\"drain_seconds\":" << (ended_ms - capture_stopped_ms) / 1000.0
             << ",\"elapsed_seconds\":" << elapsed << ",\"stream_started\":" << (stream_was_started ? "true" : "false")
             << ",\"source_kind\":" << json_quote(source_factory ? "injected_test_source" : "mstape")
             << ",\"startup_state\":" << startup_state
             << ",\"data_received\":" << (received_bytes.load() ? "true" : "false")
             << ",\"device\":" << device << ",\"media_type\":" << media
             << ",\"requested_format\":" << json_quote(format_name(options.format))
             << ",\"output_format\":" << (output_format.empty() ? "null" : json_quote(output_format))
             << ",\"output_path\":" << json_quote(utf8(options.output.native()))
             << ",\"data_path\":" << json_quote(utf8((finalized ? options.output : partial).native()))
             << ",\"data_finalized\":" << (finalized ? "true" : "false")
             << ",\"data_file_created\":" << (output ? "true" : "false")
             << ",\"report_path\":" << json_quote(utf8(report_path.native()))
             << ",\"received_bytes\":" << received_bytes.load() << ",\"received_samples\":" << samples.load()
             << ",\"sink_receive_calls\":" << sink_diagnostics->receive_calls.load()
             << ",\"sink_zero_length_samples\":" << sink_diagnostics->zero_length_samples.load()
             << ",\"sink_rejected_samples\":" << sink_diagnostics->rejected_samples.load()
             << ",\"queued_bytes\":" << queued_bytes.load() << ",\"not_queued_bytes\":" << not_queued_bytes.load()
             << ",\"processed_input_bytes\":" << processed_input_bytes << ",\"written_bytes\":" << written_bytes.load()
             << ",\"queue_remaining_bytes\":" << queue.size() << ",\"queue_peak_bytes\":" << queue.peak()
             << ",\"sample_discontinuities\":" << discontinuity_samples.load()
             << ",\"sample_discontinuities_after_first\":" << (discontinuity_samples.load() - (initial_sample_discontinuity.load() ? 1 : 0))
             << ",\"average_receive_mbps\":" << (elapsed > 0 ? received_bytes.load() * 8.0 / elapsed / 1000000.0 : 0.0)
             << ",\"no_data_seconds_at_stop\":" << (stream_was_started ? (capture_stopped_ms - (last_sample_ms.load() ? last_sample_ms.load() : stream_started_ms)) / 1000.0 : 0.0)
             << ",\"analysis_complete\":" << ((processed_input_bytes == received_bytes.load() && !failure.failed.load()) ? "true" : "false")
             << ",\"structural_validation_ok\":" << ((stats.packets && !stats.sync_errors && !stats.malformed_packets && !stats.trailing_bytes) ? "true" : "false")
             << ",\"quality_warning\":" << ((stats.sync_errors || stats.malformed_packets || stats.trailing_bytes || stats.tei || stats.continuity_errors || stats.pcr_backwards || discontinuity_samples.load() > (initial_sample_discontinuity.load() ? 1u : 0u)) ? "true" : "false")
             << ",\"analysis\":" << ts_stats_json(stats) << '}';
        return text.str();
    };
    std::string report = make_report();
    if (report_output) {
        try {
            write_text(*report_output, report + "\r\n"); report_output->close();
            if (!MoveFileExW(report_partial.c_str(), report_path.c_str(), MOVEFILE_WRITE_THROUGH))
                throw AppError(5, "Cannot finalize report: Win32 error " + std::to_string(GetLastError()));
        } catch (const std::exception& error) {
            if (!failure.failed.load()) failure.set(5, error.what());
            std::cerr << "Report error: " << error.what() << "; use the final stdout JSON if available.\n";
            report = make_report();
        }
    }
    if (options.json) std::cout << report << std::endl;
    else {
        std::cout << (failure.failed.load() ? "FAILED" : "FINISHED") << ": " << written_bytes.load()
                  << " bytes, " << stats.packets << " TS packets; file="
                  << utf8((finalized ? options.output : partial).native()) << '\n';
        std::cout << "sync_errors=" << stats.sync_errors << " malformed=" << stats.malformed_packets
                  << " TEI=" << stats.tei << " continuity_errors=" << stats.continuity_errors
                  << " pcr_backwards=" << stats.pcr_backwards
                  << " trailing=" << stats.trailing_bytes << '\n';
        std::cout << "sample_discontinuities=" << discontinuity_samples.load() << '\n';
        if (failure.failed.load()) std::cerr << failure.message << '\n';
    }
    return failure.code;
}
} // namespace capdvhs
