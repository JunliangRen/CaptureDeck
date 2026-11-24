#include "capdvhs/pipeline.hpp"
#include <array>
#include <iostream>
#include <stdexcept>
#include <thread>

using namespace capdvhs;
void expect(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
class MemoryOutput : public ByteOutput {
public:
    std::vector<std::uint8_t> bytes;
    std::size_t limit = 1000000;
    bool fail = false;
    bool flushed = false;
    std::size_t write(std::span<const std::uint8_t> input) override {
        if (fail) throw std::runtime_error("injected disk failure");
        auto count = (std::min)(limit, input.size());
        bytes.insert(bytes.end(), input.begin(), input.begin() + static_cast<std::ptrdiff_t>(count));
        return count;
    }
    void flush() override { flushed = true; }
};
int main() {
    try {
        BoundedQueue queue(4);
        const std::array<std::uint8_t,3> first{1,2,3};
        expect(queue.push(first) == PushResult::accepted, "first push");
        expect(queue.push(first) == PushResult::full, "overflow must reject");
        expect(queue.peak() == 3, "overflow must not overwrite accepted data");
        queue.close();
        expect(queue.push(first) == PushResult::closed, "push after close");
        std::vector<std::uint8_t> item;
        expect(queue.pop(item) && item == std::vector<std::uint8_t>({1,2,3}), "drain accepted data after close");
        expect(!queue.pop(item), "closed queue ends");
        BoundedQueue wakeup;
        bool woke = false;
        std::thread consumer([&] { std::vector<std::uint8_t> value; woke = !wakeup.pop(value); });
        wakeup.close(); consumer.join(); expect(woke, "close wakes blocked writer");

        std::array<std::uint8_t,192> packet{};
        packet.fill(0xff); packet[0]=1; packet[1]=2; packet[2]=3; packet[3]=4;
        packet[4]=0x47; packet[5]=0x01; packet[6]=0; packet[7]=0x10;
        MemoryOutput original; original.limit=7;
        StreamProcessor raw({4,188,192}, original);
        raw.process(std::span(packet).first(31)); raw.process(std::span(packet).subspan(31)); raw.finish();
        expect(original.bytes == std::vector<std::uint8_t>(packet.begin(),packet.end()), "raw exact bytes despite short writes");
        expect(raw.written_bytes()==192 && original.flushed && raw.stats().packets==1, "raw accounting");
        MemoryOutput original188;
        StreamProcessor raw188({0,188,188}, original188);
        const auto packet188 = std::span(packet).subspan(4);
        raw188.process(packet188.first(117)); raw188.process(packet188.subspan(117)); raw188.finish();
        expect(original188.bytes == std::vector<std::uint8_t>(packet188.begin(),packet188.end()), "native 188-byte packet preserved");
        MemoryOutput broken; broken.fail=true;
        StreamProcessor failure({4,188,192}, broken);
        bool caught=false; try { failure.process(packet); } catch (...) { caught=true; }
        expect(caught && failure.written_bytes()==0, "disk failure must propagate");
        MemoryOutput zero; zero.limit=0;
        StreamProcessor no_progress({4,188,192}, zero);
        caught=false; try { no_progress.process(packet); } catch (...) { caught=true; }
        expect(caught, "zero-byte write must fail rather than hang");
        MemoryOutput partial;
        StreamProcessor incomplete({4,188,192}, partial);
        incomplete.process(std::span(packet).first(191));
        caught=false; try { incomplete.finish(); } catch (...) { caught=true; }
        expect(caught && partial.bytes.size()==191 && incomplete.stats().trailing_bytes==191, "truncated stream preserved and rejected");
        MemoryOutput garbage;
        StreamProcessor invalid({4,188,192}, garbage);
        auto bad_packet = packet; bad_packet[4] = 0;
        invalid.process(bad_packet);
        caught=false; try { invalid.finish(); } catch (...) { caught=true; }
        expect(caught && garbage.bytes.size()==192 && invalid.stats().sync_errors==1, "entirely invalid TS preserved but not successful");
        std::cout << "pipeline: queue overflow/drain/wakeup, short writes, exact preservation, disk failure, truncation passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
