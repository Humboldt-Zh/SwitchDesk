#pragma once
#include <cstdint>
#include <limits>
#include <string_view>

namespace switchdesk {
// Deliberately conservative: reject instead of making intermediate savedata commits.
constexpr auto RestoreFits(std::uint64_t capacity, std::uint64_t journal, std::uint64_t old_bytes,
    std::uint64_t new_bytes, std::uint64_t entries) -> bool {
    constexpr auto maximum = std::numeric_limits<std::uint64_t>::max();
    if (entries > 100000 || entries > (maximum - 1024 * 1024) / 4096) return false;
    const auto overhead = 1024 * 1024 + entries * 4096;
    if (new_bytes > maximum - overhead || old_bytes > maximum - new_bytes - overhead) return false;
    return new_bytes + overhead <= capacity && old_bytes + new_bytes + overhead <= journal;
}
constexpr auto ValidOperationId(std::string_view id) -> bool {
    if (id.size() != 32) return false;
    for (const auto c : id) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
    return true;
}
}
