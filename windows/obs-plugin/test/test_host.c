// Loads the real plugin DLL (with fake_obs.c as its obs.dll), creates the source for one camera, lets it run while a
// receiver replays a test stream, then checks what reached "OBS": the right format, enough samples, and no gaps.
// The test stream's samples are a ramp (0, 1, 2, ... 32767, 0, ...), so every missing or repeated sample shows.
// usage: test_host.exe CAMERA SECONDS MIN_SAMPLES     exit code 0 = pass
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <obs-module.h>

typedef bool (*load_fn)(void);
typedef const struct obs_source_info *(*registered_fn)(void);
typedef void (*set_camera_fn)(long long);
typedef const short *(*samples_fn)(size_t *, size_t *, int *);

int main(int argc, char **argv)
{
    if (argc < 4) { printf("usage: test_host CAMERA SECONDS MIN_SAMPLES\n"); return 2; }
    long long camera = atoll(argv[1]);
    int seconds = atoi(argv[2]);
    size_t minimum = (size_t)atoll(argv[3]);

    HMODULE plugin = LoadLibraryW(L"streamsthetic-mic.dll");
    if (!plugin) { printf("FAIL: plugin did not load (%lu)\n", GetLastError()); return 1; }
    HMODULE obs = GetModuleHandleW(L"obs.dll");
    load_fn load = (load_fn)GetProcAddress(plugin, "obs_module_load");
    registered_fn registered = (registered_fn)GetProcAddress(obs, "fake_registered");
    set_camera_fn set_camera = (set_camera_fn)GetProcAddress(obs, "fake_set_camera");
    samples_fn samples = (samples_fn)GetProcAddress(obs, "fake_samples");
    if (!load || !registered || !set_camera || !samples) { printf("FAIL: missing entry points\n"); return 1; }

    if (!load()) { printf("FAIL: obs_module_load returned false\n"); return 1; }
    const struct obs_source_info *info = registered();
    if (!info || strcmp(info->id, "streamsthetic_phone_mic") != 0 || info->type != OBS_SOURCE_TYPE_INPUT ||
        !(info->output_flags & OBS_SOURCE_AUDIO)) { printf("FAIL: source not registered as an audio input\n"); return 1; }
    printf("registered: %s (%s)\n", info->id, info->get_name(NULL));
    if (!info->get_properties(NULL)) { printf("FAIL: no properties\n"); return 1; }

    set_camera(camera);
    void *source = info->create(NULL, (obs_source_t *)&camera);
    printf("source created for camera %lld; listening %d s\n", camera, seconds);
    fflush(stdout);
    Sleep((DWORD)seconds * 1000);
    info->destroy(source);

    size_t count = 0, calls = 0;
    int badFormat = 0;
    const short *s = samples(&count, &calls, &badFormat);
    size_t breaks = 0;
    for (size_t i = 1; i < count; i++)
        if (s[i] != (short)((s[i - 1] + 1) & 0x7FFF)) breaks++;
    printf("received %zu samples in %zu hand-overs; format errors %d; gaps %zu\n", count, calls, badFormat, breaks);
    if (badFormat || breaks || count < minimum) { printf("FAIL\n"); return 1; }
    printf("PASS\n");
    return 0;
}
