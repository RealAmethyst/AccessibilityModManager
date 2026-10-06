// Prism 0.17.3 to 0.18.3 C ABI adapter for the audited Cyber Sleuth plugin.
// The 0.17.3 Windows release omits Orca and Speech Dispatcher. The 0.18.3
// release includes them, but adds a field to PrismConfig. Keep the old ABI at
// prism.dll and load the verified 0.18.3 build as prism-core.dll beside it.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <wchar.h>

typedef struct PrismContext PrismContext;
typedef struct PrismBackend PrismBackend;
typedef struct PrismRegistry PrismRegistry;
typedef uint64_t PrismBackendId;
typedef void (*PrismAvailabilityCallback)(void *, PrismBackendId, const char *, bool);
typedef void (*PrismAvailabilityBaselineCallback)(void *);

typedef struct {
    uint8_t version;
    PrismRegistry *registry;
    PrismAvailabilityCallback availability_callback;
    void *availability_userdata;
    uint32_t availability_poll_interval_ms;
    uint32_t availability_debounce_samples;
    uint32_t availability_backoff_max_ms;
    bool availability_auto_power_manage;
} PrismConfigV3;

typedef struct {
    uint8_t version;
    PrismRegistry *registry;
    PrismAvailabilityCallback availability_callback;
    void *availability_userdata;
    uint32_t availability_poll_interval_ms;
    uint32_t availability_debounce_samples;
    uint32_t availability_backoff_max_ms;
    bool availability_auto_power_manage;
    PrismAvailabilityBaselineCallback availability_baseline_callback;
} PrismConfigV4;

_Static_assert(sizeof(PrismConfigV3) == 48, "Prism 0.17.3 config ABI changed");
_Static_assert(sizeof(PrismConfigV4) == 56, "Prism 0.18.3 config ABI changed");
_Static_assert(offsetof(PrismConfigV4, availability_baseline_callback) == 48,
               "Prism config extension changed");

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

__declspec(dllexport) PrismConfigV3 prism_config_init(void)
{
    PrismConfigV3 config = {0};
    config.version = 3;
    return config;
}

__declspec(dllexport) PrismContext *prism_init(PrismConfigV3 *config)
{
    typedef PrismContext *(*Fn)(PrismConfigV4 *);
    Fn fn = (Fn)symbol("prism_init");
    if (!fn) return NULL;
    if (!config) return fn(NULL);
    if (config->version != 3) return NULL;
    PrismConfigV4 converted = {0};
    converted.version = 4;
    converted.registry = config->registry;
    converted.availability_callback = config->availability_callback;
    converted.availability_userdata = config->availability_userdata;
    converted.availability_poll_interval_ms = config->availability_poll_interval_ms;
    converted.availability_debounce_samples = config->availability_debounce_samples;
    converted.availability_backoff_max_ms = config->availability_backoff_max_ms;
    converted.availability_auto_power_manage = config->availability_auto_power_manage;
    return fn(&converted);
}

__declspec(dllexport) void prism_shutdown(PrismContext *context)
{
    typedef void (*Fn)(PrismContext *);
    Fn fn = (Fn)symbol("prism_shutdown");
    if (fn) fn(context);
}

__declspec(dllexport) size_t prism_registry_count(PrismContext *context)
{
    typedef size_t (*Fn)(PrismContext *);
    Fn fn = (Fn)symbol("prism_registry_count");
    return fn ? fn(context) : 0;
}

__declspec(dllexport) PrismBackendId prism_registry_id_at(PrismContext *context, size_t index)
{
    typedef PrismBackendId (*Fn)(PrismContext *, size_t);
    Fn fn = (Fn)symbol("prism_registry_id_at");
    return fn ? fn(context, index) : 0;
}

__declspec(dllexport) const char *prism_registry_name(PrismContext *context, PrismBackendId id)
{
    typedef const char *(*Fn)(PrismContext *, PrismBackendId);
    Fn fn = (Fn)symbol("prism_registry_name");
    return fn ? fn(context, id) : NULL;
}

__declspec(dllexport) PrismBackend *prism_registry_create_best(PrismContext *context)
{
    typedef PrismBackend *(*Fn)(PrismContext *);
    Fn fn = (Fn)symbol("prism_registry_create_best");
    return fn ? fn(context) : NULL;
}

__declspec(dllexport) int prism_backend_initialize(PrismBackend *backend)
{
    typedef int (*Fn)(PrismBackend *);
    Fn fn = (Fn)symbol("prism_backend_initialize");
    return fn ? fn(backend) : 16;
}

__declspec(dllexport) const char *prism_backend_name(PrismBackend *backend)
{
    typedef const char *(*Fn)(PrismBackend *);
    Fn fn = (Fn)symbol("prism_backend_name");
    return fn ? fn(backend) : NULL;
}

__declspec(dllexport) uint64_t prism_backend_get_features(PrismBackend *backend)
{
    typedef uint64_t (*Fn)(PrismBackend *);
    Fn fn = (Fn)symbol("prism_backend_get_features");
    return fn ? fn(backend) : 0;
}

__declspec(dllexport) int prism_backend_output(PrismBackend *backend,
                                              const char *text, bool interrupt)
{
    typedef int (*Fn)(PrismBackend *, const char *, bool);
    Fn fn = (Fn)symbol("prism_backend_output");
    return fn ? fn(backend, text, interrupt) : 16;
}

__declspec(dllexport) int prism_backend_speak(PrismBackend *backend,
                                             const char *text, bool interrupt)
{
    typedef int (*Fn)(PrismBackend *, const char *, bool);
    Fn fn = (Fn)symbol("prism_backend_speak");
    return fn ? fn(backend, text, interrupt) : 16;
}

__declspec(dllexport) int prism_backend_stop(PrismBackend *backend)
{
    typedef int (*Fn)(PrismBackend *);
    Fn fn = (Fn)symbol("prism_backend_stop");
    return fn ? fn(backend) : 16;
}

__declspec(dllexport) void prism_backend_free(PrismBackend *backend)
{
    typedef void (*Fn)(PrismBackend *);
    Fn fn = (Fn)symbol("prism_backend_free");
    if (fn) fn(backend);
}

__declspec(dllexport) const char *prism_error_string(int error)
{
    typedef const char *(*Fn)(int);
    Fn fn = (Fn)symbol("prism_error_string");
    return fn ? fn(error) : "Prism core unavailable";
}
