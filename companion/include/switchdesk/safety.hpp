#pragma once
#include <algorithm>
#include <cctype>
#include <string>
#include <string_view>

namespace switchdesk {
inline auto SameDigest(std::string_view left, std::string_view right) -> bool {
    return left.size() == 64 && right.size() == 64 && std::equal(left.begin(), left.end(), right.begin(), [](unsigned char a, unsigned char b) {
        return std::isxdigit(a) && std::isxdigit(b) && std::toupper(a) == std::toupper(b);
    });
}
inline auto ValidRelative(std::string_view path) -> bool {
    if (path.empty() || path.front() == '/' || path.size() > 700 || path.find_first_of("\\:") != path.npos || path.find('\0') != path.npos) return false;
    for (std::size_t start = 0; start <= path.size();) {
        auto end = path.find('/', start); if (end == path.npos) end = path.size();
        const auto part = path.substr(start, end - start);
        if (part.empty() || part == "." || part == "..") return false;
        if (end == path.size()) return true;
        start = end + 1;
    }
    return false;
}
inline auto ValidRemote(std::string_view path) -> bool { return path == "/" || (path.starts_with('/') && ValidRelative(path.substr(1))); }
inline auto Writable(std::string_view path) -> bool {
    if (!ValidRemote(path) || path == "/") return false;
    auto lower = std::string(path); std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    for (const auto root : {"/nintendo", "/emummc", "/atmosphere", "/bootloader", "/switch/switchdesk", "/config/switchdesk"})
        if (lower == root || lower.starts_with(std::string(root) + '/')) return false;
    for (const auto file : {"/exosphere.ini", "/hekate_ipl.ini", "/hbmenu.nro", "/boot.dat", "/boot.ini"}) if (lower == file) return false;
    return true;
}
}
