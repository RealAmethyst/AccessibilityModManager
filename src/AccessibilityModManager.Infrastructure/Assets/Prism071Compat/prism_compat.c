// Prism 0.7.1 to 0.18.3 C ABI adapter. Prism 0.18.3 adds Orca and Speech
// Dispatcher under Wine, but its PrismConfig grew from version 1 (16 bytes) to
// version 4 (56 bytes). prism_config_init returns the struct by value, so a
// 0.7.1 consumer receiving the newer struct has 40 bytes of its stack
// overwritten. This module keeps the 0.7.1 ABI at prism.dll and forwards every
// other export, whose signatures are unchanged between the two versions, to
// the verified 0.18.3 build loaded as prism-core.dll beside it.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <wchar.h>

typedef struct PrismContext PrismContext;
typedef struct PrismBackend PrismBackend;
typedef uint64_t PrismBackendId;
typedef int PrismError;
typedef void (*PrismAudioCallback)(void *, const float *, size_t, size_t, size_t);

// PRISM_ERROR_BACKEND_NOT_AVAILABLE in both versions.
#define PRISM_COMPAT_UNAVAILABLE 16

typedef struct {
    uint8_t version;
    HWND hwnd;
} PrismConfigV1;

_Static_assert(sizeof(PrismConfigV1) == 16, "Prism 0.7.1 config ABI changed");

static INIT_ONCE core_once = INIT_ONCE_STATIC_INIT;
static HMODULE core;

static BOOL CALLBACK load_core(PINIT_ONCE once, PVOID parameter, PVOID *context)
{
    (void)once;
    (void)parameter;
    (void)context;
    HMODULE self = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)(const void *)&load_core, &self))
        return TRUE;
    wchar_t path[32768];
    DWORD length = GetModuleFileNameW(self, path, 32768);
    if (!length || length >= 32768) return TRUE;
    wchar_t *slash = wcsrchr(path, L'\\');
    if (!slash || (size_t)(slash - path) + 16 >= 32768) return TRUE;
    wcscpy(slash + 1, L"prism-core.dll");
    core = LoadLibraryW(path);
    return TRUE;
}

static FARPROC symbol(const char *name)
{
    InitOnceExecuteOnce(&core_once, load_core, NULL, NULL);
    return core ? GetProcAddress(core, name) : NULL;
}

#define FORWARD(ret, name, fallback, params, args)          \
    __declspec(dllexport) ret name params                   \
    {                                                       \
        typedef ret (*Fn) params;                           \
        Fn fn = (Fn)symbol(#name);                          \
        return fn ? fn args : fallback;                     \
    }

#define FORWARD_VOID(name, params, args)                    \
    __declspec(dllexport) void name params                  \
    {                                                       \
        typedef void (*Fn) params;                          \
        Fn fn = (Fn)symbol(#name);                          \
        if (fn) fn args;                                    \
    }

__declspec(dllexport) PrismConfigV1 prism_config_init(void)
{
    PrismConfigV1 config = {0};
    config.version = 1;
    return config;
}

// Version 1 has no field that Prism reads; 0.18.3 uses its defaults for it.
__declspec(dllexport) PrismContext *prism_init(PrismConfigV1 *config)
{
    typedef PrismContext *(*Fn)(void *);
    Fn fn = (Fn)symbol("prism_init");
    if (!fn || (config && config->version != 1)) return NULL;
    return fn(NULL);
}

FORWARD_VOID(prism_shutdown, (PrismContext *ctx), (ctx))
FORWARD(size_t, prism_registry_count, 0, (PrismContext *ctx), (ctx))
FORWARD(PrismBackendId, prism_registry_id_at, 0, (PrismContext *ctx, size_t index), (ctx, index))
FORWARD(PrismBackendId, prism_registry_id, 0, (PrismContext *ctx, const char *name), (ctx, name))
FORWARD(const char *, prism_registry_name, NULL, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(int, prism_registry_priority, 0, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(bool, prism_registry_exists, false, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(PrismBackend *, prism_registry_get, NULL, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(PrismBackend *, prism_registry_create, NULL, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(PrismBackend *, prism_registry_create_best, NULL, (PrismContext *ctx), (ctx))
FORWARD(PrismBackend *, prism_registry_acquire, NULL, (PrismContext *ctx, PrismBackendId id), (ctx, id))
FORWARD(PrismBackend *, prism_registry_acquire_best, NULL, (PrismContext *ctx), (ctx))
FORWARD_VOID(prism_backend_free, (PrismBackend *backend), (backend))
FORWARD(const char *, prism_backend_name, NULL, (PrismBackend *backend), (backend))
FORWARD(uint64_t, prism_backend_get_features, 0, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_initialize, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_speak, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, const char *text, bool interrupt), (backend, text, interrupt))
FORWARD(PrismError, prism_backend_speak_to_memory, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, const char *text, PrismAudioCallback callback, void *userdata), (backend, text, callback, userdata))
FORWARD(PrismError, prism_backend_braille, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, const char *text), (backend, text))
FORWARD(PrismError, prism_backend_output, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, const char *text, bool interrupt), (backend, text, interrupt))
FORWARD(PrismError, prism_backend_stop, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_pause, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_resume, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_is_speaking, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, bool *out_speaking), (backend, out_speaking))
FORWARD(PrismError, prism_backend_set_volume, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float volume), (backend, volume))
FORWARD(PrismError, prism_backend_set_rate, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float rate), (backend, rate))
FORWARD(PrismError, prism_backend_set_pitch, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float pitch), (backend, pitch))
FORWARD(PrismError, prism_backend_get_volume, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float *out_volume), (backend, out_volume))
FORWARD(PrismError, prism_backend_get_rate, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float *out_rate), (backend, out_rate))
FORWARD(PrismError, prism_backend_get_pitch, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, float *out_pitch), (backend, out_pitch))
FORWARD(PrismError, prism_backend_refresh_voices, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend), (backend))
FORWARD(PrismError, prism_backend_count_voices, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t *out_count), (backend, out_count))
FORWARD(PrismError, prism_backend_get_voice_name, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t voice_id, const char **out_name), (backend, voice_id, out_name))
FORWARD(PrismError, prism_backend_get_voice_language, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t voice_id, const char **out_language), (backend, voice_id, out_language))
FORWARD(PrismError, prism_backend_set_voice, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t voice_id), (backend, voice_id))
FORWARD(PrismError, prism_backend_get_voice, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t *out_voice_id), (backend, out_voice_id))
FORWARD(PrismError, prism_backend_get_channels, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t *out_channels), (backend, out_channels))
FORWARD(PrismError, prism_backend_get_sample_rate, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t *out_sample_rate), (backend, out_sample_rate))
FORWARD(PrismError, prism_backend_get_bit_depth, PRISM_COMPAT_UNAVAILABLE, (PrismBackend *backend, size_t *out_bit_depth), (backend, out_bit_depth))
FORWARD(const char *, prism_error_string, "Prism core unavailable", (PrismError error), (error))
