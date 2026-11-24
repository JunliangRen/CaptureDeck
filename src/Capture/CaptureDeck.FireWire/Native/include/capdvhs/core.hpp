#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <span>
#include <string>
#include <vector>

namespace capdvhs {

struct PacketLayout {
    std::uint32_t offset = 0;
    std::uint32_t packet_length = 188;
    std::uint32_t stride = 188;
    bool operator==(const PacketLayout&) const = default;
};

void validate_layout(const PacketLayout& layout);

struct PidStats {
    std::uint64_t packets = 0;
    std::uint64_t tei = 0;
    std::uint64_t continuity_errors = 0;
    std::uint64_t duplicates = 0;
    std::uint64_t discontinuities = 0;
    std::uint64_t pcr_count = 0;
    std::uint64_t pcr_backwards = 0;
    std::uint64_t pts_count = 0;
    std::optional<std::uint64_t> first_pcr;
    std::optional<std::uint64_t> last_pcr;
    std::optional<std::uint64_t> first_pts;
    std::optional<std::uint64_t> last_pts;
};

struct TsStats {
    std::uint64_t input_bytes = 0;
    std::uint64_t packets = 0;
    std::uint64_t sync_errors = 0;
    std::uint64_t malformed_packets = 0;
    std::uint64_t tei = 0;
    std::uint64_t continuity_errors = 0;
    std::uint64_t duplicates = 0;
    std::uint64_t discontinuities = 0;
    std::uint64_t pcr_count = 0;
    std::uint64_t pcr_backwards = 0;
    std::uint64_t pts_count = 0;
    std::uint64_t trailing_bytes = 0;
    std::optional<std::uint64_t> first_sync_error_packet;
    std::optional<std::uint64_t> first_malformed_packet;
    std::optional<std::uint64_t> first_tei_packet;
    std::optional<std::uint64_t> first_continuity_error_packet;
    std::optional<std::uint64_t> first_pcr_backwards_packet;
    std::map<std::uint16_t, PidStats> pids;
};

class TsAnalyzer {
public:
    explicit TsAnalyzer(PacketLayout layout);
    void feed(std::span<const std::uint8_t> bytes);
    void finish();
    const TsStats& stats() const { return stats_; }
private:
    struct PidState {
        bool have_cc = false;
        std::uint8_t cc = 0;
        bool have_packet = false;
        bool duplicate_seen = false;
        std::array<std::uint8_t, 188> packet{};
        std::optional<std::uint64_t> previous_pcr;
        std::vector<std::uint8_t> pes_header;
        bool collecting_pes = false;
    };
    void packet(std::span<const std::uint8_t> bytes);
    PacketLayout layout_;
    TsStats stats_;
    std::vector<std::uint8_t> pending_;
    std::map<std::uint16_t, PidState> states_;
    bool finished_ = false;
};

PacketLayout detect_layout(std::span<const std::uint8_t> bytes);
std::string ts_stats_json(const TsStats& stats);

} // namespace capdvhs
