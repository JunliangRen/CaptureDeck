#pragma once

#include "capdvhs/core.hpp"
#include <condition_variable>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <span>
#include <vector>

namespace capdvhs {
inline constexpr std::size_t queue_capacity = 64ull * 1024 * 1024;

enum class PushResult { accepted, full, closed };
class BoundedQueue {
public:
    explicit BoundedQueue(std::size_t capacity = queue_capacity);
    PushResult push(std::span<const std::uint8_t> bytes);
    bool pop(std::vector<std::uint8_t>& bytes);
    void close();
    std::size_t peak() const;
    std::size_t size() const;
private:
    const std::size_t capacity_;
    mutable std::mutex mutex_;
    std::condition_variable ready_;
    std::deque<std::vector<std::uint8_t>> items_;
    std::size_t bytes_ = 0;
    std::size_t peak_ = 0;
    bool closed_ = false;
};

// Injectable output permits deterministic short-write and disk-failure tests.
class ByteOutput {
public:
    virtual ~ByteOutput() = default;
    virtual std::size_t write(std::span<const std::uint8_t> bytes) = 0;
    virtual void flush() = 0;
};

class FileOutput final : public ByteOutput {
public:
    explicit FileOutput(const std::filesystem::path& path);
    ~FileOutput() override;
    std::size_t write(std::span<const std::uint8_t> bytes) override;
    void flush() override;
    void close();
private:
    void* handle_ = nullptr;
};

class StreamProcessor {
public:
    StreamProcessor(PacketLayout layout, ByteOutput& output);
    void process(std::span<const std::uint8_t> bytes);
    void finish();
    std::uint64_t written_bytes() const { return written_bytes_; }
    std::uint64_t processed_input_bytes() const { return processed_input_bytes_; }
    const TsStats& stats() const { return analyzer_.stats(); }
private:
    void write_all(std::span<const std::uint8_t> bytes);
    TsAnalyzer analyzer_;
    ByteOutput& output_;
    std::uint64_t written_bytes_ = 0;
    std::uint64_t processed_input_bytes_ = 0;
};
} // namespace capdvhs
