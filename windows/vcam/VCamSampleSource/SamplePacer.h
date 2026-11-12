#pragma once

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>

// Keep timestamps on one rational timeline. Late consumers skip slots instead of getting
// a catch-up burst; this also avoids duplicate timestamps when a 30 fps app quantizes to 1/30 s.
class FrameSchedule
{
    static constexpr LONGLONG TicksPerSecond = 10000000;
    UINT32 _fps = 60;
    LONGLONG _origin = 0;
    LONGLONG _index = 0;
    bool _started = false;
    LONGLONG TimeAt(LONGLONG index) const { return _origin + index * TicksPerSecond / _fps; }

public:
    void Reset(UINT32 fps) { _fps = fps; _origin = _index = 0; _started = false; }
    LONGLONG Due(LONGLONG now)
    {
        if (!_started) { _origin = now; _started = true; }
        if (now > TimeAt(_index) + TicksPerSecond / _fps)
            _index = ((now - _origin) * _fps + TicksPerSecond - 1) / TicksPerSecond;
        return TimeAt(_index);
    }
    void Take(LONGLONG& timestamp, LONGLONG& duration)
    {
        timestamp = TimeAt(_index);
        duration = TimeAt(_index + 1) - timestamp;
        ++_index;
    }
};

class SamplePacer
{
    FrameSchedule _schedule;
    HANDLE _timer = nullptr;
public:
    SamplePacer() = default;
    SamplePacer(const SamplePacer&) = delete;
    SamplePacer& operator=(const SamplePacer&) = delete;
    ~SamplePacer() { if (_timer) CloseHandle(_timer); }
    void Reset(UINT32 fps) { _schedule.Reset(fps); }
    HRESULT Wait(LONGLONG& timestamp, LONGLONG& duration)
    {
        if (!_timer)
        {
            _timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            if (!_timer && GetLastError() == ERROR_INVALID_PARAMETER)
                _timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
            if (!_timer) return HRESULT_FROM_WIN32(GetLastError());
        }
        for (;;)
        {
            auto now = MFGetSystemTime();
            auto due = _schedule.Due(now);
            if (now >= due)
            {
                _schedule.Take(timestamp, duration);
                return S_OK;
            }
            LARGE_INTEGER delay;
            delay.QuadPart = -(due - now); // relative time, in 100 ns units
            if (!SetWaitableTimer(_timer, &delay, 0, nullptr, nullptr, FALSE)) return HRESULT_FROM_WIN32(GetLastError());
            if (WaitForSingleObject(_timer, INFINITE) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(GetLastError());
        }
    }
};
