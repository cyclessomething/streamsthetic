#pragma once

#include <string>

// Shared-memory frame handoff between the PhoneCam PC receiver and this media source.
// The media source (loaded by the Windows Frame Server service) creates the mapping and publishes
// the resolution it serves; the receiver opens it and writes NV12 frames after the header.
// seq is a seqlock: odd while a frame is being written, even once it is complete, 0 = no frame yet.
// Times are QueryPerformanceCounter microseconds, which every process on the machine shares.
#define PHONECAM_SHARED_NAME L"Global\\PhoneCamFrame"
// auto-reset event the receiver sets after each complete frame, so the media source wakes immediately
#define PHONECAM_FRAME_EVENT_NAME L"Global\\PhoneCamFrameReady"
#define PHONECAM_SHARED_MAGIC 0x31464350 // "PCF1"
#define PHONECAM_SHARED_VERSION 1
#define PHONECAM_MAX_WIDTH 3840
#define PHONECAM_MAX_HEIGHT 2160

#pragma pack(push, 1)
struct PhoneCamSharedHeader
{
	UINT32 magic;
	UINT32 version;
	UINT32 width;          // set by the media source
	UINT32 height;         // set by the media source
	volatile LONG64 seq;   // written by the receiver
	INT64 ptsUs;           // phone capture timestamp of the current frame (phone clock)
	INT64 writeQpcUs;      // when the receiver finished writing the current frame
	INT64 readQpcUs;       // when the media source last copied a frame out
	INT64 readSeq;         // seq of the frame the media source last copied out
	BYTE reserved[8];
};
#pragma pack(pop)

static_assert(sizeof(PhoneCamSharedHeader) == 64, "PhoneCamSharedHeader must stay 64 bytes");

#define PHONECAM_SHARED_SIZE (sizeof(PhoneCamSharedHeader) + (size_t)PHONECAM_MAX_WIDTH * PHONECAM_MAX_HEIGHT * 3 / 2)

// Slot 1 keeps the original identity so existing OBS scenes and installations work.
// These helpers are shared by the media source and receiver; callers validate 1..4.
inline GUID PhoneCamSlotClsid(unsigned slot)
{
    GUID id = { 0x31621596, 0x4208, 0x4d72, {0x90, 0x66, 0x1f, 0x95, 0x07, 0x07, 0x44, 0x23} };
    id.Data4[7] += static_cast<BYTE>(slot - 1);
    return id;
}
inline std::wstring PhoneCamSourceClsid(unsigned slot)
{
    return L"{31621596-4208-4d72-9066-1f950707442" + std::to_wstring(slot + 2) + L"}";
}
inline std::wstring PhoneCamSlotSuffix(unsigned slot)
{
    return slot == 1 ? L"" : std::to_wstring(slot);
}
inline std::wstring PhoneCamSharedName(unsigned slot)
{
    return std::wstring(PHONECAM_SHARED_NAME) + PhoneCamSlotSuffix(slot);
}
inline std::wstring PhoneCamFrameEventName(unsigned slot)
{
    return std::wstring(PHONECAM_FRAME_EVENT_NAME) + PhoneCamSlotSuffix(slot);
}
inline std::wstring PhoneCamPreviewName(unsigned slot)
{
    return L"Local\\PhoneCamPreview" + PhoneCamSlotSuffix(slot);
}
inline std::wstring PhoneCamCameraName(unsigned slot)
{
    return slot == 1 ? L"Streamsthetic" : L"Streamsthetic " + std::to_wstring(slot);
}
