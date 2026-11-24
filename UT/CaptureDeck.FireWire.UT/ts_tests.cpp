#include "capdvhs/core.hpp"

#include <algorithm>
#include <array>
#include <functional>
#include <iostream>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using Bytes = std::vector<std::uint8_t>;
using Packet = std::array<std::uint8_t, 188>;

void require(bool condition, const char* description) {
    if (!condition) throw std::runtime_error(description);
}

template<class Function> void rejects(Function action) {
    bool rejected = false;
    try { action(); }
    catch (const std::invalid_argument&) { rejected = true; }
    require(rejected, "Expected invalid_argument");
}

Packet make_packet(std::uint16_t pid, unsigned cc, Bytes payload = Bytes(184, 0xff), bool start = false) {
    require(payload.size() <= 184, "Invalid test payload size");
    Packet packet{};
    packet.fill(0xff);
    packet[0] = 0x47;
    packet[1] = std::uint8_t((pid >> 8) | (start ? 0x40 : 0));
    packet[2] = std::uint8_t(pid);
    packet[3] = std::uint8_t((payload.empty() ? 0x20 : payload.size() == 184 ? 0x10 : 0x30) | cc);
    if (payload.size() != 184) {
        packet[4] = std::uint8_t(183 - payload.size());
        if (packet[4]) packet[5] = 0;
    }
    std::copy(payload.begin(), payload.end(), packet.end() - payload.size());
    return packet;
}

Bytes concatenate(std::initializer_list<Packet> packets) {
    Bytes result;
    for (const auto& packet : packets) result.insert(result.end(), packet.begin(), packet.end());
    return result;
}

Bytes encode192(const Bytes& ts, unsigned offset = 4) {
    require(ts.size() % 188 == 0 && offset <= 4, "Invalid TS192 test input");
    Bytes result;
    for (std::size_t at = 0; at < ts.size(); at += 188) {
        result.insert(result.end(), offset, 0x11);
        result.insert(result.end(), ts.begin() + at, ts.begin() + at + 188);
        result.insert(result.end(), 4 - offset, 0x22);
    }
    return result;
}

Bytes pts_bytes(std::uint64_t value, unsigned prefix = 2) {
    return {std::uint8_t((prefix << 4) | ((value >> 29) & 0x0e) | 1),
            std::uint8_t(value >> 22), std::uint8_t(((value >> 14) & 0xfe) | 1),
            std::uint8_t(value >> 7), std::uint8_t(((value << 1) & 0xfe) | 1)};
}

Bytes pes(std::uint64_t pts, bool dts = false) {
    Bytes data{0, 0, 1, 0xe0, 0, 0, 0x80, std::uint8_t(dts ? 0xc0 : 0x80),
               std::uint8_t(dts ? 10 : 5)};
    auto stamp = pts_bytes(pts, dts ? 3 : 2);
    data.insert(data.end(), stamp.begin(), stamp.end());
    if (dts) {
        stamp = pts_bytes(pts - 1, 1);
        data.insert(data.end(), stamp.begin(), stamp.end());
    }
    return data;
}

void add_pcr(Packet& packet, std::uint64_t base, unsigned extension = 0) {
    require((packet[3] & 0x20) && packet[4] >= 7, "PCR needs adaptation space");
    packet[5] |= 0x10;
    packet[6] = std::uint8_t(base >> 25);
    packet[7] = std::uint8_t(base >> 17);
    packet[8] = std::uint8_t(base >> 9);
    packet[9] = std::uint8_t(base >> 1);
    packet[10] = std::uint8_t(((base & 1) << 7) | 0x7e | ((extension >> 8) & 1));
    packet[11] = std::uint8_t(extension);
}

capdvhs::TsStats analyze(const Bytes& bytes, capdvhs::PacketLayout layout = {}, std::size_t chunk = 31) {
    capdvhs::TsAnalyzer analyzer(layout);
    for (std::size_t at = 0; at < bytes.size(); at += chunk) {
        analyzer.feed(std::span<const std::uint8_t>(bytes).subspan(at, std::min(chunk, bytes.size() - at)));
    }
    analyzer.finish();
    return analyzer.stats();
}

void layout_validation() {
    for (const auto valid : {capdvhs::PacketLayout{}, capdvhs::PacketLayout{4, 188, 192},
                             capdvhs::PacketLayout{0, 188, 192}, capdvhs::PacketLayout{2, 188, 192}})
        capdvhs::validate_layout(valid);
    for (const auto invalid : {capdvhs::PacketLayout{1, 188, 188}, {5, 188, 192}, {0, 187, 188},
                              {0, 188, 0}, {0, 188, 16}, {0, 188, 204},
                              {std::numeric_limits<unsigned>::max(), 188, 192},
                              {0, std::numeric_limits<unsigned>::max(), 192}}) {
        rejects([&] { capdvhs::validate_layout(invalid); });
        rejects([&] { capdvhs::TsAnalyzer analyzer(invalid); });
    }
}

void chunked_188_and_192() {
    const auto input = concatenate({make_packet(256, 14), make_packet(256, 15), make_packet(256, 0),
                                    make_packet(257, 5), make_packet(257, 6)});
    for (const auto layout : {capdvhs::PacketLayout{}, capdvhs::PacketLayout{4, 188, 192},
                             capdvhs::PacketLayout{0, 188, 192}, capdvhs::PacketLayout{2, 188, 192}}) {
        const auto source = layout.stride == 192 ? encode192(input, layout.offset) : input;
        const auto stats = analyze(source, layout, 1);
        require(stats.input_bytes == source.size() && stats.packets == 5, "Chunked packet count");
        require(stats.pids.size() == 2 && stats.pids.at(256).packets == 3, "PID accounting");
        require(stats.continuity_errors == 0 && stats.trailing_bytes == 0, "Chunked continuity");
    }
}

void finish_and_truncation() {
    auto input = concatenate({make_packet(256, 0)});
    input.insert(input.end(), {0x47, 0x41, 0, 0x10, 1});
    capdvhs::TsAnalyzer analyzer({});
    analyzer.feed(input);
    analyzer.finish();
    analyzer.finish();
    require(analyzer.stats().packets == 1 && analyzer.stats().trailing_bytes == 5, "Truncated packet recorded");
    bool rejected = false;
    try { analyzer.feed({}); } catch (const std::logic_error&) { rejected = true; }
    require(rejected, "Finished analyzer refuses more bytes");
}

void no_implicit_resynchronization() {
    auto bad = make_packet(256, 1);
    bad[0] = 0;
    const auto input = concatenate({make_packet(256, 0), bad, make_packet(256, 2)});
    const auto stats = analyze(input);
    require(stats.packets == 3 && stats.sync_errors == 1 && stats.continuity_errors == 1, "Fixed stride sync reporting");
    require(stats.first_sync_error_packet == 1 && stats.first_continuity_error_packet == 2,
            "First errors use zero-based packet positions");
    auto shifted = input;
    shifted.insert(shifted.begin(), 0x99);
    auto shifted_stats = analyze(shifted);
    require(shifted_stats.sync_errors == 3 && shifted_stats.trailing_bytes == 1, "Analyzer must not search for new alignment");
}

void continuity_duplicate_and_adaptation() {
    const auto first = make_packet(256, 2);
    auto different = first;
    different[50] = 7;
    const auto duplicate_stats = analyze(concatenate({first, first, make_packet(256, 3)}));
    require(duplicate_stats.duplicates == 1 && duplicate_stats.continuity_errors == 0, "Exact duplicate accepted");
    const auto duplicate_burst = analyze(concatenate({first, first, first, first, make_packet(256, 3)}));
    require(duplicate_burst.duplicates == 3 && duplicate_burst.continuity_errors == 2, "Only two consecutive copies are permitted");
    const auto changed_stats = analyze(concatenate({first, different}));
    require(changed_stats.duplicates == 0 && changed_stats.continuity_errors == 1, "Same CC with changed packet is loss");
    const auto adaptation_stats = analyze(concatenate({first, make_packet(256, 2, {}), make_packet(256, 3)}));
    require(adaptation_stats.continuity_errors == 0, "Adaptation-only does not increment CC");
    const auto bad_adaptation = analyze(concatenate({first, make_packet(256, 3, {}), make_packet(256, 4)}));
    require(bad_adaptation.continuity_errors == 1, "Adaptation-only increment is a CC error");
    const auto after_adaptation = analyze(concatenate({first, make_packet(256, 2, {}), first, make_packet(256, 3)}));
    require(after_adaptation.duplicates == 0 && after_adaptation.continuity_errors == 1, "Adaptation-only interrupts a consecutive duplicate pair");
    const auto null_stats = analyze(concatenate({make_packet(0x1fff, 0), make_packet(0x1fff, 14), make_packet(0x1fff, 14)}));
    require(null_stats.continuity_errors == 0 && null_stats.duplicates == 0, "Null PID CC is not checked");
}

void explicit_discontinuity_and_tei() {
    auto discontinuity = make_packet(256, 12, Bytes{0xff});
    discontinuity[5] = 0x80;
    auto stats = analyze(concatenate({make_packet(256, 1), discontinuity, make_packet(256, 13)}));
    require(stats.discontinuities == 1 && stats.continuity_errors == 0, "Explicit discontinuity resets CC");
    auto tei = make_packet(256, 9);
    tei[1] |= 0x80;
    stats = analyze(concatenate({make_packet(256, 1), tei, make_packet(256, 2)}));
    require(stats.tei == 1 && stats.continuity_errors == 0 && stats.pids.at(256).tei == 1, "TEI must not poison CC");
    stats = analyze(concatenate({tei, make_packet(256, 2)}));
    require(stats.tei == 1 && stats.continuity_errors == 0, "Initial TEI cannot establish CC");
}

void malformed_transport_and_adaptation() {
    auto afc0 = make_packet(256, 0);
    afc0[3] = 0;
    auto short_adaptation = make_packet(256, 0, {});
    short_adaptation[4] = 1;
    auto overflow_adaptation = make_packet(256, 0, Bytes{1});
    overflow_adaptation[4] = 184;
    auto empty_payload = make_packet(256, 0, Bytes{1});
    empty_payload[4] = 183;
    auto short_pcr = make_packet(256, 0, Bytes(182, 0));
    short_pcr[5] = 0x10;
    auto private_overrun = make_packet(256, 0, Bytes(181, 0));
    private_overrun[5] = 2;
    private_overrun[6] = 5;
    auto extension_overrun = private_overrun;
    extension_overrun[5] = 1;
    const auto stats = analyze(concatenate({afc0, short_adaptation, overflow_adaptation, empty_payload,
                                           short_pcr, private_overrun, extension_overrun}));
    require(stats.malformed_packets == 7 && stats.pcr_count == 0, "Malformed TS/adaptation rejected");
}

void pcr_timestamps() {
    auto a = make_packet(256, 0, Bytes{0xff});
    auto b = make_packet(256, 1, Bytes{0xff});
    constexpr std::uint64_t high = (std::uint64_t{1} << 33) - 1;
    add_pcr(a, high, 299);
    add_pcr(b, 3, 17);
    auto stats = analyze(concatenate({a, a, b}));
    require(stats.pcr_count == 3 && stats.pids.at(256).first_pcr == high * 300 + 299 &&
            stats.pids.at(256).last_pcr == 917, "PCR ticks and wrap retained");
    require(stats.pcr_backwards == 0, "PCR wrap is not a backward timestamp");
    auto backward = make_packet(256, 2, Bytes{0xff});
    add_pcr(backward, 2);
    stats = analyze(concatenate({a, b, backward}));
    require(stats.pcr_backwards == 1 && stats.first_pcr_backwards_packet == 2 &&
            stats.pids.at(256).pcr_backwards == 1, "Unexpected PCR reversal reported");
    backward[5] |= 0x80;
    require(analyze(concatenate({a, b, backward})).pcr_backwards == 0,
            "Signaled discontinuity resets the PCR baseline");
    auto updated_duplicate = a;
    add_pcr(updated_duplicate, 1, 7);
    stats = analyze(concatenate({a, updated_duplicate, b}));
    require(stats.duplicates == 1 && stats.continuity_errors == 0 && stats.pcr_count == 3 &&
            stats.pids.at(256).last_pcr == 917, "Duplicate may carry updated PCR");
    auto invalid = a;
    invalid[10] |= 1;
    invalid[11] = 0xff;
    stats = analyze(concatenate({invalid}));
    require(stats.malformed_packets == 1 && stats.pcr_count == 0, "PCR extension must be below 300");
    invalid = a;
    invalid[10] &= ~0x02;
    require(analyze(concatenate({invalid})).malformed_packets == 1, "PCR reserved bits checked");
}

void pts_complete_and_fragmented() {
    constexpr std::uint64_t high = (std::uint64_t{1} << 33) - 2;
    auto whole = make_packet(257, 0, pes(high), true);
    auto stats = analyze(concatenate({whole, whole, make_packet(257, 1, pes(17, true), true)}));
    require(stats.pts_count == 2 && stats.pids.at(257).first_pts == high &&
            stats.pids.at(257).last_pts == 17, "PTS/DTS markers, duplicates and wrap");
    const auto data = pes(123456789);
    auto p1 = make_packet(257, 0, Bytes(data.begin(), data.begin() + 2), true);
    auto p2 = make_packet(257, 1, Bytes(data.begin() + 2, data.begin() + 7));
    auto p3 = make_packet(257, 2, Bytes(data.begin() + 7, data.end()));
    stats = analyze(concatenate({p1, p2, p2, make_packet(257, 1, {}), p3}), {}, 1);
    require(stats.pts_count == 1 && stats.pids.at(257).first_pts == 123456789 &&
            stats.duplicates == 1 && stats.continuity_errors == 0, "PES header crosses TS/sample and duplicate");
    stats = analyze(concatenate({p1, p3}));
    require(stats.pts_count == 0 && stats.continuity_errors == 1, "Do not stitch PES across CC gap");
    auto tei = p2;
    tei[1] |= 0x80;
    stats = analyze(concatenate({p1, tei, p3}));
    require(stats.pts_count == 0 && stats.tei == 1, "Do not stitch PES across TEI");
}

void malformed_pts_and_opaque_payload() {
    auto bad_marker = pes(99);
    bad_marker[11] &= 0xfe;
    auto short_header = pes(99);
    short_header[8] = 4;
    auto reserved_flags = pes(99);
    reserved_flags[7] = 0x40;
    auto bad_dts = pes(99, true);
    bad_dts[14] = 0x21;
    auto short_pes = pes(99);
    short_pes[5] = 7;
    auto stats = analyze(concatenate({make_packet(257, 0, bad_marker, true),
                                      make_packet(257, 1, short_header, true),
                                      make_packet(257, 2, reserved_flags, true),
                                      make_packet(257, 3, bad_dts, true),
                                      make_packet(257, 4, short_pes, true)}));
    require(stats.malformed_packets == 5 && stats.pts_count == 0, "Malformed PTS/PES is reported");
    auto scrambled = make_packet(257, 0, pes(99), true);
    scrambled[3] |= 0x80;
    stats = analyze(concatenate({make_packet(0, 0, Bytes{0, 0xb0, 0x0d}, true), scrambled,
                                make_packet(257, 1, Bytes{0, 0, 1, 0xbe, 0, 0}, true)}));
    require(stats.malformed_packets == 0 && stats.pts_count == 0, "PSI, padding PES and scrambled payload are opaque");
}

void layout_detection() {
    auto input = concatenate({make_packet(256, 0), make_packet(256, 1), make_packet(256, 2),
                               make_packet(256, 3), make_packet(256, 4), make_packet(256, 5)});
    require(capdvhs::detect_layout(input) == capdvhs::PacketLayout{}, "Detect TS188");
    require(capdvhs::detect_layout(encode192(input)) == capdvhs::PacketLayout{4, 188, 192}, "Detect TS192");
    rejects([&] { capdvhs::detect_layout(std::span<const std::uint8_t>(input).first(4 * 188)); });
    input.insert(input.begin(), 0x99);
    rejects([&] { capdvhs::detect_layout(input); });
    Bytes ambiguous(192 * 64, 0);
    for (const unsigned stride : {188u, 192u}) {
        const unsigned offset = stride == 192 ? 4 : 0;
        for (std::size_t at = offset; at + 188 <= ambiguous.size(); at += stride) {
            ambiguous[at] = 0x47;
            ambiguous[at + 3] = 0x10;
        }
    }
    rejects([&] { capdvhs::detect_layout(ambiguous); });
}

void json_reporting() {
    const auto stats = analyze(concatenate({make_packet(256, 0, pes(90000), true)}));
    const auto json = capdvhs::ts_stats_json(stats);
    require(json.starts_with("{\"input_bytes\":188,"), "JSON reports input bytes");
    require(json.find("\"pid\":256") != std::string::npos &&
            json.find("\"first_pts\":90000") != std::string::npos &&
            json.find("\"first_pcr\":null") != std::string::npos &&
            json.find("\"pcr_tick_hz\":27000000") != std::string::npos && json.ends_with("]}"), "JSON timestamps and nulls");
}
} // namespace

int main() {
    const std::pair<const char*, std::function<void()>> tests[]{
        {"layout validation", layout_validation}, {"chunked TS188/TS192", chunked_188_and_192},
        {"finish and truncation", finish_and_truncation}, {"no implicit resynchronization", no_implicit_resynchronization},
        {"continuity, duplicate, adaptation, null", continuity_duplicate_and_adaptation},
        {"discontinuity and TEI", explicit_discontinuity_and_tei},
        {"malformed transport and adaptation", malformed_transport_and_adaptation},
        {"PCR timestamps", pcr_timestamps}, {"PTS complete and fragmented", pts_complete_and_fragmented},
        {"malformed PTS and opaque payload", malformed_pts_and_opaque_payload},
        {"layout detection", layout_detection}, {"JSON reporting", json_reporting}};
    unsigned failures = 0;
    for (const auto& [name, test] : tests) {
        try { test(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    }
    std::cout << std::size(tests) - failures << '/' << std::size(tests) << " tests passed\n";
    return failures ? 1 : 0;
}
