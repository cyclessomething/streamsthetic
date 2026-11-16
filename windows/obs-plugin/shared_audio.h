// The phone microphone, shared between StreamstheticReceiver.exe (writer) and the OBS plugin (reader).
// One block per camera slot, named "Local\StreamstheticAudio<slot>" (slot 1-4). The receiver appends the phone's
// 48 kHz mono 16-bit samples to a ring and then publishes the running total in `written`; a reader keeps its own
// position and copies whatever is new. Nothing waits on anything: a slow reader skips ahead, it never delays the stream.
#pragma once
#include <stdint.h>

#define STREAMSTHETIC_AUDIO_MAGIC 0x31445541u /* "AUD1" */
#define STREAMSTHETIC_AUDIO_RATE 48000u
#define STREAMSTHETIC_AUDIO_RING 96000u /* samples: 2 seconds */
#define STREAMSTHETIC_AUDIO_NAME L"Local\\StreamstheticAudio%u"

typedef struct StreamstheticSharedAudio
{
    uint32_t magic;
    uint32_t sampleRate;
    uint32_t channels;
    uint32_t ring;
    volatile int64_t written; /* samples written since the block was created; only grows */
    int16_t samples[STREAMSTHETIC_AUDIO_RING];
} StreamstheticSharedAudio;
