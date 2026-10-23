#pragma once
#include <array>
#include <cstdint>
#include <span>

namespace switchdesk {
constexpr std::uint32_t Magic = 0x314B4453;
constexpr std::uint16_t Protocol = 1;
constexpr std::uint64_t MaxFrame = 16 * 1024 * 1024;
constexpr std::uint32_t Chunk = 1024 * 1024;
enum class Kind : std::uint16_t { Request = 1, Response = 2, Binary = 3, ReadAt = 5, Progress = 6, Cancel = 7 };
struct Header { Kind kind{}; std::uint64_t size{}; };
inline auto Encode(Header value) -> std::array<std::uint8_t, 16> {
    std::array<std::uint8_t, 16> bytes{};
    for (unsigned i = 0; i < 4; ++i) bytes[i] = static_cast<std::uint8_t>(Magic >> (i * 8));
    bytes[4] = Protocol; bytes[6] = static_cast<std::uint8_t>(value.kind);
    for (unsigned i = 0; i < 8; ++i) bytes[8 + i] = static_cast<std::uint8_t>(value.size >> (i * 8));
    return bytes;
}
inline auto Decode(std::span<const std::uint8_t, 16> bytes, Header& header) -> bool {
    std::uint32_t magic{}; for (unsigned i = 0; i < 4; ++i) magic |= std::uint32_t(bytes[i]) << (i * 8);
    if (magic != Magic || bytes[4] != Protocol || bytes[5] || bytes[7]) return false;
    const auto kind = bytes[6]; if (kind != 1 && kind != 2 && kind != 3 && kind != 5 && kind != 6 && kind != 7) return false;
    header.kind = static_cast<Kind>(kind); header.size = 0;
    for (unsigned i = 0; i < 8; ++i) header.size |= std::uint64_t(bytes[8 + i]) << (i * 8);
    return header.size <= MaxFrame;
}
}
