#include "capdvhs/pipeline.hpp"
#include "capdvhs/util.hpp"
#include <windows.h>
#include <algorithm>
#include <limits>

namespace capdvhs {
BoundedQueue::BoundedQueue(std::size_t capacity) : capacity_(capacity) {
    if (!capacity) throw std::invalid_argument("Queue capacity must be positive");
}
PushResult BoundedQueue::push(std::span<const std::uint8_t> bytes) {
    std::lock_guard lock(mutex_);
    if (closed_) return PushResult::closed;
    if (bytes.size() > capacity_ - bytes_) return PushResult::full;
    if (bytes.empty()) return PushResult::accepted;
    items_.emplace_back(bytes.begin(), bytes.end());
    bytes_ += bytes.size(); peak_ = (std::max)(peak_, bytes_);
    ready_.notify_one();
    return PushResult::accepted;
}
bool BoundedQueue::pop(std::vector<std::uint8_t>& bytes) {
    std::unique_lock lock(mutex_);
    ready_.wait(lock, [&] { return closed_ || !items_.empty(); });
    if (items_.empty()) return false;
    bytes = std::move(items_.front()); items_.pop_front(); bytes_ -= bytes.size();
    return true;
}
void BoundedQueue::close() { std::lock_guard lock(mutex_); closed_ = true; ready_.notify_all(); }
std::size_t BoundedQueue::peak() const { std::lock_guard lock(mutex_); return peak_; }
std::size_t BoundedQueue::size() const { std::lock_guard lock(mutex_); return bytes_; }

FileOutput::FileOutput(const std::filesystem::path& path) {
    HANDLE handle = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW,
                                FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (handle == INVALID_HANDLE_VALUE) throw AppError(5, "Cannot create file " + utf8(path.native()) +
                                                       ": Win32 error " + std::to_string(GetLastError()));
    handle_ = handle;
}
FileOutput::~FileOutput() { if (handle_) CloseHandle(handle_); }
std::size_t FileOutput::write(std::span<const std::uint8_t> bytes) {
    if (!handle_) throw AppError(5, "Output file is closed");
    DWORD written = 0;
    DWORD size = static_cast<DWORD>((std::min)(bytes.size(), static_cast<std::size_t>(MAXDWORD)));
    if (!WriteFile(handle_, bytes.data(), size, &written, nullptr))
        throw AppError(5, "WriteFile failed: Win32 error " + std::to_string(GetLastError()));
    return written;
}
void FileOutput::flush() {
    if (!handle_ || !FlushFileBuffers(handle_)) throw AppError(5, "FlushFileBuffers failed: Win32 error " + std::to_string(GetLastError()));
}
void FileOutput::close() {
    if (handle_) {
        HANDLE handle = handle_; handle_ = nullptr;
        if (!CloseHandle(handle)) throw AppError(5, "CloseHandle failed: Win32 error " + std::to_string(GetLastError()));
    }
}

StreamProcessor::StreamProcessor(PacketLayout layout, ByteOutput& output)
    : analyzer_(layout), output_(output) {}
void StreamProcessor::write_all(std::span<const std::uint8_t> bytes) {
    while (!bytes.empty()) {
        auto count = output_.write(bytes);
        if (!count || count > bytes.size()) throw AppError(5, "Output returned an invalid write length");
        written_bytes_ += count;
        bytes = bytes.subspan(count);
    }
}
void StreamProcessor::process(std::span<const std::uint8_t> bytes) {
    // Analyze the input delivered to the writer; if writing fails, the report is
    // explicitly incomplete and byte counters show how much reached disk.
    analyzer_.feed(bytes);
    processed_input_bytes_ += bytes.size();
    write_all(bytes);
}
void StreamProcessor::finish() {
    analyzer_.finish();
    output_.flush();
    if (stats().trailing_bytes) throw AppError(4, "Stream ended with an incomplete transport packet (" +
                                                std::to_string(stats().trailing_bytes) + " trailing bytes)");
    if (stats().packets && stats().sync_errors + stats().malformed_packets == stats().packets)
        throw AppError(4, "Stream contains no structurally valid TS packets; original bytes were retained");
}
} // namespace capdvhs
