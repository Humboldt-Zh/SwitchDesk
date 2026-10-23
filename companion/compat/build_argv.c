/* Based on newlib/libgloss/libsysbase/build_argv.c at
 * R-YaTian/newlib 8e4d4166b043622b1fe463dbdb8255a05f0d56b2.
 * Preserve pointer width on AArch64 and handle empty argument data.
 * Distributed under the upstream newlib/libgloss license terms. */
#include <stdint.h>

struct __argv {
    int argvMagic;
    char *commandLine;
    int length;
    int argc;
    char **argv;
    char **endARGV;
};

void build_argv(struct __argv *argstruct) {
    if (!argstruct) return;
    if (!argstruct->commandLine || argstruct->length <= 0) {
        argstruct->argc = 0;
        argstruct->argv = 0;
        argstruct->endARGV = 0;
        return;
    }
    char *data = argstruct->commandLine;
    const int len = argstruct->length;
    char **argv = (char **)(((uintptr_t)data + (uintptr_t)len + sizeof(char **)) & ~(uintptr_t)(sizeof(char **) - 1));
    char *end = data + len - 1;
    int count = 0;
    do {
        argv[count++] = data;
        while (data < end && *data) data++;
        data++;
    } while (data < end);
    *end = '\0';
    argv[count] = 0;
    argstruct->argv = argv;
    argstruct->argc = count;
    argstruct->endARGV = &argv[count + 1];
}
