// OBS source "Streamsthetic phone microphone": the sound from a phone's microphone, straight into OBS, with no
// virtual audio cable and no driver. StreamstheticReceiver.exe puts each phone's audio in shared memory
// (shared_audio.h); this source reads the block of the chosen camera (1-4) and hands the samples to OBS.
// Built against libobs 30.0.0 headers (loads in OBS 30 and newer); links only to the few obs.dll functions listed in obs.def.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <obs-module.h>
#include <util/platform.h>
#include "shared_audio.h"

OBS_DECLARE_MODULE()

#define CHUNK 4800 /* at most 100 ms per hand-over */

struct phone_mic {
    obs_source_t *source;
    volatile LONG slot;    /* camera 1-4; set from OBS's thread, read by ours */
    volatile LONG running;
    HANDLE thread;
};

static const char *phone_mic_name(void *unused)
{
    UNUSED_PARAMETER(unused);
    return "Streamsthetic phone microphone";
}

static void close_block(HANDLE *mapping, const StreamstheticSharedAudio **shared)
{
    if (*shared) UnmapViewOfFile(*shared);
    if (*mapping) CloseHandle(*mapping);
    *shared = NULL;
    *mapping = NULL;
}

static DWORD WINAPI phone_mic_thread(LPVOID param)
{
    struct phone_mic *mic = param;
    HANDLE mapping = NULL;
    const StreamstheticSharedAudio *shared = NULL;
    LONG openSlot = 0;
    int64_t position = -1;
    int16_t *buffer = malloc(CHUNK * sizeof(int16_t));
    DWORD lastTry = 0;
    if (!buffer) return 1;

    while (mic->running) {
        LONG slot = mic->slot;
        if (shared && slot != openSlot) close_block(&mapping, &shared); /* camera changed in the properties */
        if (!shared) {
            DWORD now = GetTickCount();
            if (lastTry && now - lastTry < 500) { Sleep(20); continue; }
            lastTry = now;
            wchar_t name[64];
            swprintf(name, 64, STREAMSTHETIC_AUDIO_NAME, (unsigned)slot);
            mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
            if (!mapping) continue; /* no phone sound on this camera yet */
            shared = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(StreamstheticSharedAudio));
            if (!shared || shared->magic != STREAMSTHETIC_AUDIO_MAGIC || shared->ring != STREAMSTHETIC_AUDIO_RING) {
                close_block(&mapping, &shared);
                continue;
            }
            openSlot = slot;
            position = -1;
            blog(LOG_INFO, "[streamsthetic] reading the phone microphone of camera %ld", slot);
        }

        /* a plain acquire read: the view is read-only, and an interlocked call would write (and fault) */
        int64_t written = ReadAcquire64((volatile const LONG64 *)&shared->written);
        /* start live; after a long gap (or a restarted receiver) jump to now as well */
        if (position < 0 || written < position || written - position > (int64_t)STREAMSTHETIC_AUDIO_RING / 2)
            position = written;
        while (written > position && mic->running) {
            uint32_t count = (uint32_t)min(written - position, (int64_t)CHUNK);
            for (uint32_t i = 0; i < count; i++)
                buffer[i] = shared->samples[(position + i) % STREAMSTHETIC_AUDIO_RING];
            position += count;
            struct obs_source_audio audio = {0};
            audio.data[0] = (const uint8_t *)buffer;
            audio.frames = count;
            audio.speakers = SPEAKERS_MONO;
            audio.format = AUDIO_FORMAT_16BIT;
            audio.samples_per_sec = STREAMSTHETIC_AUDIO_RATE;
            /* the samples just ended "now"; OBS smooths small jitter in these times */
            audio.timestamp = os_gettime_ns() - (uint64_t)count * 1000000000ULL / STREAMSTHETIC_AUDIO_RATE;
            obs_source_output_audio(mic->source, &audio);
        }
        Sleep(10);
    }
    close_block(&mapping, &shared);
    free(buffer);
    return 0;
}

static void phone_mic_update(void *data, obs_data_t *settings)
{
    struct phone_mic *mic = data;
    long long slot = obs_data_get_int(settings, "camera");
    InterlockedExchange(&mic->slot, (LONG)(slot >= 1 && slot <= 4 ? slot : 1));
}

static void *phone_mic_create(obs_data_t *settings, obs_source_t *source)
{
    struct phone_mic *mic = calloc(1, sizeof(*mic));
    if (!mic) return NULL;
    mic->source = source;
    mic->running = 1;
    phone_mic_update(mic, settings);
    mic->thread = CreateThread(NULL, 0, phone_mic_thread, mic, 0, NULL);
    return mic;
}

static void phone_mic_destroy(void *data)
{
    struct phone_mic *mic = data;
    InterlockedExchange(&mic->running, 0);
    if (mic->thread) {
        WaitForSingleObject(mic->thread, INFINITE);
        CloseHandle(mic->thread);
    }
    free(mic);
}

static void phone_mic_defaults(obs_data_t *settings)
{
    obs_data_set_default_int(settings, "camera", 1);
}

static obs_properties_t *phone_mic_properties(void *unused)
{
    UNUSED_PARAMETER(unused);
    obs_properties_t *props = obs_properties_create();
    obs_property_t *list = obs_properties_add_list(props, "camera", "Phone", OBS_COMBO_TYPE_LIST, OBS_COMBO_FORMAT_INT);
    obs_property_list_add_int(list, "Camera 1 (Streamsthetic)", 1);
    obs_property_list_add_int(list, "Camera 2 (Streamsthetic 2)", 2);
    obs_property_list_add_int(list, "Camera 3 (Streamsthetic 3)", 3);
    obs_property_list_add_int(list, "Camera 4 (Streamsthetic 4)", 4);
    obs_properties_add_text(props, "help",
        "In the phone app, turn on Settings > Streaming & power > Send the microphone to the PC. "
        "Sound arrives while that phone streams.", OBS_TEXT_INFO);
    return props;
}

bool obs_module_load(void)
{
    struct obs_source_info info = {0};
    info.id = "streamsthetic_phone_mic";
    info.type = OBS_SOURCE_TYPE_INPUT;
    info.output_flags = OBS_SOURCE_AUDIO;
    info.get_name = phone_mic_name;
    info.create = phone_mic_create;
    info.destroy = phone_mic_destroy;
    info.update = phone_mic_update;
    info.get_defaults = phone_mic_defaults;
    info.get_properties = phone_mic_properties;
    info.icon_type = OBS_ICON_TYPE_AUDIO_INPUT;
    obs_register_source(&info);
    blog(LOG_INFO, "[streamsthetic] phone microphone source loaded");
    return true;
}

const char *obs_module_name(void)
{
    return "Streamsthetic phone microphone";
}

const char *obs_module_description(void)
{
    return "The microphone of a phone connected with Streamsthetic, as an OBS audio source.";
}
