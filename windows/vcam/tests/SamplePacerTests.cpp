#include <atomic>
#include <cmath>
#include <iostream>
#include <thread>
#include "../VCamSampleSource/SamplePacer.h"

static void Check(bool condition, const char* message)
{
    if (!condition) throw message;
    std::cout << "PASS: " << message << '\n';
}
static void Success(HRESULT hr) { if (FAILED(hr)) throw "timer operation failed"; }

int main()
{
    try
    {
        constexpr LONGLONG origin = 125000003;
        for (UINT32 fps : {30u, 60u})
        {
            FrameSchedule schedule;
            schedule.Reset(fps);
            for (LONGLONG frame = 0; frame < 10000; ++frame)
            {
                auto expected = origin + frame * 10000000 / fps;
                if (schedule.Due(expected) != expected) throw "schedule drifted";
                LONGLONG timestamp, duration;
                schedule.Take(timestamp, duration);
                if (timestamp != expected || duration != (frame + 1) * 10000000 / fps - frame * 10000000 / fps)
                    throw "rational timestamp or duration changed";
                if (std::llround(double(timestamp - origin) * fps / 10000000) != frame)
                    throw "duplicate negotiated-timebase timestamp";
            }
            Check(true, fps == 30 ? "30 fps timeline has no drift or duplicate rounded timestamp" : "60 fps timeline has no drift or duplicate rounded timestamp");
        }
        FrameSchedule late;
        late.Reset(30);
        late.Due(origin);
        LONGLONG stamp, duration;
        late.Take(stamp, duration);
        auto delayed = origin + 50500000;
        auto next = late.Due(delayed);
        Check(next >= delayed && next <= delayed + 333334, "a stalled consumer skips backlog instead of receiving a catch-up burst");
        late.Take(stamp, duration);
        Check(late.Due(delayed) > stamp, "next deadline remains after the recovered sample");

        // Real high-resolution timers: a producer offers 60 frames/sec while the consumer requests
        // every available frame. Its negotiated 30 fps must still take one second for 31 samples.
        std::atomic<bool> stop{false};
        std::atomic<int> produced{0};
        std::atomic<bool> producerFailed{false};
        std::thread producer([&] {
            SamplePacer input; input.Reset(60);
            while (!stop)
            {
                LONGLONG time, length;
                if (FAILED(input.Wait(time, length))) { producerFailed = true; break; }
                ++produced;
            }
        });
        SamplePacer output; output.Reset(30);
        LONGLONG first = 0, last = 0;
        auto started = MFGetSystemTime();
        bool increasing = true;
        for (int frame = 0; frame <= 30; ++frame)
        {
            LONGLONG time, length;
            if (FAILED(output.Wait(time, length))) { producerFailed = true; break; }
            if (!frame) first = time;
            else increasing &= std::llround(double(time - first) * 30 / 10000000) > std::llround(double(last - first) * 30 / 10000000);
            last = time;
        }
        auto elapsed = MFGetSystemTime() - started;
        stop = true;
        producer.join();
        Check(!producerFailed && produced >= 50 && produced <= 90, "real input produces about 60 frames in the measurement window");
        Check(elapsed >= 9500000 && elapsed <= 14000000, "30 fps consumer emits 31 samples in about one second despite 60 fps input");
        Check(increasing && last - first >= 10000000, "paced delivery remains monotonic in the consumer's 1/30-second timebase");
        std::cout << "SAMPLE PACER TEST PASS\n";
        return 0;
    }
    catch (const char* failure) { std::cerr << "FAIL: " << failure << '\n'; return 1; }
}
