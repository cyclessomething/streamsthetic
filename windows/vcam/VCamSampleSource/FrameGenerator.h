#pragma once

#include "PhoneCamShared.h"

// Copies the newest NV12 frame from the PhoneCam shared-memory buffer into Media Foundation samples.
// Replaces the upstream VCamSample Direct2D test-pattern generator; CPU path only.
class FrameGenerator
{
	unsigned _slot;
	UINT _width;
	UINT _height;
	ULONGLONG _frame;
	LONGLONG _qpcFreq;
	HANDLE _mapping;
	HANDLE _frameEvent;
	PhoneCamSharedHeader* _shared;
	LONG64 _lastSeq;

	HRESULT OpenShared();
	void WaitForNewFrame();
	bool CopyLatestFrame(BYTE* dst, LONG pitch);
	void FillBlack(BYTE* dst, LONG pitch);
	INT64 NowQpcUs() const;

public:
	explicit FrameGenerator(unsigned slot = 1) :
		_slot(slot),
		_width(0),
		_height(0),
		_frame(0),
		_qpcFreq(1),
		_mapping(nullptr),
		_frameEvent(nullptr),
		_shared(nullptr),
		_lastSeq(0)
	{
		LARGE_INTEGER freq;
		QueryPerformanceFrequency(&freq);
		_qpcFreq = freq.QuadPart;
	}

	FrameGenerator(const FrameGenerator&) = delete;
	FrameGenerator& operator=(const FrameGenerator&) = delete;

	~FrameGenerator()
	{
		if (_shared)
		{
			UnmapViewOfFile(_shared);
		}

		if (_mapping)
		{
			CloseHandle(_mapping);
		}

		if (_frameEvent)
		{
			CloseHandle(_frameEvent);
		}
	}

	HRESULT EnsureRenderTarget(UINT width, UINT height);
	HRESULT Generate(IMFSample* sample, REFGUID format, IMFSample** outSample);
};
