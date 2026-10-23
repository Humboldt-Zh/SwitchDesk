#include <cassert>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <memory>
extern "C" {
#include "../compat/build_argv.c"
}
int main() {
    auto storage = std::make_unique<char[]>(1024);
    const char text[] = "first\0second\0";
    std::memcpy(storage.get(), text, sizeof(text) - 1);
    __argv args{};
    args.commandLine = storage.get(); args.length = sizeof(text) - 1;
    build_argv(&args);
    assert(args.argc == 2 && std::strcmp(args.argv[0], "first") == 0 && std::strcmp(args.argv[1], "second") == 0);
    assert(args.argv[2] == nullptr && args.endARGV == &args.argv[3]);
    assert(reinterpret_cast<std::uintptr_t>(args.argv) >= reinterpret_cast<std::uintptr_t>(storage.get()));
    assert(reinterpret_cast<std::uintptr_t>(args.argv) % alignof(char*) == 0);
    args.length = 0; build_argv(&args); assert(args.argc == 0 && args.argv == nullptr && args.endARGV == nullptr);
    args.commandLine = nullptr; args.length = 8; build_argv(&args); assert(args.argc == 0);
    build_argv(nullptr);
    std::cout << "newlib argv pointer-width, alignment and empty-input checks passed\n";
}
