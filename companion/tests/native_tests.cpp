#include "switchdesk/wire.hpp"
#include "switchdesk/safety.hpp"
#include "switchdesk/restore_policy.hpp"
#include "switchdesk/export_reads.hpp"
#include <algorithm>
#include <cassert>
#include <iostream>
#include <random>
#include <vector>

namespace {
auto ExportReadTests() -> void {
    using namespace switchdesk;
    // A short header, two NCA-like regions, and small ticket/certificate regions.
    const std::array<std::uint64_t, 6> ends{320, Chunk + 123, Chunk + 127, 2 * Chunk + 19, 2 * Chunk + 27, 2 * Chunk + 43};
    const auto size = ends.back();
    std::vector<std::uint8_t> buffer(Chunk);
    std::uint64_t calls{};
    const auto segmented = [&](std::span<std::uint8_t> target, std::uint64_t position) -> ExportSegmentRead {
        ++calls;
        const auto end = std::find_if(ends.begin(), ends.end(), [position](auto value) { return value > position; });
        assert(end != ends.end());
        const auto count = std::min<std::uint64_t>(target.size(), *end - position);
        for (std::uint64_t i = 0; i < count; ++i) target[static_cast<std::size_t>(i)] = static_cast<std::uint8_t>(position + i);
        return {0, count};
    };
    for (std::uint64_t offset = 0; offset < size;) {
        const auto count = std::min<std::uint64_t>(buffer.size(), size - offset);
        const auto read = ReadExportChunk(std::span(buffer).first(static_cast<std::size_t>(count)), offset, size, segmented);
        assert(read.state == ExportReadState::Complete && read.completed == count);
        for (std::uint64_t i = 0; i < count; ++i) assert(buffer[static_cast<std::size_t>(i)] == static_cast<std::uint8_t>(offset + i));
        offset += count;
    }
    assert(calls > 3); // Three protocol chunks require additional segment reads.
    for (const auto offset : std::array<std::uint64_t, 4>{0, 320, Chunk + 123, size - 1}) {
        const auto count = std::min<std::uint64_t>(19, size - offset);
        const auto read = ReadExportChunk(std::span(buffer).first(static_cast<std::size_t>(count)), offset, size, segmented);
        assert(read.state == ExportReadState::Complete && read.completed == count);
        for (std::uint64_t i = 0; i < count; ++i) assert(buffer[static_cast<std::size_t>(i)] == static_cast<std::uint8_t>(offset + i));
    }
    const auto before_empty = calls;
    assert(ReadExportChunk(std::span(buffer).first(0), size, size, segmented).state == ExportReadState::Complete);
    assert(calls == before_empty);
    assert(ReadExportChunk(std::span(buffer).first(1), size, size, segmented).state == ExportReadState::OutOfRange);
    assert(ReadExportChunk(std::span(buffer).first(0), size + 1, size, segmented).state == ExportReadState::OutOfRange);
    assert(ReadExportChunk(std::span(buffer).first(2), UINT64_MAX - 1, UINT64_MAX, segmented).state == ExportReadState::OutOfRange);
    assert(calls == before_empty);

    // Exercise crossing 4 GiB with a tiny buffer and nonzero destination offset.
    std::array<std::uint8_t, 32> guarded{}; guarded.fill(0xEE);
    constexpr auto large_offset = (1ULL << 32) - 8;
    auto target = std::span(guarded).subspan(3, 21);
    std::uint64_t high_calls{};
    std::uint64_t high_completed{};
    const auto high = ReadExportChunk(target, large_offset, large_offset + target.size(),
        [&](std::span<std::uint8_t> part, std::uint64_t position) -> ExportSegmentRead {
            assert(position == large_offset + high_completed);
            const auto count = std::min<std::size_t>(5, part.size()); ++high_calls;
            for (std::size_t i = 0; i < count; ++i) part[i] = static_cast<std::uint8_t>(position + i);
            high_completed += count;
            return {0, count};
        });
    assert(high.state == ExportReadState::Complete && high.completed == target.size() && high_calls == 5);
    for (std::size_t i = 0; i < target.size(); ++i) assert(target[i] == static_cast<std::uint8_t>(large_offset + i));
    for (std::size_t i = 0; i < guarded.size(); ++i) if (i < 3 || i >= 24) assert(guarded[i] == 0xEE);

    auto stalled = ReadExportChunk(target, 0, target.size(), [](auto, auto) -> ExportSegmentRead { return {}; });
    assert(stalled.state == ExportReadState::NoProgress && stalled.completed == 0);
    std::uint64_t attempts{};
    const auto partial_stall = ReadExportChunk(target, 0, target.size(), [&](auto, auto) -> ExportSegmentRead { return {0, ++attempts == 1 ? 2ULL : 0ULL}; });
    assert(partial_stall.state == ExportReadState::NoProgress && partial_stall.completed == 2 && attempts == 2);
    attempts = 0;
    const auto failed = ReadExportChunk(target, 0, target.size(), [&](auto, auto) -> ExportSegmentRead { return ++attempts == 1 ? ExportSegmentRead{0, 2} : ExportSegmentRead{0x1234, 5}; });
    assert(failed.state == ExportReadState::ReadError && failed.error == 0x1234 && failed.completed == 2 && failed.reported == 5 && attempts == 2);
    const auto oversized = ReadExportChunk(target, 0, target.size(), [](auto part, auto) -> ExportSegmentRead { return {0, part.size() + 1}; });
    assert(oversized.state == ExportReadState::InvalidCount && oversized.completed == 0 && oversized.reported == target.size() + 1);
    std::cout << "Export reads passed: segmented NSP chunks, boundaries/tail, 64-bit offsets, bounds, no-progress, source errors and invalid counts\n";
}
}

int main() {
    using namespace switchdesk;
    ExportReadTests();
    assert(RestoreFits(64ULL << 20, 64ULL << 20, 1ULL << 20, 1ULL << 20, 10));
    assert(!RestoreFits(64ULL << 20, 1ULL << 20, 1ULL << 20, 1ULL << 20, 10));
    assert(!RestoreFits(1024, 64ULL << 20, 10, 10, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, UINT64_MAX, 1, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, 1, UINT64_MAX, 1));
    assert(!RestoreFits(UINT64_MAX, UINT64_MAX, 1, 1, UINT64_MAX));
    assert(ValidOperationId("0123456789abcdef0123456789ABCDEF"));
    assert(!ValidOperationId("../operations") && !ValidOperationId(std::string(32, 'x')));
    assert(SameDigest(std::string(64, 'a'), std::string(64, 'A')));
    assert(!SameDigest(std::string(64, 'G'), std::string(64, 'g')));
    assert(!SameDigest("", "") && !SameDigest(std::string(64, 'A'), std::string(64, 'B')));
    assert(ValidRemote("/")); assert(ValidRelative("nested/save.dat")); assert(!ValidRelative("../x")); assert(!ValidRemote("/x/../Nintendo"));
    assert(!Writable("/EMUmmc/raw1")); assert(!Writable("/switch/SwitchDesk/staging/x")); assert(Writable("/switch/tools/config.ini"));
    const auto packet = Encode({Kind::Request, 123}); assert(packet[0] == 0x53 && packet[1] == 0x44 && packet[8] == 123);
    Header header{}; assert(Decode(packet, header) && header.size == 123 && header.kind == Kind::Request);
    auto invalid = packet; invalid[4] = 2; assert(!Decode(invalid, header));
    auto cancel = Encode({Kind::Cancel, 0}); assert(Decode(cancel, header) && header.kind == Kind::Cancel && header.size == 0);
    auto too_large = Encode({Kind::Binary, 5ULL << 30}); assert(!Decode(too_large, header));
    std::mt19937_64 rng(873);
    for (int i = 0; i < 100000; ++i) { const auto size = rng() % (MaxFrame + 1); auto encoded = Encode({Kind::Binary, size}); assert(Decode(encoded, header) && header.size == size); }
    std::cout << "Native wire/path checks passed (100000 deterministic roundtrips)\n";
}
