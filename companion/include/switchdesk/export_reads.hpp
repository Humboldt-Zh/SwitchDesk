#pragma once
#include <concepts>
#include <cstdint>
#include <span>

namespace switchdesk {
struct ExportSegmentRead {
    std::uint32_t error{};
    std::uint64_t count{};
};
enum class ExportReadState { Complete, OutOfRange, ReadError, NoProgress, InvalidCount };
struct ExportReadResult {
    ExportReadState state{};
    std::uint32_t error{};
    std::uint64_t completed{};
    std::uint64_t reported{};
};

// NspEntry::Read may stop at the header or a collection boundary. Fill the
// requested protocol chunk without treating those successful short reads as EOF.
template<typename Reader>
requires requires(Reader& reader, std::span<std::uint8_t> target, std::uint64_t position) {
    { reader(target, position) } -> std::same_as<ExportSegmentRead>;
}
auto ReadExportChunk(std::span<std::uint8_t> target, std::uint64_t offset,
    std::uint64_t size, Reader&& reader) -> ExportReadResult {
    if (offset > size || target.size() > size - offset) return {ExportReadState::OutOfRange};
    std::uint64_t completed{};
    while (completed < target.size()) {
        const auto remaining = target.subspan(static_cast<std::size_t>(completed));
        const auto read = reader(remaining, offset + completed);
        if (read.error != 0) return {ExportReadState::ReadError, read.error, completed, read.count};
        if (read.count == 0) return {ExportReadState::NoProgress, 0, completed};
        if (read.count > remaining.size()) return {ExportReadState::InvalidCount, 0, completed, read.count};
        completed += read.count;
    }
    return {ExportReadState::Complete, 0, completed};
}
}
