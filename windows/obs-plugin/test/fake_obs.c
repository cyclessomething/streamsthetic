// Test double for obs.dll: the same functions the plugin imports (obs.def), built from the real libobs headers so the
// structures match. It remembers the registered source and every audio hand-over, for test_host.c to inspect.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <obs-module.h>
#include <util/platform.h>

static struct obs_source_info g_info;
static long long g_camera = 1;
static CRITICAL_SECTION g_lock;
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
static short g_samples[48000 * 30];
static size_t g_count;
static int g_badFormat;
static size_t g_calls;
static int g_dummy;

static BOOL CALLBACK init_lock(PINIT_ONCE once, PVOID param, PVOID *context)
{
    (void)once; (void)param; (void)context;
    InitializeCriticalSection(&g_lock);
    return TRUE;
}

static void lock(void) { InitOnceExecuteOnce(&g_once, init_lock, NULL, NULL); EnterCriticalSection(&g_lock); }

void blog(int level, const char *format, ...)
{
    va_list args;
    va_start(args, format);
    printf("obs log %d: ", level);
    vprintf(format, args);
    printf("\n");
    va_end(args);
}

uint64_t os_gettime_ns(void)
{
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);
    return (uint64_t)((double)now.QuadPart * 1e9 / (double)freq.QuadPart);
}

long long obs_data_get_int(obs_data_t *data, const char *name)
{
    (void)data;
    return strcmp(name, "camera") == 0 ? g_camera : 0;
}

void obs_data_set_default_int(obs_data_t *data, const char *name, long long val) { (void)data; (void)name; (void)val; }
obs_properties_t *obs_properties_create(void) { return (obs_properties_t *)&g_dummy; }

obs_property_t *obs_properties_add_list(obs_properties_t *props, const char *name, const char *description,
                                        enum obs_combo_type type, enum obs_combo_format format)
{
    (void)props; (void)name; (void)description; (void)type; (void)format;
    return (obs_property_t *)&g_dummy;
}

obs_property_t *obs_properties_add_text(obs_properties_t *props, const char *name, const char *description,
                                        enum obs_text_type type)
{
    (void)props; (void)name; (void)description; (void)type;
    return (obs_property_t *)&g_dummy;
}

size_t obs_property_list_add_int(obs_property_t *p, const char *name, long long val) { (void)p; (void)name; (void)val; return 0; }

void obs_register_source_s(const struct obs_source_info *info, size_t size)
{
    memcpy(&g_info, info, min(size, sizeof(g_info)));
}

void obs_source_output_audio(obs_source_t *source, const struct obs_source_audio *audio)
{
    (void)source;
    lock();
    g_calls++;
    if (audio->speakers != SPEAKERS_MONO || audio->format != AUDIO_FORMAT_16BIT || audio->samples_per_sec != 48000)
        g_badFormat++;
    const short *in = (const short *)audio->data[0];
    for (uint32_t i = 0; i < audio->frames && g_count < sizeof(g_samples) / sizeof(g_samples[0]); i++)
        g_samples[g_count++] = in[i];
    LeaveCriticalSection(&g_lock);
}

// for the test host only
EXPORT const struct obs_source_info *fake_registered(void) { return g_info.id ? &g_info : NULL; }
EXPORT void fake_set_camera(long long camera) { g_camera = camera; }
EXPORT const short *fake_samples(size_t *count, size_t *calls, int *badFormat)
{
    lock();
    *count = g_count;
    *calls = g_calls;
    *badFormat = g_badFormat;
    LeaveCriticalSection(&g_lock);
    return g_samples;
}
