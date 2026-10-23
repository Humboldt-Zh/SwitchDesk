#include "switchdesk/wire.hpp"
#include "switchdesk/safety.hpp"
#include <cassert>
#include <iostream>
#include <random>
int main() {
    using namespace switchdesk;
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
