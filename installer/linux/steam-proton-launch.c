// Steam expands %command% before invoking this wrapper. The legacy argument
// form remains supported for installed Reloaded II packages.
#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/stat.h>
#include <unistd.h>

static int regular_file(const char *path) {
    struct stat info;
    return stat(path, &info) == 0 && S_ISREG(info.st_mode);
}

static int directory(const char *path) {
    struct stat info;
    return stat(path, &info) == 0 && S_ISDIR(info.st_mode);
}

static int same_file(const char *left, const char *right) {
    struct stat a, b;
    return stat(left, &a) == 0 && stat(right, &b) == 0 &&
           a.st_dev == b.st_dev && a.st_ino == b.st_ino;
}

static int dll_name_equal(const char *left, size_t left_length,
                          const char *right, size_t right_length) {
    if (left_length >= 4 && strncasecmp(left + left_length - 4, ".dll", 4) == 0)
        left_length -= 4;
    if (right_length >= 4 && strncasecmp(right + right_length - 4, ".dll", 4) == 0)
        right_length -= 4;
    return left_length == right_length && strncasecmp(left, right, left_length) == 0;
}

static int merge_overrides(const char *requested) {
    if (strcmp(requested, "-") == 0) return 0;
    const char *previous = getenv("WINEDLLOVERRIDES");
    if (previous && *previous) {
        const char *new_rule = requested;
        while (*new_rule) {
            const char *new_end = strchr(new_rule, ';');
            if (!new_end) new_end = new_rule + strlen(new_rule);
            const char *new_equals = memchr(new_rule, '=', (size_t)(new_end - new_rule));
            if (!new_equals || new_equals == new_rule) return -1;
            const char *old_rule = previous;
            while (*old_rule) {
                const char *old_end = strchr(old_rule, ';');
                if (!old_end) old_end = old_rule + strlen(old_rule);
                const char *old_equals = memchr(old_rule, '=', (size_t)(old_end - old_rule));
                if (old_equals && dll_name_equal(new_rule, (size_t)(new_equals - new_rule),
                                                  old_rule, (size_t)(old_equals - old_rule)) &&
                    ((size_t)(new_end - new_equals) != (size_t)(old_end - old_equals) ||
                     strncmp(new_equals, old_equals, (size_t)(new_end - new_equals)) != 0)) {
                    fprintf(stderr, "Steam already sets a conflicting Wine DLL override for %.*s.\n",
                            (int)(new_equals - new_rule), new_rule);
                    return -1;
                }
                old_rule = *old_end ? old_end + 1 : old_end;
            }
            new_rule = *new_end ? new_end + 1 : new_end;
        }
    }
    size_t length = strlen(requested) + (previous && *previous ? strlen(previous) + 1 : 0) + 1;
    char *combined = malloc(length);
    if (!combined) return -1;
    if (previous && *previous)
        snprintf(combined, length, "%s;%s", requested, previous);
    else
        snprintf(combined, length, "%s", requested);
    int result = setenv("WINEDLLOVERRIDES", combined, 1);
    free(combined);
    return result;
}

static int prepend_bridge(const char *bridge) {
    if (strcmp(bridge, "-") == 0) return 0;
    const char *previous = getenv("WINEDLLPATH");
    size_t length = strlen(bridge) + (previous && *previous ? strlen(previous) + 1 : 0) + 1;
    char *combined = malloc(length);
    if (!combined) return -1;
    if (previous && *previous)
        snprintf(combined, length, "%s:%s", bridge, previous);
    else
        snprintf(combined, length, "%s", bridge);
    int result = setenv("WINEDLLPATH", combined, 1);
    free(combined);
    return result;
}

int main(int argc, char **argv) {
    int modern = argc > 1 && strcmp(argv[1], "--amm-v1") == 0;
    int command_start = modern ? 9 : 5;
    if (argc <= command_start || strcmp(argv[command_start - 1], "--") != 0) {
        fprintf(stderr, "Invalid Steam mod launch command. Reinstall the mod's Steam setup.\n");
        return 2;
    }
    const char *mode = modern ? argv[2] : "replaceExecutable";
    const char *game = modern ? argv[3] : argv[1];
    const char *launcher = modern ? argv[4] : argv[2];
    const char *bridge = modern ? argv[5] : argv[3];
    const char *overrides = modern ? argv[6] : "-";
    const char *reloaded = modern ? argv[7] : "1";
    if ((strcmp(mode, "direct") != 0 && strcmp(mode, "replaceExecutable") != 0) ||
        !regular_file(game) ||
        (strcmp(mode, "replaceExecutable") == 0 && !regular_file(launcher)) ||
        (strcmp(mode, "direct") == 0 && strcmp(launcher, "-") != 0) ||
        (strcmp(bridge, "-") != 0 && !directory(bridge)) ||
        (strcmp(reloaded, "0") != 0 && strcmp(reloaded, "1") != 0)) {
        fprintf(stderr, "Mod launch setup is incomplete or invalid. Check installed game and loader files.\n");
        return 2;
    }

    int match = -1;
    for (int i = command_start; i < argc; ++i) {
        if (same_file(argv[i], game)) {
            if (match != -1) {
                fprintf(stderr, "Steam command names the game executable more than once.\n");
                return 2;
            }
            match = i;
        }
    }
    if (match == -1) {
        fprintf(stderr, "Steam command does not contain the expected game executable.\n");
        return 2;
    }
    if (prepend_bridge(bridge) != 0 || merge_overrides(overrides) != 0 ||
        (strcmp(reloaded, "1") == 0 && setenv("AMM_RELOADED_CONFIG_READY", "1", 1) != 0)) {
        fprintf(stderr, "Could not prepare the mod loader environment.\n");
        return 2;
    }

    if (strcmp(mode, "direct") == 0) {
        execvp(argv[command_start], argv + command_start);
    } else {
        char **command = calloc((size_t)(argc - command_start + 2), sizeof(char *));
        if (!command) {
            perror("calloc");
            return 2;
        }
        int out = 0;
        for (int i = command_start; i < argc; ++i) {
            command[out++] = i == match ? (char *)launcher : argv[i];
            if (i == match) command[out++] = (char *)game;
        }
        command[out] = NULL;
        execvp(command[0], command);
    }
    fprintf(stderr, "Could not start Steam's Proton command: %s\n", strerror(errno));
    return 127;
}
