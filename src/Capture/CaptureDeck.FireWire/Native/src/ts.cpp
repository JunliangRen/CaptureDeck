#include "capdvhs/core.hpp"

#include <algorithm>
#include <limits>
#include <sstream>
#include <stdexcept>

namespace capdvhs {
namespace {
constexpr std::size_t ts_size = 188;
constexpr std::size_t detection_packets = 5;

bool same_payload_packet(const std::array<std::uint8_t, 188>& previous,
                         std::span<const std::uint8_t> current) {
    // A legal duplicate may carry an updated PCR. The rest of the transport
    // packet, including adaptation flags and all payload bytes, must match.
    if ((previous[3] & 0x20) && (current[3] & 0x20) &&
        previous[4] >= 7 && current[4] >= 7 &&
        (previous[5] & 0x10) && (current[5] & 0x10)) {
        return std::equal(previous.begin(), previous.begin() + 6, current.begin()) &&
               std::equal(previous.begin() + 12, previous.end(), current.begin() + 12);
    }
    return std::equal(previous.begin(), previous.end(), current.begin());
}

bool structurally_valid(std::span<const std::uint8_t> data) {
    if (data.size() != ts_size || data[0] != 0x47) return false;
    const auto control = (data[3] >> 4) & 3;
    if (control == 0) return false;
    if (control == 1) return true;
    const auto length = data[4];
    return control == 2 ? length == 183 : length <= 182;
}

bool has_optional_pes_header(std::uint8_t stream_id) {
    switch (stream_id) {
    case 0xbc: case 0xbe: case 0xbf: case 0xf0:
    case 0xf1: case 0xf2: case 0xf8: case 0xff:
        return false;
    default:
        return stream_id >= 0xbd;
    }
}

std::optional<std::uint64_t> timestamp(std::span<const std::uint8_t> data,
                                      unsigned prefix) {
    if (data.size() < 5 || (unsigned(data[0]) >> 4) != prefix ||
        !(data[0] & 1) || !(data[2] & 1) || !(data[4] & 1)) return {};
    return (std::uint64_t(data[0] & 0x0e) << 29) |
           (std::uint64_t(data[1]) << 22) |
           (std::uint64_t(data[2] & 0xfe) << 14) |
           (std::uint64_t(data[3]) << 7) | (data[4] >> 1);
}

void optional_json(std::ostringstream& stream, const std::optional<std::uint64_t>& value) {
    if (value) stream << *value;
    else stream << "null";
}
} // namespace

void validate_layout(const PacketLayout& layout) {
    if (layout.packet_length != ts_size ||
        (layout.stride != 188 && layout.stride != 192) ||
        layout.offset > layout.stride - layout.packet_length) {
        throw std::invalid_argument("TS layout requires 188-byte packets within a 188- or 192-byte stride");
    }
}

TsAnalyzer::TsAnalyzer(PacketLayout layout) : layout_(layout) {
    validate_layout(layout_);
}

void TsAnalyzer::feed(std::span<const std::uint8_t> bytes) {
    if (finished_) throw std::logic_error("Cannot feed a finished TS analyzer");
    stats_.input_bytes += bytes.size();
    pending_.insert(pending_.end(), bytes.begin(), bytes.end());
    std::size_t consumed = 0;
    while (pending_.size() - consumed >= layout_.stride) {
        packet(std::span<const std::uint8_t>(pending_).subspan(
            consumed + layout_.offset, layout_.packet_length));
        consumed += layout_.stride;
    }
    pending_.erase(pending_.begin(), pending_.begin() + consumed);
}

void TsAnalyzer::finish() {
    if (finished_) return;
    stats_.trailing_bytes = pending_.size();
    finished_ = true;
}

void TsAnalyzer::packet(std::span<const std::uint8_t> bytes) {
    const auto packet_index = stats_.packets++;
    const auto remember_first = [packet_index](std::optional<std::uint64_t>& first) {
        if (!first) first = packet_index;
    };
    if (bytes[0] != 0x47) {
        ++stats_.sync_errors;
        remember_first(stats_.first_sync_error_packet);
        // The missing packet's PID is unknowable. Do not stitch PES fragments
        // across it; retain CC baselines so later gaps remain observable.
        for (auto& [pid, state] : states_) {
            (void)pid;
            state.collecting_pes = false;
            state.pes_header.clear();
            state.have_packet = false;
            state.duplicate_seen = false;
        }
        return;
    }
    const auto pid = std::uint16_t(((bytes[1] & 0x1f) << 8) | bytes[2]);
    auto& stats = stats_.pids[pid];
    ++stats.packets;
    auto& state = states_[pid];
    const auto reset_pes = [&] {
        state.collecting_pes = false;
        state.pes_header.clear();
    };
    if (bytes[1] & 0x80) {
        ++stats_.tei;
        remember_first(stats_.first_tei_packet);
        ++stats.tei;
        state.have_packet = false;
        state.duplicate_seen = false;
        reset_pes();
        return; // A corrupt transport header must not seed continuity state.
    }
    if (!structurally_valid(bytes)) {
        ++stats_.malformed_packets;
        remember_first(stats_.first_malformed_packet);
        state.have_packet = false;
        state.duplicate_seen = false;
        reset_pes();
        return;
    }
    const auto control = (bytes[3] >> 4) & 3;
    const bool payload = (control & 1) != 0;
    const auto cc = std::uint8_t(bytes[3] & 0x0f);
    std::size_t payload_offset = 4;
    bool discontinuity = false;
    bool malformed = false;
    std::optional<std::uint64_t> pcr;
    if (control & 2) {
        const auto length = bytes[4];
        payload_offset = 5 + length;
        if (length) {
            const auto flags = bytes[5];
            discontinuity = (flags & 0x80) != 0;
            // Walk all variable adaptation fields to detect truncation, even
            // though only PCR is interpreted by this diagnostic analyzer.
            std::size_t cursor = 6;
            const auto end = payload_offset;
            const auto take = [&](std::size_t amount) {
                if (amount > end - cursor) { malformed = true; return false; }
                cursor += amount;
                return true;
            };
            if (flags & 0x10) {
                if (take(6)) {
                    const auto at = cursor - 6;
                    const auto extension = unsigned((bytes[at + 4] & 1) << 8) | bytes[at + 5];
                    if ((bytes[at + 4] & 0x7e) != 0x7e || extension >= 300) {
                        malformed = true;
                    } else {
                        const std::uint64_t base =
                            (std::uint64_t(bytes[at]) << 25) |
                            (std::uint64_t(bytes[at + 1]) << 17) |
                            (std::uint64_t(bytes[at + 2]) << 9) |
                            (std::uint64_t(bytes[at + 3]) << 1) |
                            (bytes[at + 4] >> 7);
                        pcr = base * 300 + extension;
                    }
                }
            }
            if (!malformed && (flags & 0x08)) take(6); // OPCR
            if (!malformed && (flags & 0x04)) take(1); // splice countdown
            if (!malformed && (flags & 0x02) && take(1)) {
                const auto private_length = bytes[cursor - 1];
                take(private_length);
            }
            if (!malformed && (flags & 0x01) && take(1)) {
                const auto extension_length = bytes[cursor - 1];
                take(extension_length);
            }
        }
    }
    if (malformed) {
        ++stats_.malformed_packets;
        remember_first(stats_.first_malformed_packet);
        state.have_packet = false;
        state.duplicate_seen = false;
        reset_pes();
        return;
    }
    if (discontinuity) {
        ++stats_.discontinuities;
        ++stats.discontinuities;
        state.previous_pcr.reset();
        state.have_cc = false;
        state.have_packet = false;
        state.duplicate_seen = false;
        reset_pes();
    }
    bool duplicate = false;
    if (pid != 0x1fff) {
        if (state.have_cc) {
            const auto expected = std::uint8_t((state.cc + (payload ? 1 : 0)) & 0x0f);
            duplicate = payload && cc == state.cc && state.have_packet &&
                        same_payload_packet(state.packet, bytes);
            if (duplicate) {
                ++stats_.duplicates;
                ++stats.duplicates;
                if (state.duplicate_seen) {
                    // H.222.0 permits only two consecutive copies of one
                    // packet. Keep the duplicate evidence and flag the burst.
                    ++stats_.continuity_errors;
                    remember_first(stats_.first_continuity_error_packet);
                    ++stats.continuity_errors;
                    reset_pes();
                }
            } else if (cc != expected) {
                ++stats_.continuity_errors;
                remember_first(stats_.first_continuity_error_packet);
                ++stats.continuity_errors;
                reset_pes();
            }
        }
        state.have_cc = true;
        state.cc = cc;
        state.duplicate_seen = duplicate;
        if (payload) {
            std::copy(bytes.begin(), bytes.end(), state.packet.begin());
            state.have_packet = true;
        } else {
            state.have_packet = false;
        }
    }
    if (pcr) {
        ++stats_.pcr_count;
        ++stats.pcr_count;
        if (!stats.first_pcr) stats.first_pcr = pcr;
        stats.last_pcr = pcr;
        constexpr std::uint64_t wrap = (std::uint64_t{1} << 33) * 300;
        if (state.previous_pcr && (wrap + *pcr - *state.previous_pcr) % wrap > wrap / 2) {
            ++stats_.pcr_backwards;
            ++stats.pcr_backwards;
            remember_first(stats_.first_pcr_backwards_packet);
        }
        state.previous_pcr = pcr;
    }
    if (duplicate) return; // PCR is per received packet; PES timestamps are unique.
    if (!payload || pid == 0x1fff) return;
    if (bytes[3] & 0xc0) { reset_pes(); return; } // Scrambled PES is opaque.
    if (bytes[1] & 0x40) {
        reset_pes();
        state.collecting_pes = true;
    }
    if (!state.collecting_pes) return;
    auto& header = state.pes_header;
    // PTS/DTS are within the first 19 PES bytes. Keeping only those bytes
    // bounds memory and permits the header to cross TS/sample boundaries.
    const auto amount = std::min<std::size_t>(19 - header.size(), ts_size - payload_offset);
    header.insert(header.end(), bytes.begin() + payload_offset,
                  bytes.begin() + payload_offset + amount);
    if (header.size() < 3) return;
    if (header[0] != 0 || header[1] != 0 || header[2] != 1) {
        reset_pes(); // PSI/SI and other non-PES payloads are expected.
        return;
    }
    if (header.size() < 6) return;
    if (!has_optional_pes_header(header[3])) { reset_pes(); return; }
    if (header.size() < 9) return;
    if ((header[6] & 0xc0) != 0x80) { reset_pes(); return; }
    const auto flags = header[7] >> 6;
    const auto required = flags == 3 ? 10u : 5u;
    const unsigned pes_length = (header[4] << 8) | header[5];
    if (flags == 1 || (flags >= 2 && header[8] < required) ||
        (pes_length != 0 && pes_length < 3u + header[8])) {
        ++stats_.malformed_packets;
        remember_first(stats_.first_malformed_packet);
        reset_pes();
        return;
    }
    if (flags == 0) { reset_pes(); return; }
    if (header.size() < 9 + required) return;
    auto pts = timestamp(std::span<const std::uint8_t>(header).subspan(9, 5), flags);
    if (!pts || (flags == 3 &&
        !timestamp(std::span<const std::uint8_t>(header).subspan(14, 5), 1))) {
        ++stats_.malformed_packets;
        remember_first(stats_.first_malformed_packet);
        reset_pes();
        return;
    }
    ++stats_.pts_count;
    ++stats.pts_count;
    if (!stats.first_pts) stats.first_pts = pts;
    stats.last_pts = pts;
    reset_pes();
}

PacketLayout detect_layout(std::span<const std::uint8_t> bytes) {
    std::vector<PacketLayout> matches;
    for (const auto layout : {PacketLayout{0, 188, 188}, PacketLayout{4, 188, 192}}) {
        const auto count = std::min<std::size_t>(64, bytes.size() / layout.stride);
        if (count < detection_packets) continue;
        bool valid = true;
        for (std::size_t index = 0; index < count; ++index) {
            if (!structurally_valid(bytes.subspan(index * layout.stride + layout.offset, ts_size))) {
                valid = false;
                break;
            }
        }
        if (valid) matches.push_back(layout);
    }
    if (matches.empty()) {
        throw std::invalid_argument("Cannot identify TS layout: need at least five aligned valid TS188 or TS192 packets; specify the declared driver layout");
    }
    if (matches.size() != 1) {
        throw std::invalid_argument("Ambiguous TS188/TS192 layout; specify the declared driver layout");
    }
    return matches.front();
}

std::string ts_stats_json(const TsStats& stats) {
    std::ostringstream out;
    out << "{\"input_bytes\":" << stats.input_bytes
        << ",\"packets\":" << stats.packets
        << ",\"sync_errors\":" << stats.sync_errors
        << ",\"malformed_packets\":" << stats.malformed_packets
        << ",\"tei\":" << stats.tei
        << ",\"continuity_errors\":" << stats.continuity_errors
        << ",\"duplicates\":" << stats.duplicates
        << ",\"discontinuities\":" << stats.discontinuities
        << ",\"pcr_count\":" << stats.pcr_count
        << ",\"pcr_backwards\":" << stats.pcr_backwards
        << ",\"pts_count\":" << stats.pts_count
        << ",\"trailing_bytes\":" << stats.trailing_bytes
        << ",\"pcr_tick_hz\":27000000,\"pts_tick_hz\":90000";
    out << ",\"first_sync_error_packet\":";
    optional_json(out, stats.first_sync_error_packet);
    out << ",\"first_malformed_packet\":";
    optional_json(out, stats.first_malformed_packet);
    out << ",\"first_tei_packet\":";
    optional_json(out, stats.first_tei_packet);
    out << ",\"first_continuity_error_packet\":";
    optional_json(out, stats.first_continuity_error_packet);
    out << ",\"first_pcr_backwards_packet\":";
    optional_json(out, stats.first_pcr_backwards_packet);
    out << ",\"pids\":[";
    bool first = true;
    for (const auto& [pid, entry] : stats.pids) {
        if (!first) out << ',';
        first = false;
        out << "{\"pid\":" << pid << ",\"packets\":" << entry.packets
            << ",\"tei\":" << entry.tei
            << ",\"continuity_errors\":" << entry.continuity_errors
            << ",\"duplicates\":" << entry.duplicates
            << ",\"discontinuities\":" << entry.discontinuities
            << ",\"pcr_count\":" << entry.pcr_count
            << ",\"pcr_backwards\":" << entry.pcr_backwards
            << ",\"pts_count\":" << entry.pts_count << ",\"first_pcr\":";
        optional_json(out, entry.first_pcr);
        out << ",\"last_pcr\":";
        optional_json(out, entry.last_pcr);
        out << ",\"first_pts\":";
        optional_json(out, entry.first_pts);
        out << ",\"last_pts\":";
        optional_json(out, entry.last_pts);
        out << '}';
    }
    out << "]}";
    return out.str();
}

} // namespace capdvhs
