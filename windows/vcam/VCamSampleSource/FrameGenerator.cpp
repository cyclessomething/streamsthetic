#include "pch.h"
#include <sddl.h>
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "FrameGenerator.h"

HRESULT FrameGenerator::EnsureRenderTarget(UINT width, UINT height)
{
	RETURN_HR_IF(E_INVALIDARG, !width || !height || (width & 1) || (height & 1) ||
		!((width <= PHONECAM_MAX_WIDTH && height <= PHONECAM_MAX_HEIGHT) ||
		  (width <= PHONECAM_MAX_HEIGHT && height <= PHONECAM_MAX_WIDTH)));
	_width = width;
	_height = height;
	_frame = 0;

	// the receiver may not exist yet; Generate retries until the mapping is available
	LOG_IF_FAILED(OpenShared());
	return S_OK;
}

HRESULT FrameGenerator::OpenShared()
{
	if (!_shared)
	{
		// only System, Local Service (Frame Server), Administrators and signed-in users may touch the frames
		PSECURITY_DESCRIPTOR sd = nullptr;
		RETURN_IF_WIN32_BOOL_FALSE(ConvertStringSecurityDescriptorToSecurityDescriptorW(
			L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GA;;;AU)", SDDL_REVISION_1, &sd, nullptr));
		SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
		auto size = (ULONGLONG)PHONECAM_SHARED_SIZE;
		_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, (DWORD)(size >> 32), (DWORD)size, PhoneCamSharedName(_slot).c_str());
		if (!_frameEvent)
		{
			_frameEvent = CreateEventW(&sa, FALSE, FALSE, PhoneCamFrameEventName(_slot).c_str());
			if (!_frameEvent)
			{
				_frameEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, PhoneCamFrameEventName(_slot).c_str());
			}
		}
		LocalFree(sd);

		// creating Global\ objects needs SeCreateGlobalPrivilege; processes without it can still open one
		if (!_mapping)
		{
			_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, PhoneCamSharedName(_slot).c_str());
			RETURN_LAST_ERROR_IF_NULL(_mapping);
		}

		_shared = (PhoneCamSharedHeader*)MapViewOfFile(_mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0);
		if (!_shared)
		{
			auto hr = HRESULT_FROM_WIN32(GetLastError());
			CloseHandle(_mapping);
			_mapping = nullptr;
			RETURN_HR(hr);
		}
		WINTRACE(L"FrameGenerator shared memory '%s' mapped", PhoneCamSharedName(_slot).c_str());
	}

	// publish what we serve so the receiver knows which size to write
	_shared->width = _width;
	_shared->height = _height;
	_shared->version = PHONECAM_SHARED_VERSION;
	_shared->magic = PHONECAM_SHARED_MAGIC;
	return S_OK;
}

INT64 FrameGenerator::NowQpcUs() const
{
	LARGE_INTEGER now;
	QueryPerformanceCounter(&now);
	// split to avoid overflowing ticks * 1e6
	return (now.QuadPart / _qpcFreq) * 1000000 + (now.QuadPart % _qpcFreq) * 1000000 / _qpcFreq;
}

bool FrameGenerator::CopyLatestFrame(BYTE* dst, LONG pitch)
{
	auto header = _shared;
	if (!header || header->magic != PHONECAM_SHARED_MAGIC || header->width != _width || header->height != _height)
		return false;

	auto src = reinterpret_cast<const BYTE*>(header + 1);
	// A write takes 1-2 ms; 50 Sleep(0) retries gave up sooner than that and showed black frames (seen as
	// flicker, mostly at 30 fps). Retry for up to 20 ms instead.
	auto deadline = NowQpcUs() + 20000;
	while (NowQpcUs() < deadline)
	{
		auto seq = ReadAcquire64(&header->seq);
		if (!seq)
			return false; // nothing written yet

		if (seq & 1)
		{
			YieldProcessor(); // receiver is mid-write
			continue;
		}

		for (UINT y = 0; y < _height; y++)
		{
			CopyMemory(dst + (size_t)y * pitch, src + (size_t)y * _width, _width);
		}

		auto uvDst = dst + (size_t)pitch * _height;
		auto uvSrc = src + (size_t)_width * _height;
		for (UINT y = 0; y < _height / 2; y++)
		{
			CopyMemory(uvDst + (size_t)y * pitch, uvSrc + (size_t)y * _width, _width);
		}

		MemoryBarrier();
		if (ReadAcquire64(&header->seq) == seq)
		{
			header->readQpcUs = NowQpcUs();
			header->readSeq = seq;
			_lastSeq = seq;
			return true;
		}
	}
	return false;
}

// Consumers may request samples faster than frames arrive. Waiting here for the next frame keeps the
// output paced to the phone and hands each frame over the moment it is written. Gives up after two
// frame periods so a stalled receiver still yields (repeated or black) frames.
// Waits on the receiver's frame event: Sleep(1) cannot be used because the Frame Server runs at the
// default ~15.6 ms timer resolution, which made delivery miss frames (measured ~48 fps from 60).
void FrameGenerator::WaitForNewFrame()
{
	auto header = _shared;
	if (!header || header->magic != PHONECAM_SHARED_MAGIC)
		return;

	auto deadline = NowQpcUs() + 2 * 1000000 / 60;
	for (;;)
	{
		auto seq = ReadAcquire64(&header->seq);
		if (seq && !(seq & 1) && seq != _lastSeq)
			return;

		auto now = NowQpcUs();
		if (now >= deadline)
			return;

		if (_frameEvent)
		{
			WaitForSingleObject(_frameEvent, (DWORD)((deadline - now + 999) / 1000));
		}
		else
		{
			Sleep(1);
		}
	}
}

void FrameGenerator::FillBlack(BYTE* dst, LONG pitch)
{
	for (UINT y = 0; y < _height; y++)
	{
		FillMemory(dst + (size_t)y * pitch, _width, 16);
	}

	auto uvDst = dst + (size_t)pitch * _height;
	for (UINT y = 0; y < _height / 2; y++)
	{
		FillMemory(uvDst + (size_t)y * pitch, _width, 128);
	}
}

HRESULT FrameGenerator::Generate(IMFSample* sample, REFGUID format, IMFSample** outSample)
{
	RETURN_HR_IF_NULL(E_POINTER, sample);
	RETURN_HR_IF_NULL(E_POINTER, outSample);
	*outSample = nullptr;
	RETURN_HR_IF(MF_E_INVALIDMEDIATYPE, format != MFVideoFormat_NV12);

	if (!_shared)
	{
		OpenShared(); // receiver or mapping may appear later; black frames until then
	}
	WaitForNewFrame();

	wil::com_ptr_nothrow<IMFMediaBuffer> mediaBuffer;
	RETURN_IF_FAILED(sample->GetBufferByIndex(0, &mediaBuffer));
	wil::com_ptr_nothrow<IMF2DBuffer2> buffer2D;
	RETURN_IF_FAILED(mediaBuffer->QueryInterface(IID_PPV_ARGS(&buffer2D)));

	BYTE* scanline;
	LONG pitch;
	BYTE* start;
	DWORD length;
	RETURN_IF_FAILED(buffer2D->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline, &pitch, &start, &length));
	// black only before the first frame ever arrives; after that a missed copy keeps the buffer's previous picture
	if (!CopyLatestFrame(scanline, pitch) && !_lastSeq)
	{
		FillBlack(scanline, pitch);
	}
	buffer2D->Unlock2D();

	_frame++;
	sample->AddRef();
	*outSample = sample;
	return S_OK;
}
