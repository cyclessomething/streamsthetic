// PhoneCam receiver: phone stream (PCM1 protocol) -> Media Foundation H.264 decode -> NV12 -> PhoneCam virtual camera.
// Native replacement for tools/feeder.py.
//
//   PhoneCamReceiver.exe [--seconds N]                                    live over USB (runs adb forward itself)
//   PhoneCamReceiver.exe --wifi --code C                                  live over Wi-Fi, finds the phone itself
//   PhoneCamReceiver.exe --host H --port P [--code C]                     live from a known address
//   PhoneCamReceiver.exe --replay FILE [--realtime] [--no-camera]         decode a recorded PCM1 stream
//   PhoneCamReceiver.exe --install-camera | --uninstall-camera            always-present system camera (admin)
//   Add --slot 1..4 to select a camera (default 1); install/uninstall without --slot handles all four.
//
// By default it registers the PhoneCam camera for as long as it runs (--no-register if another process already
// did, e.g. an installed system camera).
//
// Replay without --realtime decodes as fast as possible and exits non-zero if frames are lost or have the wrong
// size, so it doubles as an offline self-test (tools/testdata/*.pcm1).
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <iphlpapi.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <strmif.h>
#include <wmcodecdsp.h>
#include <mfvirtualcamera.h>
#include <d3d11_4.h>
#include <initguid.h>
#include <codecapi.h>
#include <dbghelp.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#pragma comment(lib, "dbghelp.lib")

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <deque>
#include <functional>
#include <iostream>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "wil/com.h"
#include "wil/resource.h"
#include "wil/result.h"
#include "PhoneCamShared.h"

namespace
{
constexpr uint32_t PCM1_MAGIC = 0x50434D31;
constexpr uint8_t TYPE_CONFIG = 1, TYPE_FRAME = 2, TYPE_CLOCK = 3, TYPE_SETTINGS = 4, TYPE_AUDIO = 5, TYPE_REJECT = 6;

int64_t NowUs()
{
    static const int64_t freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f.QuadPart; }();
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    // QueryPerformanceCounter microseconds: the same clock the media source and tools/feeder.py use
    return (now.QuadPart / freq) * 1000000 + (now.QuadPart % freq) * 1000000 / freq;
}

uint32_t Be32(const BYTE* p) { return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3]; }
int64_t Be64(const BYTE* p) { return (int64_t(Be32(p)) << 32) | Be32(p + 4); }

double Median(std::vector<double> v)
{
    if (v.empty()) return 0;
    std::nth_element(v.begin(), v.begin() + v.size() / 2, v.end());
    return v[v.size() / 2];
}

double Percentile(std::vector<double> v, double p)
{
    if (v.empty()) return 0;
    std::sort(v.begin(), v.end());
    return v[std::min(v.size() - 1, size_t(v.size() * p))];
}

// --- stream sources --------------------------------------------------------------------------------------------

struct Source
{
    virtual ~Source() = default;
    virtual bool Read(void* buffer, size_t size) = 0;
    // bytes already received and not read yet (a newer frame waiting means this PC is the one behind)
    virtual size_t Pending() { return 0; }
};

struct FileSource : Source
{
    FILE* file = nullptr;
    explicit FileSource(const std::wstring& path)
    {
        if (_wfopen_s(&file, path.c_str(), L"rb") || !file) throw std::runtime_error("cannot open replay file");
    }
    ~FileSource() override { if (file) fclose(file); }
    bool Read(void* buffer, size_t size) override { return fread(buffer, 1, size, file) == size; }
};

struct SocketSource : Source
{
    SOCKET socket = INVALID_SOCKET;
    SocketSource(const std::string& host, const std::string& port)
    {
        addrinfo hints{}, *result = nullptr;
        hints.ai_family = AF_INET;
        hints.ai_socktype = SOCK_STREAM;
        if (getaddrinfo(host.c_str(), port.c_str(), &hints, &result)) throw std::runtime_error("cannot resolve host");
        socket = ::socket(result->ai_family, result->ai_socktype, result->ai_protocol);
        auto ok = socket != INVALID_SOCKET && connect(socket, result->ai_addr, int(result->ai_addrlen)) == 0;
        freeaddrinfo(result);
        if (!ok) throw std::runtime_error("cannot connect to the phone (is the stream running and forwarded?)");
        BOOL noDelay = TRUE;
        setsockopt(socket, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay), sizeof(noDelay));
    }
    ~SocketSource() override { if (socket != INVALID_SOCKET) closesocket(socket); }
    bool Read(void* buffer, size_t size) override
    {
        auto p = static_cast<char*>(buffer);
        while (size)
        {
            int got = recv(socket, p, int(std::min<size_t>(size, 1 << 20)), 0);
            if (got <= 0) return false;
            p += got;
            size -= got;
        }
        return true;
    }
    size_t Pending() override
    {
        u_long bytes = 0;
        return ioctlsocket(socket, FIONREAD, &bytes) == 0 ? bytes : 0;
    }
    // the clock thread and setting changes (stdin thread) both write to the phone
    bool Send(const void* buffer, int size)
    {
        std::lock_guard lock(sendLock);
        return send(socket, static_cast<const char*>(buffer), size, 0) == size;
    }
    std::mutex sendLock;
};

// The connected phone, for setting changes typed on stdin ("set cam=0;zoom=0.6"; the PhoneCam app sends these).
std::mutex g_phoneLock;
SocketSource* g_phone = nullptr;

// 'S' + u16 big-endian length + ASCII "key=value;..." (keys cam, zoom, w, h, fps, mbps; the phone validates them)
void SendSettingsToPhone(const std::string& text)
{
    if (text.empty() || text.size() > 512 || !std::all_of(text.begin(), text.end(), [](char c) { return c >= 32 && c < 127; }))
    {
        wprintf(L"settings not sent: expected short ASCII text like cam=0;zoom=0.6\n");
        return;
    }
    std::string message = "S";
    message += char(text.size() >> 8);
    message += char(text.size() & 0xFF);
    message += text;
    std::lock_guard lock(g_phoneLock);
    if (!g_phone || !g_phone->Send(message.data(), int(message.size()))) wprintf(L"settings not sent: phone not connected\n");
}

struct Packet
{
    uint8_t type = 0;
    uint8_t flags = 0;
    int64_t ptsUs = 0;
    int64_t sendUs = 0;
    std::vector<BYTE> payload;
};

bool ReadPacket(Source& source, Packet& packet)
{
    BYTE header[28];
    if (!source.Read(header, sizeof(header))) return false;
    if (Be32(header) != PCM1_MAGIC) throw std::runtime_error("bad PCM1 magic (stream out of sync)");
    packet.type = header[4];
    packet.flags = header[5];
    auto length = Be32(header + 8);
    // real frames are at most a few hundred KB even at 4K; anything larger means a corrupt header
    if (length > 16u * 1024 * 1024) throw std::runtime_error("PCM1 packet too large (stream corrupt)");
    packet.ptsUs = Be64(header + 12);
    packet.sendUs = Be64(header + 20);
    packet.payload.resize(length);
    return !length || source.Read(packet.payload.data(), length);
}

// --- decoder ---------------------------------------------------------------------------------------------------

// A locked, CPU-readable NV12 frame.
struct DecodedFrame
{
    const BYTE* data;   // NV12, luma plane then interleaved chroma
    LONG pitch;
    UINT32 width;       // visible size
    UINT32 height;
    UINT32 planeHeight; // allocated luma rows (1088 for 1080p streams), chroma starts at pitch * planeHeight
    int64_t ptsUs;
};

// A decoded or scaled frame still inside its Media Foundation sample (GPU texture or system memory).
struct VideoFrame
{
    IMFSample* sample;
    UINT32 width;        // visible size
    UINT32 height;
    UINT32 bufferWidth;  // allocated size (1920x1088 for a 1080p stream)
    UINT32 bufferHeight;
    int64_t ptsUs;
};

// Maps a frame for reading (for GPU textures Media Foundation copies it back) and hands it to fn.
void LockFrame(const VideoFrame& frame, const std::function<void(const DecodedFrame&)>& fn)
{
    wil::com_ptr<IMFMediaBuffer> buffer;
    THROW_IF_FAILED(frame.sample->GetBufferByIndex(0, &buffer));
    if (auto buffer2D = buffer.try_query<IMF2DBuffer>())
    {
        BYTE* scan = nullptr;
        LONG pitch = 0;
        THROW_IF_FAILED(buffer2D->Lock2D(&scan, &pitch));
        auto unlock = wil::scope_exit([&] { buffer2D->Unlock2D(); });
        fn({ scan, pitch, frame.width, frame.height, frame.bufferHeight, frame.ptsUs });
        return;
    }
    BYTE* data = nullptr;
    THROW_IF_FAILED(buffer->Lock(&data, nullptr, nullptr));
    auto unlock = wil::scope_exit([&] { buffer->Unlock(); });
    fn({ data, LONG(frame.bufferWidth), frame.width, frame.height, frame.bufferHeight, frame.ptsUs });
}

// Hardware video device shared by the decoder and the scaler; null if the PC has no usable GPU.
wil::com_ptr<IMFDXGIDeviceManager> CreateD3DManager()
{
    wil::com_ptr<ID3D11Device> device;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, nullptr)))
    {
        return nullptr;
    }
    if (auto multithread = device.try_query<ID3D11Multithread>()) multithread->SetMultithreadProtected(TRUE);
    UINT token = 0;
    wil::com_ptr<IMFDXGIDeviceManager> manager;
    if (FAILED(MFCreateDXGIDeviceManager(&token, &manager)) || FAILED(manager->ResetDevice(device.get(), token))) return nullptr;
    return manager;
}

// Microsoft's H.264 decoder MFT in low-latency mode: outputs each frame as soon as it is decoded, in NV12.
class H264Decoder
{
    wil::com_ptr<IMFTransform> _mft;
    UINT32 _width = 0, _height = 0, _visibleWidth = 0, _visibleHeight = 0;
    bool _providesSamples = false;
    bool _gpu = false;
    DWORD _outputSize = 0;

    void SetOutputNV12()
    {
        for (DWORD i = 0;; i++)
        {
            wil::com_ptr<IMFMediaType> type;
            THROW_IF_FAILED(_mft->GetOutputAvailableType(0, i, &type));
            GUID subtype{};
            type->GetGUID(MF_MT_SUBTYPE, &subtype);
            if (subtype != MFVideoFormat_NV12) continue;

            THROW_IF_FAILED(_mft->SetOutputType(0, type.get(), 0));
            THROW_IF_FAILED(MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &_width, &_height));
            MFVideoArea area{};
            if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), nullptr)))
            {
                _visibleWidth = UINT32(area.Area.cx);
                _visibleHeight = UINT32(area.Area.cy);
            }
            else
            {
                _visibleWidth = _width;
                _visibleHeight = _height;
            }

            MFT_OUTPUT_STREAM_INFO info{};
            THROW_IF_FAILED(_mft->GetOutputStreamInfo(0, &info));
            _providesSamples = info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES);
            _outputSize = info.cbSize;
            return;
        }
    }

public:
    bool UsesGpu() const { return _gpu; }

    // With a D3D manager the decoder works on the GPU (DXVA) and hands out GPU textures; 4K60 is too slow on
    // the CPU (measured 29 fps decode-only on an i7-14700KF).
    void Init(IMFDXGIDeviceManager* manager)
    {
        THROW_IF_FAILED(CoCreateInstance(CLSID_CMSH264DecoderMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&_mft)));
        _gpu = false;
        if (manager)
        {
            wil::com_ptr<IMFAttributes> attributes;
            if (SUCCEEDED(_mft->GetAttributes(&attributes)) && MFGetAttributeUINT32(attributes.get(), MF_SA_D3D11_AWARE, FALSE))
            {
                _gpu = SUCCEEDED(_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(manager)));
            }
        }
        if (auto codecApi = _mft.try_query<ICodecAPI>())
        {
            VARIANT value{};
            value.vt = VT_UI4;
            value.ulVal = 1;
            LOG_IF_FAILED(codecApi->SetValue(&CODECAPI_AVLowLatencyMode, &value));
        }

        wil::com_ptr<IMFMediaType> input;
        THROW_IF_FAILED(MFCreateMediaType(&input));
        THROW_IF_FAILED(input->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        THROW_IF_FAILED(input->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264));
        THROW_IF_FAILED(input->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        THROW_IF_FAILED(_mft->SetInputType(0, input.get(), 0));
        SetOutputNV12();
        THROW_IF_FAILED(_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        THROW_IF_FAILED(_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
    }

    // Feeds one access unit (Annex-B) and calls onFrame for every frame the decoder produces.
    // Returns false if the decoder rejected the data (e.g. joined mid-stream before a keyframe).
    bool Decode(const std::vector<BYTE>& accessUnit, int64_t ptsUs, const std::function<void(const VideoFrame&)>& onFrame)
    {
        wil::com_ptr<IMFMediaBuffer> buffer;
        THROW_IF_FAILED(MFCreateMemoryBuffer(DWORD(accessUnit.size()), &buffer));
        BYTE* dst = nullptr;
        THROW_IF_FAILED(buffer->Lock(&dst, nullptr, nullptr));
        memcpy(dst, accessUnit.data(), accessUnit.size());
        buffer->Unlock();
        THROW_IF_FAILED(buffer->SetCurrentLength(DWORD(accessUnit.size())));
        wil::com_ptr<IMFSample> sample;
        THROW_IF_FAILED(MFCreateSample(&sample));
        THROW_IF_FAILED(sample->AddBuffer(buffer.get()));
        THROW_IF_FAILED(sample->SetSampleTime(ptsUs * 10));

        auto hr = _mft->ProcessInput(0, sample.get(), 0);
        if (hr == MF_E_NOTACCEPTING)
        {
            Drain(onFrame);
            hr = _mft->ProcessInput(0, sample.get(), 0);
        }
        if (FAILED(hr)) return false;
        Drain(onFrame);
        return true;
    }

private:
    void Drain(const std::function<void(const VideoFrame&)>& onFrame)
    {
        for (;;)
        {
            wil::com_ptr<IMFSample> outSample;
            if (!_providesSamples)
            {
                wil::com_ptr<IMFMediaBuffer> outBuffer;
                THROW_IF_FAILED(MFCreateMemoryBuffer(_outputSize, &outBuffer));
                THROW_IF_FAILED(MFCreateSample(&outSample));
                THROW_IF_FAILED(outSample->AddBuffer(outBuffer.get()));
            }

            MFT_OUTPUT_DATA_BUFFER output{};
            output.pSample = outSample.get();
            DWORD status = 0;
            auto hr = _mft->ProcessOutput(0, 1, &output, &status);
            if (output.pEvents) output.pEvents->Release();
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                SetOutputNV12(); // the real size is known once the first frame has been parsed
                wprintf(L"video %ux%u (decoder buffer %ux%u)\n", _visibleWidth, _visibleHeight, _width, _height);
                continue;
            }
            THROW_IF_FAILED(hr);

            wil::com_ptr<IMFSample> produced;
            if (_providesSamples) produced.attach(output.pSample);
            else produced = outSample;

            LONGLONG time = 0;
            produced->GetSampleTime(&time);
            onFrame({ produced.get(), _visibleWidth, _visibleHeight, _width, _height, time / 10 });
        }
    }
};

// --- scaling ---------------------------------------------------------------------------------------------------

// Upright picture: the phone reports how far its frames must turn (0/90/180/270 degrees clockwise, from its
// orientation sensor) in its settings ("rot="); the video processor turns them while scaling.
std::atomic<int> g_rotation = 0;
std::atomic<bool> g_mirror = false; // "mirror=1" in the phone settings: flip the picture left-right

// The input type with the angle the processor should turn it by.
wil::com_ptr<IMFMediaType> Rotated(wil::com_ptr<IMFMediaType> type, int rotation)
{
    THROW_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_ROTATION, UINT32(rotation)));
    return type;
}

// The angle travels on the input type (MF_MT_VIDEO_ROTATION, see Rotated); this makes the processor apply it.
void ApplyRotation(IMFTransform* mft, MF_VIDEO_PROCESSOR_MIRROR mirror)
{
    auto control = wil::com_query<IMFVideoProcessorControl>(mft);
    THROW_IF_FAILED(control->SetRotation(ROTATION_NORMAL));
    THROW_IF_FAILED(control->SetMirror(mirror));
    MFARGB black{ 0, 0, 0, 255 };
    THROW_IF_FAILED(control->SetBorderColor(&black));
}

// The output type with the picture fitted inside it: when the shapes differ (phone upright, app showing wide
// video) the picture keeps its shape, centred, and the processor fills the rest (black bars, nothing cut off).
wil::com_ptr<IMFMediaType> Fitted(wil::com_ptr<IMFMediaType> type, UINT32 inW, UINT32 inH, UINT32 outW, UINT32 outH, int rotation)
{
    bool sideways = rotation == 90 || rotation == 270;
    double contentW = sideways ? inH : inW, contentH = sideways ? inW : inH;
    double fit = std::min(outW / contentW, outH / contentH);
    MFVideoArea area{};
    area.Area.cx = LONG(contentW * fit) & ~1;
    area.Area.cy = LONG(contentH * fit) & ~1;
    area.OffsetX.value = short((LONG(outW) - area.Area.cx) / 2 & ~1);
    area.OffsetY.value = short((LONG(outH) - area.Area.cy) / 2 & ~1);
    THROW_IF_FAILED(type->SetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area)));
    THROW_IF_FAILED(type->SetBlob(MF_MT_GEOMETRIC_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area)));
    return type;
}

// The processor never writes outside the fitted picture (the bars came out green: untouched memory), so they are
// painted black here, in a writable copy on its way to the camera (scaler buffer, or the read-back staging copy).
// NV12 video-range black: Y 16, U/V 128. Nothing to do when the picture fills the frame.
void PaintBars(const DecodedFrame& frame, UINT32 inW, UINT32 inH, int rotation)
{
    bool sideways = rotation == 90 || rotation == 270;
    double contentW = sideways ? inH : inW, contentH = sideways ? inW : inH;
    double fit = std::min(frame.width / contentW, frame.height / contentH);
    UINT32 w = UINT32(contentW * fit) & ~1u, h = UINT32(contentH * fit) & ~1u;
    if (w + 2 >= frame.width && h + 2 >= frame.height) return;
    UINT32 left = (frame.width - w) / 2 & ~1u, top = (frame.height - h) / 2 & ~1u;
    auto paint = [&](BYTE* plane, UINT32 rows, UINT32 rowTop, UINT32 rowBottom, BYTE value) {
        for (UINT32 y = 0; y < rows; y++)
        {
            auto row = plane + size_t(y) * frame.pitch;
            if (y < rowTop || y >= rowBottom) memset(row, value, frame.width);
            else
            {
                memset(row, value, left);
                memset(row + left + w, value, frame.width - left - w);
            }
        }
    };
    auto luma = const_cast<BYTE*>(frame.data);
    paint(luma, frame.height, top, top + h, 16);
    paint(luma + size_t(frame.pitch) * frame.planeHeight, frame.height / 2, top / 2, (top + h) / 2, 128);
}

// Resizes NV12 frames with Microsoft's Video Processor MFT (CPU path), used when the phone sends a different
// size than the camera serves (e.g. 4K phone stream into a 1080p camera), or the picture must turn.
class Nv12Scaler
{
    wil::com_ptr<IMFTransform> _mft;
    UINT32 _inW = 0, _inH = 0, _outW = 0, _outH = 0;
    int _rotation = 0;
    bool _mirror = false;
    std::vector<BYTE> _out;

    static wil::com_ptr<IMFMediaType> Nv12Type(UINT32 width, UINT32 height)
    {
        wil::com_ptr<IMFMediaType> type;
        THROW_IF_FAILED(MFCreateMediaType(&type));
        THROW_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        THROW_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        THROW_IF_FAILED(MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, width, height));
        THROW_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, width));
        THROW_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        THROW_IF_FAILED(MFSetAttributeRatio(type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        return type;
    }

public:
    // Returns the frame resized to outW x outH (data owned by the scaler until the next call).
    DecodedFrame Scale(const DecodedFrame& frame, UINT32 outW, UINT32 outH, int rotation = 0)
    {
        if (!_mft || frame.width != _inW || frame.height != _inH || outW != _outW || outH != _outH || rotation != _rotation || g_mirror.load() != _mirror)
        {
            _mft.reset();
            THROW_IF_FAILED(CoCreateInstance(CLSID_VideoProcessorMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&_mft)));
            THROW_IF_FAILED(_mft->SetInputType(0, Rotated(Nv12Type(frame.width, frame.height), rotation).get(), 0));
            THROW_IF_FAILED(_mft->SetOutputType(0, Fitted(Nv12Type(outW, outH), frame.width, frame.height, outW, outH, rotation).get(), 0));
            // The software processor mirrors the rotated output, matching the displayed-picture setting.
            ApplyRotation(_mft.get(), g_mirror.load() ? MIRROR_HORIZONTAL : MIRROR_NONE);
            THROW_IF_FAILED(_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
            _inW = frame.width; _inH = frame.height; _outW = outW; _outH = outH; _rotation = rotation; _mirror = g_mirror.load();
            _out.resize(size_t(outW) * outH * 3 / 2);
            wprintf(L"scaling %ux%u -> %ux%u, turned %d degrees\n", _inW, _inH, _outW, _outH, rotation);
        }

        // the decoder's planes are padded (pitch, 1088 rows); the processor wants a tight NV12 image
        auto inSize = DWORD(size_t(_inW) * _inH * 3 / 2);
        wil::com_ptr<IMFMediaBuffer> inBuffer;
        THROW_IF_FAILED(MFCreateMemoryBuffer(inSize, &inBuffer));
        BYTE* in = nullptr;
        THROW_IF_FAILED(inBuffer->Lock(&in, nullptr, nullptr));
        for (UINT32 y = 0; y < _inH; y++) memcpy(in + size_t(y) * _inW, frame.data + size_t(y) * frame.pitch, _inW);
        auto uvSrc = frame.data + size_t(frame.pitch) * frame.planeHeight;
        for (UINT32 y = 0; y < _inH / 2; y++) memcpy(in + size_t(_inW) * _inH + size_t(y) * _inW, uvSrc + size_t(y) * frame.pitch, _inW);
        inBuffer->Unlock();
        THROW_IF_FAILED(inBuffer->SetCurrentLength(inSize));

        wil::com_ptr<IMFSample> inSample;
        THROW_IF_FAILED(MFCreateSample(&inSample));
        THROW_IF_FAILED(inSample->AddBuffer(inBuffer.get()));
        THROW_IF_FAILED(inSample->SetSampleTime(frame.ptsUs * 10));
        THROW_IF_FAILED(inSample->SetSampleDuration(166667));
        THROW_IF_FAILED(_mft->ProcessInput(0, inSample.get(), 0));

        wil::com_ptr<IMFMediaBuffer> outBuffer;
        THROW_IF_FAILED(MFCreateMemoryBuffer(DWORD(_out.size()), &outBuffer));
        wil::com_ptr<IMFSample> outSample;
        THROW_IF_FAILED(MFCreateSample(&outSample));
        THROW_IF_FAILED(outSample->AddBuffer(outBuffer.get()));
        MFT_OUTPUT_DATA_BUFFER output{};
        output.pSample = outSample.get();
        DWORD status = 0;
        THROW_IF_FAILED(_mft->ProcessOutput(0, 1, &output, &status));
        if (output.pEvents) output.pEvents->Release();

        BYTE* out = nullptr;
        DWORD length = 0;
        THROW_IF_FAILED(outBuffer->Lock(&out, nullptr, &length));
        memcpy(_out.data(), out, std::min<size_t>(length, _out.size()));
        outBuffer->Unlock();
        return { _out.data(), LONG(_outW), _outW, _outH, _outH, frame.ptsUs };
    }
};

// Resizes decoder output on the GPU (Video Processor MFT with the shared D3D manager): the frame never leaves
// video memory until the camera-sized result is read back. Crops the decoder's padding rows via the aperture.
class GpuScaler
{
    wil::com_ptr<IMFTransform> _mft;
    UINT32 _inW = 0, _inH = 0, _bufW = 0, _bufH = 0, _outW = 0, _outH = 0;
    int _rotation = 0;
    bool _mirror = false;
    bool _providesSamples = false;
    DWORD _outputSize = 0;

    static wil::com_ptr<IMFMediaType> Nv12Type(UINT32 width, UINT32 height, UINT32 visibleW, UINT32 visibleH)
    {
        wil::com_ptr<IMFMediaType> type;
        THROW_IF_FAILED(MFCreateMediaType(&type));
        THROW_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        THROW_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        THROW_IF_FAILED(MFSetAttributeSize(type.get(), MF_MT_FRAME_SIZE, width, height));
        THROW_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
        THROW_IF_FAILED(MFSetAttributeRatio(type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
        MFVideoArea area{};
        area.Area.cx = LONG(visibleW);
        area.Area.cy = LONG(visibleH);
        THROW_IF_FAILED(type->SetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area)));
        THROW_IF_FAILED(type->SetBlob(MF_MT_GEOMETRIC_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area)));
        return type;
    }

public:
    VideoFrame Scale(IMFDXGIDeviceManager* manager, const VideoFrame& frame, UINT32 outW, UINT32 outH, wil::com_ptr<IMFSample>& holder, int rotation = 0)
    {
        if (!_mft || frame.width != _inW || frame.height != _inH || frame.bufferWidth != _bufW || frame.bufferHeight != _bufH || outW != _outW || outH != _outH || rotation != _rotation || g_mirror.load() != _mirror)
        {
            _mft.reset();
            THROW_IF_FAILED(CoCreateInstance(CLSID_VideoProcessorMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&_mft)));
            THROW_IF_FAILED(_mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(manager)));
            THROW_IF_FAILED(_mft->SetInputType(0, Rotated(Nv12Type(frame.bufferWidth, frame.bufferHeight, frame.width, frame.height), rotation).get(), 0));
            THROW_IF_FAILED(_mft->SetOutputType(0, Fitted(Nv12Type(outW, outH, outW, outH), frame.width, frame.height, outW, outH, rotation).get(), 0));
            // The D3D processor mirrors BEFORE rotation, unlike the software path. For quarter turns,
            // mirror the input vertically so the final upright picture is mirrored horizontally.
            // Keeping MIRROR_HORIZONTAL here flipped both portrait orientations upside down.
            auto mirror = !g_mirror.load() ? MIRROR_NONE
                : (rotation == 90 || rotation == 270) ? MIRROR_VERTICAL : MIRROR_HORIZONTAL;
            ApplyRotation(_mft.get(), mirror);
            THROW_IF_FAILED(_mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
            MFT_OUTPUT_STREAM_INFO info{};
            THROW_IF_FAILED(_mft->GetOutputStreamInfo(0, &info));
            _providesSamples = info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES);
            _outputSize = info.cbSize;
            _inW = frame.width; _inH = frame.height; _bufW = frame.bufferWidth; _bufH = frame.bufferHeight; _outW = outW; _outH = outH; _rotation = rotation; _mirror = g_mirror.load();
            wprintf(L"scaling %ux%u -> %ux%u on the GPU, turned %d degrees\n", _inW, _inH, _outW, _outH, rotation);
        }

        frame.sample->SetSampleDuration(166667);
        THROW_IF_FAILED(_mft->ProcessInput(0, frame.sample, 0));
        wil::com_ptr<IMFSample> outSample;
        if (!_providesSamples)
        {
            wil::com_ptr<IMFMediaBuffer> outBuffer;
            THROW_IF_FAILED(MFCreateMemoryBuffer(std::max<DWORD>(_outputSize, outW * outH * 3 / 2), &outBuffer));
            THROW_IF_FAILED(MFCreateSample(&outSample));
            THROW_IF_FAILED(outSample->AddBuffer(outBuffer.get()));
        }
        MFT_OUTPUT_DATA_BUFFER output{};
        output.pSample = outSample.get();
        DWORD status = 0;
        THROW_IF_FAILED(_mft->ProcessOutput(0, 1, &output, &status));
        if (output.pEvents) output.pEvents->Release();
        if (_providesSamples) holder.attach(output.pSample);
        else holder = outSample;
        return { holder.get(), outW, outH, outW, outH, frame.ptsUs };
    }
};

// Reads GPU frames back through one reusable staging texture. Locking the Media Foundation buffer instead
// (IMF2DBuffer::Lock2D) slowed down over time in live 4K runs (7 ms -> 23 ms per frame within 10 s).
class GpuReadback
{
    // Two staging textures when pipelined: the GPU copies frame N into one while the CPU reads frame N-1 out of the
    // other, so the CPU never waits for the GPU to finish the newest frame (4K at 60 fps: the wait alone was 10+ ms
    // of the 16.7 ms budget). The price is one frame (16.7 ms) of delay, so it is used for 4K output only, and only
    // while the direct copy is too slow (see Adapt): on a PC with room to spare the direct copy has the lower delay.
    wil::com_ptr<ID3D11Texture2D> _stagingRing[2];
    wil::com_ptr<ID3D11Texture2D> _staging;
    D3D11_TEXTURE2D_DESC _desc{};
    int _next = 0;
    int64_t _pendingPts[2] = { -1, -1 };
    bool _allowPipelined = false;
    bool _active = false;           // currently pipelined
    double _emaUs = 0;              // smoothed time of one direct copy
    int _samples = 0;
    int64_t _activeSinceUs = 0;
    int64_t _holdUs = 10'000'000;      // how long the pipelined copy stays before the direct copy is tried again
    int64_t _triedDirectAtUs = 0;

    // Direct copy slower than ~12 ms (the budget is 16.7 ms at 60 fps) -> pipeline; every 10 s try the direct copy again.
    void Adapt(int64_t tookUs, int64_t nowUs)
    {
        if (_active)
        {
            if (nowUs - _activeSinceUs < _holdUs) return;
            _active = false; _emaUs = 0; _samples = 0; _triedDirectAtUs = nowUs;
            wprintf(L"4K copy: trying the direct copy again (lower delay)\n");
            return;
        }
        _emaUs = _samples++ == 0 ? double(tookUs) : _emaUs * 0.9 + double(tookUs) * 0.1;
        if (_samples >= 30 && _emaUs > 12000)
        {
            // the direct copy failed again right after a retry: the PC is busy for good (OBS rendering 4K), so wait longer before the next try
            _holdUs = (_triedDirectAtUs && nowUs - _triedDirectAtUs < 15'000'000) ? std::min<int64_t>(_holdUs * 2, 120'000'000) : 10'000'000;
            _active = true; _activeSinceUs = nowUs;
            _pendingPts[0] = _pendingPts[1] = -1; _next = 0;
            wprintf(L"4K copy: %.1f ms is too slow for 60 fps, switching to the pipelined copy (one frame more delay)\n", _emaUs / 1000.0);
        }
    }

public:
    explicit GpuReadback(bool pipelined = false) : _allowPipelined(pipelined) {}

    // Returns false if the frame is not a GPU texture (then the caller locks it the normal way).
    bool Read(const VideoFrame& frame, const std::function<void(const DecodedFrame&)>& fn)
    {
        bool big = frame.width * frame.height > 1920u * 1080u;
        int64_t start = NowUs();
        bool ok = ReadImpl(frame, fn, _allowPipelined && big && _active);
        if (ok && _allowPipelined && big) Adapt(NowUs() - start, start);
        return ok;
    }

private:
    bool ReadImpl(const VideoFrame& frame, const std::function<void(const DecodedFrame&)>& fn, bool pipelined)
    {
        wil::com_ptr<IMFMediaBuffer> buffer;
        THROW_IF_FAILED(frame.sample->GetBufferByIndex(0, &buffer));
        auto dxgi = buffer.try_query<IMFDXGIBuffer>();
        if (!dxgi) return false;
        wil::com_ptr<ID3D11Texture2D> texture;
        THROW_IF_FAILED(dxgi->GetResource(IID_PPV_ARGS(&texture)));
        UINT subresource = 0;
        THROW_IF_FAILED(dxgi->GetSubresourceIndex(&subresource));

        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        wil::com_ptr<ID3D11Device> device;
        texture->GetDevice(&device);

        if (!_staging || desc.Width != _desc.Width || desc.Height != _desc.Height || desc.Format != _desc.Format)
        {
            _stagingRing[0].reset(); _stagingRing[1].reset(); _pendingPts[0] = _pendingPts[1] = -1; _next = 0;
            _desc = desc;
            _desc.ArraySize = 1;
            _desc.MipLevels = 1;
            _desc.Usage = D3D11_USAGE_STAGING;
            _desc.BindFlags = 0;
            // writable too: black bars around a turned picture are painted into this copy (PaintBars)
            _desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ | D3D11_CPU_ACCESS_WRITE;
            _desc.MiscFlags = 0;
            _staging.reset();
            THROW_IF_FAILED(device->CreateTexture2D(&_desc, nullptr, &_staging));
        }

        wil::com_ptr<ID3D11DeviceContext> context;
        device->GetImmediateContext(&context);
        if (pipelined)
        {
            if (!_stagingRing[0])
                for (auto& t : _stagingRing) THROW_IF_FAILED(device->CreateTexture2D(&_desc, nullptr, &t));
            auto& target = _stagingRing[_next];
            context->CopySubresourceRegion(target.get(), 0, 0, 0, 0, texture.get(), subresource, nullptr);
            _pendingPts[_next] = frame.ptsUs;
            _next ^= 1;
            // _next now names the texture holding the PREVIOUS frame: hand that one out
            auto& ready = _stagingRing[_next];
            auto readyPts = _pendingPts[_next];
            if (readyPts < 0) return true; // first frame: nothing older to show yet
            D3D11_MAPPED_SUBRESOURCE m{};
            THROW_IF_FAILED(context->Map(ready.get(), 0, D3D11_MAP_READ_WRITE, 0, &m));
            auto unmapReady = wil::scope_exit([&] { context->Unmap(ready.get(), 0); });
            fn({ static_cast<const BYTE*>(m.pData), LONG(m.RowPitch), frame.width, frame.height, _desc.Height, readyPts });
            return true;
        }
        context->CopySubresourceRegion(_staging.get(), 0, 0, 0, 0, texture.get(), subresource, nullptr);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        THROW_IF_FAILED(context->Map(_staging.get(), 0, D3D11_MAP_READ_WRITE, 0, &mapped));
        auto unmap = wil::scope_exit([&] { context->Unmap(_staging.get(), 0); });
        // NV12 staging layout: luma rows, then interleaved chroma starting after the texture's full height
        fn({ static_cast<const BYTE*>(mapped.pData), LONG(mapped.RowPitch), frame.width, frame.height, _desc.Height, frame.ptsUs });
        return true;
    }
};

// --- preview for the PhoneCam app ------------------------------------------------------------------------------

// A small latest-frame copy (turned and fitted like the camera output) for the app's preview box, produced for
// each decoded frame while requested ("preview on" on stdin). Works without an app opening the camera.
// Memory "Local\PhoneCamPreview": PreviewHeader, then NV12 (PREVIEW_W x PREVIEW_H). seq is odd while writing.
constexpr UINT32 PREVIEW_W = 1280, PREVIEW_H = 720; // 480x270 looked pixelated: a tall phone picture only filled a third of the width
struct PreviewHeader
{
    UINT32 magic; // 'PCPV'
    UINT32 width, height;
    volatile LONG64 seq;
};
std::atomic<bool> g_previewOn = false;

class PreviewSink
{
    unsigned _slot;
    wil::unique_handle _mapping;
    PreviewHeader* _header = nullptr;
    BYTE* _pixels = nullptr;

public:
    explicit PreviewSink(unsigned slot) : _slot(slot) {}
    ~PreviewSink() { if (_header) UnmapViewOfFile(_header); }

    void Write(const DecodedFrame& frame)
    {
        if (frame.width != PREVIEW_W || frame.height != PREVIEW_H) return;
        if (!_header)
        {
            auto size = DWORD(sizeof(PreviewHeader) + PREVIEW_W * PREVIEW_H * 3 / 2);
            _mapping.reset(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, size, PhoneCamPreviewName(_slot).c_str()));
            if (!_mapping) return;
            _header = static_cast<PreviewHeader*>(MapViewOfFile(_mapping.get(), FILE_MAP_ALL_ACCESS, 0, 0, size));
            if (!_header) return;
            _header->magic = 'PCPV';
            _header->width = PREVIEW_W;
            _header->height = PREVIEW_H;
            InterlockedExchange64(&_header->seq, 0);
            _pixels = reinterpret_cast<BYTE*>(_header + 1);
        }
        InterlockedIncrement64(&_header->seq); // odd: writing
        for (UINT32 y = 0; y < PREVIEW_H; y++) memcpy(_pixels + size_t(y) * PREVIEW_W, frame.data + size_t(y) * frame.pitch, PREVIEW_W);
        auto uv = frame.data + size_t(frame.pitch) * frame.planeHeight;
        for (UINT32 y = 0; y < PREVIEW_H / 2; y++)
            memcpy(_pixels + size_t(PREVIEW_W) * PREVIEW_H + size_t(y) * PREVIEW_W, uv + size_t(y) * frame.pitch, PREVIEW_W);
        InterlockedIncrement64(&_header->seq); // even: complete
    }
};

// --- PhoneCam virtual camera shared memory ---------------------------------------------------------------------

// Writer side of the Global\PhoneCamFrame contract (PhoneCamShared.h). The media source creates the mapping
// and publishes the size it serves; this only opens it.
class SharedFrame
{
    unsigned _slot;
    wil::unique_handle _mapping, _event;
    PhoneCamSharedHeader* _header = nullptr;
    LONG64 _seq = 0;
    bool _warnedSize = false;

public:
    explicit SharedFrame(unsigned slot) : _slot(slot) {}
    ~SharedFrame() { if (_header) UnmapViewOfFile(_header); }

    bool TryOpen()
    {
        if (_header) return true;
        _mapping.reset(OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, PhoneCamSharedName(_slot).c_str()));
        if (!_mapping) return false;
        _header = static_cast<PhoneCamSharedHeader*>(MapViewOfFile(_mapping.get(), FILE_MAP_ALL_ACCESS, 0, 0, 0));
        if (!_header) return false;
        _event.reset(OpenEventW(EVENT_MODIFY_STATE, FALSE, PhoneCamFrameEventName(_slot).c_str()));
        return true;
    }

    bool Ready() const { return _header && _header->magic == PHONECAM_SHARED_MAGIC && _header->width && _header->height; }
    // the media source stamps each copy; recent stamps mean an app is reading frames
    bool InUse() const { return _header && _header->readQpcUs > NowUs() - 1000000; }
    UINT32 Width() const { return _header ? _header->width : 0; }
    UINT32 Height() const { return _header ? _header->height : 0; }

    // Copies a decoded frame in; returns the seq value written, or 0 if the size does not match the camera.
    LONG64 Write(const DecodedFrame& frame, int64_t& writeUs)
    {
        if (frame.width != _header->width || frame.height != _header->height)
        {
            if (!_warnedSize)
            {
                wprintf(L"frame %ux%u does not match the camera's %ux%u; set the phone to the same size\n",
                    frame.width, frame.height, _header->width, _header->height);
                _warnedSize = true;
            }
            return 0;
        }

        auto dst = reinterpret_cast<BYTE*>(_header + 1);
        InterlockedExchange64(&_header->seq, ++_seq * 2 - 1); // odd: writing
        for (UINT32 y = 0; y < frame.height; y++)
        {
            memcpy(dst + size_t(y) * frame.width, frame.data + size_t(y) * frame.pitch, frame.width);
        }
        auto uvSrc = frame.data + size_t(frame.pitch) * frame.planeHeight;
        auto uvDst = dst + size_t(frame.width) * frame.height;
        for (UINT32 y = 0; y < frame.height / 2; y++)
        {
            memcpy(uvDst + size_t(y) * frame.width, uvSrc + size_t(y) * frame.pitch, frame.width);
        }
        _header->ptsUs = frame.ptsUs;
        writeUs = NowUs();
        _header->writeQpcUs = writeUs;
        InterlockedExchange64(&_header->seq, _seq * 2); // even: complete
        if (_event) SetEvent(_event.get());
        return _seq * 2;
    }

    // true while the camera has not yet taken the last frame written
    bool LastFrameUnread() const { return _header && _seq && _header->readSeq != _seq * 2; }
    LONG64 ReadSeq() const { return _header ? _header->readSeq : 0; }

    void ReadBack(int64_t& readUs, LONG64& readSeq) const
    {
        readUs = _header->readQpcUs;
        readSeq = _header->readSeq;
    }
};

// --- virtual camera registration -------------------------------------------------------------------------------

// Session cameras exist while this process runs (no admin needed). System cameras stay installed for all users
// across reboots, like a plugged-in webcam, and need an elevated process to create or remove.
wil::com_ptr<IMFVirtualCamera> CreatePhoneCam(bool system, unsigned slot)
{
    wil::com_ptr<IMFVirtualCamera> camera;
    THROW_IF_FAILED_MSG(MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
        system ? MFVirtualCameraLifetime_System : MFVirtualCameraLifetime_Session,
        system ? MFVirtualCameraAccess_AllUsers : MFVirtualCameraAccess_CurrentUser,
        PhoneCamCameraName(slot).c_str(), PhoneCamSourceClsid(slot).c_str(), nullptr, 0, &camera),
        "MFCreateVirtualCamera failed (is the PhoneCam media source registered?)");
    return camera;
}

// Written by --install-camera (elevated) so later runs know the always-present camera exists and must not
// register a second, temporary one.
constexpr wchar_t PHONECAM_KEY[] = L"SOFTWARE\\PhoneCam";

std::wstring SystemCameraMarker(unsigned slot)
{
    return slot == 1 ? L"SystemCamera" : L"SystemCamera" + std::to_wstring(slot);
}

bool SystemCameraInstalled(unsigned slot)
{
    DWORD value = 0, size = sizeof(value);
    return RegGetValueW(HKEY_LOCAL_MACHINE, PHONECAM_KEY, SystemCameraMarker(slot).c_str(), RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS && value;
}

void SetSystemCameraMarker(bool installed, unsigned slot)
{
    if (installed)
    {
        DWORD value = 1;
        LOG_IF_WIN32_ERROR(RegSetKeyValueW(HKEY_LOCAL_MACHINE, PHONECAM_KEY, SystemCameraMarker(slot).c_str(), REG_DWORD, &value, sizeof(value)));
    }
    else
    {
        RegDeleteKeyValueW(HKEY_LOCAL_MACHINE, PHONECAM_KEY, SystemCameraMarker(slot).c_str());
    }
}

// --- USB (adb) -------------------------------------------------------------------------------------------------

std::wstring EnvVar(const wchar_t* name)
{
    wchar_t buffer[MAX_PATH]{};
    auto length = GetEnvironmentVariableW(name, buffer, MAX_PATH);
    return length && length < MAX_PATH ? buffer : L"";
}

// Runs "adb forward tcp:PORT localabstract:phonecam" so localhost:PORT reaches the phone app over USB.
// adb is looked up next to this exe (bundled), in the Android SDK, then on PATH. Returns false if adb is
// missing or no phone is attached/authorized.
bool AdbForward(const std::string& port)
{
    wchar_t exePath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, exePath, MAX_PATH);
    std::wstring exeDir = exePath;
    exeDir = exeDir.substr(0, exeDir.find_last_of(L'\\'));

    std::vector<std::wstring> candidates = { exeDir + L"\\adb.exe" };
    if (auto home = EnvVar(L"ANDROID_HOME"); !home.empty()) candidates.push_back(home + L"\\platform-tools\\adb.exe");
    if (auto local = EnvVar(L"LOCALAPPDATA"); !local.empty()) candidates.push_back(local + L"\\Android\\Sdk\\platform-tools\\adb.exe");
    candidates.push_back(L"adb.exe"); // PATH

    for (auto& adb : candidates)
    {
        if (adb.find(L'\\') != std::wstring::npos && GetFileAttributesW(adb.c_str()) == INVALID_FILE_ATTRIBUTES) continue;

        std::wstring command = L"\"" + adb + L"\" forward tcp:" + std::wstring(port.begin(), port.end()) + L" localabstract:phonecam";
        STARTUPINFOW startup{ sizeof(startup) };
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) continue;
        wil::unique_handle processHandle(process.hProcess), threadHandle(process.hThread);
        if (WaitForSingleObject(process.hProcess, 10000) != WAIT_OBJECT_0)
        {
            TerminateProcess(process.hProcess, 1);
            return false;
        }
        DWORD exitCode = 1;
        GetExitCodeProcess(process.hProcess, &exitCode);
        return exitCode == 0;
    }
    return false;
}

// --- Wi-Fi discovery -------------------------------------------------------------------------------------------

struct FoundPhone
{
    std::string host, port, model;
};

// Directed broadcast address of every active IPv4 adapter (e.g. 192.168.0.255). With several adapters (Ethernet,
// Wi-Fi, VPN, VM switches) Windows may send 255.255.255.255 out of only one of them.
std::vector<std::string> AdapterBroadcastAddresses()
{
    std::vector<std::string> result;
    ULONG size = 16 * 1024;
    std::vector<BYTE> buffer(size);
    auto adapters = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
    if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, nullptr, adapters, &size) == ERROR_BUFFER_OVERFLOW)
    {
        buffer.resize(size);
        adapters = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        if (GetAdaptersAddresses(AF_INET, GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, nullptr, adapters, &size) != NO_ERROR) return result;
    }
    for (auto a = adapters; a; a = a->Next)
    {
        if (a->OperStatus != IfOperStatusUp || a->IfType == IF_TYPE_SOFTWARE_LOOPBACK) continue;
        for (auto u = a->FirstUnicastAddress; u; u = u->Next)
        {
            auto ip = reinterpret_cast<sockaddr_in*>(u->Address.lpSockaddr);
            if (u->OnLinkPrefixLength == 0 || u->OnLinkPrefixLength >= 31) continue;
            ULONG mask = htonl(~0UL << (32 - u->OnLinkPrefixLength));
            in_addr broadcast{};
            broadcast.s_addr = ip->sin_addr.s_addr | ~mask;
            char text[INET_ADDRSTRLEN]{};
            inet_ntop(AF_INET, &broadcast, text, sizeof(text));
            result.push_back(text);
        }
    }
    return result;
}

// Broadcasts "PHONECAM?" to UDP 5557 and takes the first "PHONECAM1|<model>|<tcp port>" reply. The phone
// answers our own query, so no inbound firewall rule (or Windows "allow access" prompt) is needed.
std::optional<FoundPhone> DiscoverPhone(const std::string& target)
{
    SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return std::nullopt;
    auto closeSocket = wil::scope_exit([&] { closesocket(s); });
    BOOL on = TRUE;
    setsockopt(s, SOL_SOCKET, SO_BROADCAST, reinterpret_cast<const char*>(&on), sizeof(on));
    DWORD timeoutMs = 500;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));

    std::vector<std::string> targets = { target };
    if (target == "255.255.255.255")
    {
        for (auto& b : AdapterBroadcastAddresses()) targets.push_back(b);
    }

    for (int attempt = 0; attempt < 4; attempt++)
    {
        const char query[] = "PHONECAM?";
        for (auto& t : targets)
        {
            sockaddr_in to{};
            to.sin_family = AF_INET;
            to.sin_port = htons(5557);
            if (inet_pton(AF_INET, t.c_str(), &to.sin_addr) != 1) continue;
            sendto(s, query, int(sizeof(query) - 1), 0, reinterpret_cast<const sockaddr*>(&to), sizeof(to));
        }
        char reply[256];
        sockaddr_in from{};
        int fromLength = sizeof(from);
        int length = recvfrom(s, reply, sizeof(reply) - 1, 0, reinterpret_cast<sockaddr*>(&from), &fromLength);
        if (length <= 0) continue;
        reply[length] = 0;
        std::string text = reply;
        auto first = text.find('|'), last = text.rfind('|');
        if (text.rfind("PHONECAM1|", 0) != 0 || first == last) continue;
        char ip[INET_ADDRSTRLEN]{};
        inet_ntop(AF_INET, &from.sin_addr, ip, sizeof(ip));
        return FoundPhone{ ip, text.substr(last + 1), text.substr(first + 1, last - first - 1) };
    }
    return std::nullopt;
}

// --- command line ----------------------------------------------------------------------------------------------

// ---- phone microphone: plays the phone's PCM (48 kHz, mono, 16-bit) on a chosen output (WASAPI shared mode) ----------
// Windows has no microphone driver here: pick a virtual cable (VB-Cable) as the output, then choose the cable's other end
// as the microphone in OBS / Discord. --list-audio prints the outputs; --audio-device NAME picks one (substring of its
// name; "default" = the default output).
static std::vector<std::pair<std::wstring, wil::com_ptr<IMMDevice>>> RenderDevices()
{
    std::vector<std::pair<std::wstring, wil::com_ptr<IMMDevice>>> result;
    auto enumerator = wil::CoCreateInstance<MMDeviceEnumerator, IMMDeviceEnumerator>();
    wil::com_ptr<IMMDeviceCollection> devices;
    THROW_IF_FAILED(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices));
    UINT count = 0;
    THROW_IF_FAILED(devices->GetCount(&count));
    for (UINT i = 0; i < count; i++)
    {
        wil::com_ptr<IMMDevice> device;
        if (FAILED(devices->Item(i, &device))) continue;
        wil::com_ptr<IPropertyStore> props;
        if (FAILED(device->OpenPropertyStore(STGM_READ, &props))) continue;
        PROPVARIANT name;
        PropVariantInit(&name);
        if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &name)) && name.vt == VT_LPWSTR) result.push_back({ name.pwszVal, device });
        PropVariantClear(&name);
    }
    return result;
}

class AudioPlayer
{
public:
    bool Open(const std::wstring& wanted)
    {
        try
        {
            wil::com_ptr<IMMDevice> device;
            std::wstring label = L"default output";
            if (wanted == L"default")
            {
                auto enumerator = wil::CoCreateInstance<MMDeviceEnumerator, IMMDeviceEnumerator>();
                THROW_IF_FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device));
            }
            else
            {
                for (auto& [name, dev] : RenderDevices())
                    if (name.find(wanted) != std::wstring::npos) { device = dev; label = name; break; }
                if (!device) { wprintf(L"audio: no output matches \"%s\"\n", wanted.c_str()); return false; }
            }
            THROW_IF_FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&_client)));
            WAVEFORMATEX format{};
            format.wFormatTag = WAVE_FORMAT_PCM; format.nChannels = 1; format.nSamplesPerSec = 48000; format.wBitsPerSample = 16;
            format.nBlockAlign = 2; format.nAvgBytesPerSec = 96000;
            THROW_IF_FAILED(_client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 2000000 /* 200 ms */, 0, &format, nullptr));
            THROW_IF_FAILED(_client->GetBufferSize(&_frames));
            THROW_IF_FAILED(_client->GetService(__uuidof(IAudioRenderClient), reinterpret_cast<void**>(&_render)));
            THROW_IF_FAILED(_client->Start());
            wprintf(L"audio: playing the phone microphone on %s\n", label.c_str());
            return true;
        }
        catch (const std::exception& e) { wprintf(L"audio: could not open the output (%S)\n", e.what()); }
        catch (...) { wprintf(L"audio: could not open the output\n"); }
        _client.reset(); _render.reset();
        return false;
    }

    void Play(const std::vector<uint8_t>& pcm)
    {
        if (!_render) return;
        const UINT32 incoming = UINT32(pcm.size() / 2);
        UINT32 padding = 0;
        if (FAILED(_client->GetCurrentPadding(&padding)) || _frames - padding < incoming) { _dropped++; return; } // device behind: drop, never lag
        BYTE* data = nullptr;
        if (FAILED(_render->GetBuffer(incoming, &data))) return;
        memcpy(data, pcm.data(), incoming * 2);
        _render->ReleaseBuffer(incoming, 0);
        // level, to see in the log that sound arrives
        const int16_t* samples = reinterpret_cast<const int16_t*>(pcm.data());
        for (UINT32 i = 0; i < incoming; i++) { _sum += double(samples[i]) * samples[i]; }
        _count += incoming;
        auto now = NowUs();
        if (now - _lastLog > 2'000'000)
        {
            wprintf(L"audio: level %.0f (dropped %u packets)\n", _count ? sqrt(_sum / _count) : 0.0, _dropped);
            _lastLog = now; _sum = 0; _count = 0;
        }
    }

private:
    wil::com_ptr<IAudioClient> _client;
    wil::com_ptr<IAudioRenderClient> _render;
    UINT32 _frames = 0, _dropped = 0;
    double _sum = 0;
    UINT64 _count = 0;
    int64_t _lastLog = 0;
};

struct Options
{
    unsigned slot = 1;
    bool explicitSlot = false;
    std::string host = "127.0.0.1";
    std::string port = "5555";
    std::string code;
    double seconds = 0;
    std::wstring replay;
    bool realtime = false;
    bool noCamera = false;      // decode only, never touch the virtual camera
    bool noRegister = false;    // the camera is already registered (installed system camera, or VCamSample.exe)
    bool noAdb = false;         // do not run "adb forward" for the default USB address
    bool wifi = false;          // find the phone on the network instead of using --host/--port
    std::string discoverTo = "255.255.255.255";
    bool listBroadcasts = false; // diagnostics: print the addresses Wi-Fi discovery will query
    bool cpuOnly = false;        // decode and scale on the CPU even when a GPU is available
    int rotate = -1;             // tests: turn the picture this much (degrees clockwise) instead of what the phone says
    bool gpuOnly = false;        // decode on the GPU from the start (tests)
    bool installCamera = false;
    std::wstring audioDevice;    // --audio-device NAME: play the phone microphone here ("default" = default output; empty = not at all)
    bool listAudio = false;      // --list-audio: print the audio outputs and exit
    bool uninstallCamera = false;
    UINT32 scaleWidth = 0, scaleHeight = 0; // --scale-to WxH, decode-only tests of the scaler
    std::wstring dumpPath;                  // --dump FILE: raw NV12 of the 300th output frame
};

// Writes one frame as raw NV12 (view with: ffmpeg -f rawvideo -pix_fmt nv12 -s WxH -i FILE out.png).
void DumpFrame(const DecodedFrame& frame, const std::wstring& path)
{
    FILE* file = nullptr;
    if (_wfopen_s(&file, path.c_str(), L"wb") || !file) return;
    for (UINT32 y = 0; y < frame.height; y++) fwrite(frame.data + size_t(y) * frame.pitch, 1, frame.width, file);
    auto uv = frame.data + size_t(frame.pitch) * frame.planeHeight;
    for (UINT32 y = 0; y < frame.height / 2; y++) fwrite(uv + size_t(y) * frame.pitch, 1, frame.width, file);
    fclose(file);
    wprintf(L"dumped %ux%u NV12 frame to %s\n", frame.width, frame.height, path.c_str());
}

Options ParseArgs(int argc, wchar_t** argv)
{
    Options o;
    auto narrow = [](const wchar_t* s) { std::string r; for (; *s; s++) r += char(*s); return r; };
    for (int i = 1; i < argc; i++)
    {
        std::wstring a = argv[i];
        auto next = [&] { if (i + 1 >= argc) throw std::runtime_error("missing value for argument"); return argv[++i]; };
        if (a == L"--host") o.host = narrow(next());
        else if (a == L"--slot")
        {
            std::wstring value = next();
            if (value.size() != 1 || value[0] < L'1' || value[0] > L'4')
                throw std::runtime_error("--slot expects 1, 2, 3 or 4");
            o.slot = unsigned(value[0] - L'0');
            o.explicitSlot = true;
        }
        else if (a == L"--port") o.port = narrow(next());
        else if (a == L"--code") o.code = narrow(next());
        else if (a == L"--seconds") o.seconds = _wtof(next());
        else if (a == L"--replay") o.replay = next();
        else if (a == L"--realtime") o.realtime = true;
        else if (a == L"--no-camera") o.noCamera = true;
        else if (a == L"--no-register") o.noRegister = true;
        else if (a == L"--no-adb") o.noAdb = true;
        else if (a == L"--wifi") o.wifi = true;
        else if (a == L"--audio-device") o.audioDevice = next();
        else if (a == L"--list-audio") o.listAudio = true;
        else if (a == L"--discover-to") o.discoverTo = narrow(next()); // tests: query one address instead of broadcasting
        else if (a == L"--list-broadcasts") o.listBroadcasts = true;
        else if (a == L"--cpu") o.cpuOnly = true;
        else if (a == L"--gpu") o.gpuOnly = true;
        else if (a == L"--install-camera") o.installCamera = true;
        else if (a == L"--uninstall-camera") o.uninstallCamera = true;
        else if (a == L"--scale-to")
        {
            if (swscanf_s(next(), L"%ux%u", &o.scaleWidth, &o.scaleHeight) != 2) throw std::runtime_error("--scale-to expects WxH");
        }
        else if (a == L"--dump") o.dumpPath = next();
        else if (a == L"--preview") g_previewOn = true; // tests: make preview frames from the start
        else if (a == L"--rotate")
        {
            o.rotate = _wtoi(next());
            if (o.rotate % 90 || o.rotate < 0 || o.rotate > 270) throw std::runtime_error("--rotate expects 0, 90, 180 or 270");
        }
        else throw std::runtime_error("unknown argument");
    }
    return o;
}

static std::string Utf8(const std::wstring& text)
{
    if (text.empty()) return {};
    int bytes = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), int(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(bytes, ' ');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), int(text.size()), out.data(), bytes, nullptr, nullptr);
    return out;
}

int Run(const Options& options)
{
    if (options.listBroadcasts)
    {
        for (auto& b : AdapterBroadcastAddresses()) wprintf(L"%S\n", b.c_str());
        return 0;
    }

    if (options.listAudio)
    {
        // one name per line (the PC app lists them; UTF-8 so non-English device names survive)
        for (auto& [name, device] : RenderDevices()) puts(Utf8(name).c_str());
        return 0;
    }

    if (options.installCamera || options.uninstallCamera)
    {
        unsigned first = options.explicitSlot ? options.slot : 1;
        unsigned last = options.explicitSlot ? options.slot : 4;
        for (unsigned slot = first; slot <= last; slot++)
        {
            auto system = CreatePhoneCam(true, slot);
            if (options.installCamera)
            {
                THROW_IF_FAILED(system->Start(nullptr));
                SetSystemCameraMarker(true, slot);
                wprintf(L"%s installed as a system camera (stays available after this program exits)\n", PhoneCamCameraName(slot).c_str());
            }
            else
            {
                THROW_IF_FAILED(system->Remove());
                SetSystemCameraMarker(false, slot);
                wprintf(L"%s system camera removed\n", PhoneCamCameraName(slot).c_str());
            }
        }
        return 0;
    }

    // A second writer would corrupt the seqlock and mix two phones in one camera. The mutex spans
    // Windows sessions because the camera frame buffers do too; the OS releases it after a crash.
    auto writerName = L"Global\\PhoneCamReceiverSlot" + std::to_wstring(options.slot);
    wil::unique_handle writer(CreateMutexW(nullptr, FALSE, writerName.c_str()));
    THROW_LAST_ERROR_IF(!writer);
    auto acquired = WaitForSingleObject(writer.get(), 0);
    if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED)
        throw std::runtime_error("this camera slot already has a receiver; stop it or choose another --slot");
    auto releaseWriter = wil::scope_exit([&] { ReleaseMutex(writer.get()); });

    // With the system camera installed the device always exists (like a plugged-in webcam), so restarting this
    // program never makes apps lose it. Otherwise register a session camera that lives while this process runs.
    wil::com_ptr<IMFVirtualCamera> sessionCamera;
    auto removeSessionCamera = wil::scope_exit([&] { if (sessionCamera) sessionCamera->Remove(); });
    if (!options.noCamera && !options.noRegister && !SystemCameraInstalled(options.slot))
    {
        sessionCamera = CreatePhoneCam(false, options.slot);
        THROW_IF_FAILED(sessionCamera->Start(nullptr));
        wprintf(L"Streamsthetic camera registered for this session\n");
    }

    // The phone connection does not wait for an app to open the camera: frames are decoded either way and only
    // written while the camera is open (the shared memory exists only then).
    SharedFrame camera(options.slot);
    bool cameraReady = false; // shared memory exists: frames can be written
    bool cameraOpen = false;  // an app is reading frames right now
    int64_t nextCameraCheck = 0;
    auto checkCamera = [&] {
        if (options.noCamera) return;
        auto now = NowUs();
        if (now < nextCameraCheck) return;
        nextCameraCheck = now + 500000;
        cameraReady = camera.TryOpen() && camera.Ready();
        bool open = cameraReady && camera.InUse();
        if (open && !cameraOpen) wprintf(L"virtual camera serves %ux%u NV12\n", camera.Width(), camera.Height());
        if (!open && cameraOpen) wprintf(L"waiting for an app to open Streamsthetic\n");
        cameraOpen = open;
    };
    if (!options.noCamera) wprintf(L"waiting for an app to open Streamsthetic\n");

    // totals across all connections (live mode reconnects; replay is a single pass)
    std::vector<double> decodeMs, vcamMs, captureVcamMs, captureDecodedMs, allDecodeMs;
    int64_t lastReportUs = 0;
    std::vector<double> stepMs, scaleMs, readbackMs; // per-stage timing for the GPU path
    size_t framePackets = 0, framesOut = 0, framesWritten = 0, rejected = 0, wrongSize = 0, reportFrames = 0, reportDecoded = 0;
    UINT32 outWidth = 0, outHeight = 0;
    auto start = NowUs();
    auto deadline = options.seconds > 0 ? start + int64_t(options.seconds * 1e6) : INT64_MAX;
    auto nextReport = start + 5000000;

    // per-connection state, reset by StartConnection()
    H264Decoder decoder;
    int64_t bestRtt = INT64_MAX, clockOffset = 0; // phone_time ~= pc_time + clockOffset
    std::deque<std::pair<int64_t, int64_t>> received; // (ptsUs, rxUs) waiting for their decoded frame
    std::deque<std::pair<LONG64, std::pair<int64_t, int64_t>>> written; // seq -> (ptsUs, writeUs)
    LONG64 lastReadSeq = 0;
    bool synced = false;
    int64_t firstPts = -1, firstWall = 0;
    bool catchingUp = false;
    size_t skippedFrames = 0;
    long long caughtUpFromMs = 0;
    Source* currentSource = nullptr; // the stream being read (to see whether newer frames are already waiting)
    int64_t lagSinceUs = 0; // when frames first arrived more than 300 ms late (0 = not late)
    // Normal delay of this link (lowest capture->decoded delay over the last 5-10 s), so a late frame is judged
    // against what is usual for USB or Wi-Fi at this size, not a fixed number.
    struct { int64_t floorUs = INT64_MAX, windowFloorUs = INT64_MAX, windowStart = 0; size_t inRow = 0, total = 0; } late;

    // GPU available for streams larger than 1080p (see the switch after Decode); every stream starts on the CPU
    auto d3d = options.cpuOnly ? nullptr : CreateD3DManager();
    bool wantGpu = false;
    std::vector<BYTE> lastConfig;
    auto startConnection = [&] {
        decoder = H264Decoder(); // a new stream starts with its own codec config and keyframe
        decoder.Init(options.gpuOnly ? d3d.get() : nullptr);
        wantGpu = false;
        lastConfig.clear();
        bestRtt = INT64_MAX;
        clockOffset = 0;
        received.clear();
        synced = false;
        firstPts = -1;
        catchingUp = false;
        lagSinceUs = 0;
        auto lateTotal = late.total; // the count is for the whole run
        late = {};
        late.total = lateTotal;
    };

    Nv12Scaler cpuScaler;
    GpuScaler gpuScaler;
    GpuReadback gpuReadback{ true }; // pipelined: see GpuReadback
    // the app's preview box: own scaler and read-back, so they never resize the camera's
    GpuScaler previewGpuScaler;
    GpuReadback previewReadback;
    Nv12Scaler previewCpuScaler;
    PreviewSink preview(options.slot);
    struct { LONG64 lastReadSeq = 0; size_t reads = 0, frames = 0, skipped = 0; int64_t windowStart = 0; bool active = false; } pace;
    int64_t rx = 0; // receive time of the frame being delivered
    auto deliver = [&](const DecodedFrame& frame) {
        outWidth = frame.width;
        outHeight = frame.height;
        if (!options.dumpPath.empty() && framesOut == 300) DumpFrame(frame, options.dumpPath);

        if (options.noCamera)
        {
            allDecodeMs.push_back((NowUs() - rx) / 1000.0);
            return;
        }
        if (!cameraReady) return; // no app has opened PhoneCam yet
        int64_t writeUs = 0;
        auto seq = camera.Write(frame, writeUs);
        if (!seq)
        {
            wrongSize++;
            return;
        }
        framesWritten++;
        reportFrames++;
        decodeMs.push_back((writeUs - rx) / 1000.0);
        allDecodeMs.push_back(decodeMs.back());
        written.push_back({ seq, { frame.ptsUs, writeUs } });
        if (written.size() > 240) written.pop_front();

        int64_t readUs;
        LONG64 readSeq;
        camera.ReadBack(readUs, readSeq);
        if (readSeq != lastReadSeq)
        {
            for (auto& [s, w] : written)
            {
                if (s != readSeq) continue;
                lastReadSeq = readSeq;
                vcamMs.push_back((readUs - w.second) / 1000.0);
                if (bestRtt != INT64_MAX) captureVcamMs.push_back((readUs - (w.first - clockOffset)) / 1000.0);
                break;
            }
        }
    };

    auto onFrame = [&](const VideoFrame& decoded) {
        framesOut++;
        reportDecoded++;
        // delay up to "decoded": what the app shows while no other app has the camera open
        if (bestRtt != INT64_MAX) captureDecodedMs.push_back((NowUs() - (decoded.ptsUs - clockOffset)) / 1000.0);
        if (decoded.width * decoded.height > 1920u * 1080u && !decoder.UsesGpu()) wantGpu = true;
        rx = NowUs();
        while (!received.empty() && received.front().first < decoded.ptsUs) received.pop_front();
        if (!received.empty() && received.front().first == decoded.ptsUs)
        {
            rx = received.front().second;
            received.pop_front();
        }

        // fit the size the camera serves (or --scale-to in decode-only tests)
        checkCamera();
        auto targetW = options.noCamera ? options.scaleWidth : (cameraReady ? camera.Width() : 0);
        auto targetH = options.noCamera ? options.scaleHeight : (cameraReady ? camera.Height() : 0);
        int rotation = options.rotate >= 0 ? options.rotate : g_rotation.load();

        // Publish the freshest decoded frame. Readers take the latest shared frame rather than queueing;
        // a separate 15 fps throttle here made the PC preview trail the camera output noticeably.
        if (g_previewOn)
        {
            auto show = [&](const DecodedFrame& f) {
                PaintBars(f, decoded.width, decoded.height, rotation);
                preview.Write(f);
            };
            if (decoder.UsesGpu())
            {
                wil::com_ptr<IMFSample> previewSample; // not "small": a Windows header defines that as char
                auto previewFrame = previewGpuScaler.Scale(d3d.get(), decoded, PREVIEW_W, PREVIEW_H, previewSample, rotation);
                if (!previewReadback.Read(previewFrame, show)) LockFrame(previewFrame, show);
            }
            else
            {
                LockFrame(decoded, [&](const DecodedFrame& f) { show(previewCpuScaler.Scale(f, PREVIEW_W, PREVIEW_H, rotation)); });
            }
        }
        // Without a camera mapping there is nowhere to deliver the full-size pixels. The preview above
        // already has its own small readback. Preserve decode-only/dump diagnostics and camera bootstrap:
        // a newly opened mapping still gets frames before InUse can become true.
        if (!options.noCamera && !cameraReady && options.dumpPath.empty()) return;
        bool scale = targetW && targetH && (decoded.width != targetW || decoded.height != targetH || rotation != 0 || g_mirror.load());

        // Pace the copy work to the app that reads the camera. OBS set to 3840x2160 reads 30 fps while the phone
        // sends 60: copying every 4K frame (12 MB each, out of video memory) made the PC fall behind and lag.
        // Every frame is still decoded (H.264 needs them); only frames the app would never see are not copied.
        if (!options.noCamera && cameraReady)
        {
            auto now = NowUs();
            auto readSeq = camera.ReadSeq();
            if (readSeq != pace.lastReadSeq) { pace.lastReadSeq = readSeq; pace.reads++; }
            pace.frames++;
            if (now - pace.windowStart >= 1000000)
            {
                // the app reads clearly fewer frames than arrive (30 of 60): only copy when it took the last one
                bool slower = pace.reads > 0 && pace.reads * 4 < pace.frames * 3;
                if (slower != pace.active)
                {
                    pace.active = slower;
                    if (slower) wprintf(L"the app reads %zu of %zu frames per second: copying only the frames it takes\n", pace.reads, pace.frames);
                }
                pace.reads = pace.frames = 0;
                pace.windowStart = now;
            }
            if (pace.active && camera.LastFrameUnread())
            {
                pace.skipped++;
                return;
            }
        }

        // Behind the phone (a slow copy, a big keyframe, the app's own load on the same GPU): don't copy frames that
        // arrive late, so the next ones catch up within a few frames instead of the delay piling up. Copying a 4K
        // frame takes ~11 ms with OBS busy on the GPU; decoding one (always needed for H.264) well under 1 ms.
        // At most 3 in a row, so the picture keeps moving even if the phone clock estimate is off.
        if (!options.noCamera && cameraReady && bestRtt != INT64_MAX)
        {
            auto now = NowUs();
            auto lagUs = now - (decoded.ptsUs - clockOffset);
            late.windowFloorUs = std::min(late.windowFloorUs, lagUs);
            if (now - late.windowStart >= 5000000)
            {
                late.floorUs = late.windowFloorUs;
                late.windowFloorUs = lagUs;
                late.windowStart = now;
            }
            auto normalUs = std::min(late.floorUs, late.windowFloorUs);
            // only when the next packet (28-byte header at least) is already here: then this PC is behind. A slow
            // phone or network can't be helped by dropping frames here.
            bool newerWaiting = currentSource && currentSource->Pending() >= 28;
            if (lagUs > normalUs + 50000 && newerWaiting && late.inRow < 3)
            {
                late.inRow++;
                late.total++;
                return;
            }
            late.inRow = 0;
        }

        if (!scale)
        {
            // full-size frames from the GPU use the same persistent staging copy as scaled ones (Lock2D slowed down)
            auto t1 = NowUs();
            if (!decoder.UsesGpu() || !gpuReadback.Read(decoded, deliver)) LockFrame(decoded, deliver);
            if (decoder.UsesGpu()) readbackMs.push_back((NowUs() - t1) / 1000.0);
        }
        else if (decoder.UsesGpu())
        {
            wil::com_ptr<IMFSample> scaled;
            auto t0 = NowUs();
            auto scaledFrame = gpuScaler.Scale(d3d.get(), decoded, targetW, targetH, scaled, rotation);
            auto t1 = NowUs();
            auto withBars = [&](const DecodedFrame& f) {
                PaintBars(f, decoded.width, decoded.height, rotation);
                deliver(f);
            };
            if (!gpuReadback.Read(scaledFrame, withBars)) LockFrame(scaledFrame, withBars);
            scaleMs.push_back((t1 - t0) / 1000.0);
            readbackMs.push_back((NowUs() - t1) / 1000.0);
        }
        else
        {
            LockFrame(decoded, [&](const DecodedFrame& f) { 
                auto scaledFrame = cpuScaler.Scale(f, targetW, targetH, rotation);
                PaintBars(scaledFrame, f.width, f.height, rotation);
                deliver(scaledFrame);
            });
        }
    };

    // Streams one connection (or the replay file) until it ends, a read fails or the deadline passes.
    // Returns how many packets arrived: 0 means the connection never carried a stream.
    auto streamFrom = [&](Source& source) {
        currentSource = &source;
        auto forgetSource = wil::scope_exit([&] { currentSource = nullptr; });
        Packet packet;
        size_t packets = 0;
        AudioPlayer audio;
        bool audioTried = false, audioOk = false;
        while (NowUs() < deadline && ReadPacket(source, packet))
        {
            if (++packets == 1 && options.replay.empty()) wprintf(L"receiving from the phone\n");
            auto rx = NowUs();
            if (packet.type == TYPE_CLOCK)
            {
                auto rtt = rx - packet.sendUs;
                if (rtt < bestRtt)
                {
                    bestRtt = rtt;
                    clockOffset = packet.ptsUs - (packet.sendUs + rtt / 2);
                }
                continue;
            }
            if (packet.type == TYPE_SETTINGS)
            {
                wprintf(L"phone settings: %.*S\n", int(packet.payload.size()), reinterpret_cast<const char*>(packet.payload.data()));
                // "rot=90": how far the phone's frames must turn to be upright (phones without it: 0)
                std::string text(packet.payload.begin(), packet.payload.end());
                auto at = (";" + text).find(";rot=");
                int rotation = at == std::string::npos ? 0 : atoi(text.c_str() + at + 4);
                if (rotation % 90 == 0 && rotation >= 0 && rotation <= 270 && rotation != g_rotation.exchange(rotation))
                {
                    wprintf(L"picture turned %d degrees to stay upright\n", rotation);
                }
                auto mat = (";" + text).find(";mirror=");
                bool mirror = mat != std::string::npos && atoi(text.c_str() + mat + 7) == 1;
                if (mirror != g_mirror.exchange(mirror)) wprintf(L"picture mirrored: %d\n", mirror ? 1 : 0);
                continue;
            }
            if (packet.type == TYPE_REJECT) throw std::runtime_error("pairing code refused");
            if (packet.type == TYPE_AUDIO)
            {
                if (!options.audioDevice.empty())
                {
                    if (!audioTried) { audioTried = true; audioOk = audio.Open(options.audioDevice); }
                    if (audioOk) audio.Play(packet.payload);
                }
                continue;
            }
            if (packet.type != TYPE_CONFIG && packet.type != TYPE_FRAME) continue;
            if (packet.type == TYPE_FRAME) framePackets++;

            // joining mid-stream: nothing before the first config or keyframe can be decoded
            if (!synced)
            {
                if (packet.type == TYPE_FRAME && !(packet.flags & 1)) continue;
                synced = true;
            }

            if (options.realtime && packet.type == TYPE_FRAME)
            {
                if (firstPts < 0) { firstPts = packet.ptsUs; firstWall = rx; }
                auto due = firstWall + (packet.ptsUs - firstPts);
                while (NowUs() < due) Sleep(0);
                rx = NowUs();
            }

            // Latency guard, last resort: if this PC stays more than 300 ms behind the phone for a whole second (even
            // with late frames not copied, see onFrame), skip ahead to the next keyframe instead of letting the delay
            // build up. A single late frame no longer triggers it: that froze the picture for up to a second.
            if (packet.type == TYPE_FRAME && bestRtt != INT64_MAX && !options.realtime)
            {
                auto lagMs = (rx - (packet.ptsUs - clockOffset)) / 1000;
                if (lagMs <= 300) lagSinceUs = 0;
                else if (!lagSinceUs) lagSinceUs = rx;
                if (!catchingUp && lagSinceUs && rx - lagSinceUs >= 1000000)
                {
                    catchingUp = true;
                    skippedFrames = 0;
                    caughtUpFromMs = lagMs;
                    lagSinceUs = 0;
                }
                if (catchingUp)
                {
                    if (!(packet.flags & 1))
                    {
                        skippedFrames++;
                        continue;
                    }
                    catchingUp = false;
                    wprintf(L"fell %lld ms behind the phone; skipped %zu frames to catch up\n", caughtUpFromMs, skippedFrames);
                }
            }

            if (packet.type == TYPE_FRAME) received.push_back({ packet.ptsUs, rx });
            auto decodeStart = NowUs();
            if (!decoder.Decode(packet.payload, packet.ptsUs, onFrame)) rejected++;
            stepMs.push_back((NowUs() - decodeStart) / 1000.0);

            // The CPU is fastest up to 1080p (no copy back from video memory) but too slow beyond it (4K60:
            // 29 fps). Once the first frame shows a larger stream, move to the GPU and decode that keyframe again.
            if (wantGpu && !decoder.UsesGpu() && d3d)
            {
                wantGpu = false;
                decoder = H264Decoder();
                decoder.Init(d3d.get());
                wprintf(L"stream larger than 1080p: decoding on the GPU\n");
                if (lastConfig.size() && !decoder.Decode(lastConfig, 0, [](const VideoFrame&) {})) rejected++;
                if (packet.type == TYPE_FRAME && (packet.flags & 1) && !decoder.Decode(packet.payload, packet.ptsUs, [](const VideoFrame&) {})) rejected++;
            }
            if (packet.type == TYPE_CONFIG) lastConfig = packet.payload;

            if (rx >= nextReport && !options.noCamera)
            {
                // reported every second (it was every 5): the readouts in the app follow the stream closely
                double seconds = lastReportUs ? std::max(0.5, (rx - lastReportUs) / 1e6) : 1.0;
                lastReportUs = rx;
                auto capture = captureVcamMs.empty() ? std::wstring(L"n/a (no phone clock)") : std::to_wstring(Median(captureVcamMs)).substr(0, 6) + L" ms";
                // "from phone" = frames decoded, the stream's real rate; "written" is lower on purpose when the app
                // reads fewer frames or frames arrive late (not copied)
                auto captureDecoded = captureDecodedMs.empty() ? std::wstring(L"n/a") : std::to_wstring(Median(captureDecodedMs)).substr(0, 6) + L" ms";
                wprintf(L"%5.1f fps written | decode median %5.1f ms | written->vcam median %5.1f ms (n=%zu) | capture->vcam median %s | rejected %zu | from phone %.1f fps | capture->decoded median %s\n",
                    reportFrames / seconds, Median(decodeMs), Median(vcamMs), vcamMs.size(), capture.c_str(), rejected, reportDecoded / seconds, captureDecoded.c_str());
                if (!readbackMs.empty())
                {
                    wprintf(L"   timing: whole step median %.1f p95 %.1f | gpu scale %.1f | readback+write %.1f ms\n",
                        Median(stepMs), Percentile(stepMs, 0.95), Median(scaleMs), Median(readbackMs));
                }
                stepMs.clear(); scaleMs.clear(); readbackMs.clear();
                decodeMs.clear(); vcamMs.clear(); captureVcamMs.clear(); captureDecodedMs.clear();
                reportFrames = 0;
                reportDecoded = 0;
                nextReport = rx + 1000000;
            }
        }
        return packets;
    };

    if (!options.replay.empty())
    {
        FileSource file(options.replay);
        startConnection();
        streamFrom(file);
    }
    else
    {
        // Live: keep the camera registered and reconnect whenever the phone stream is missing or drops (cable
        // wiggle, Wi-Fi blip, app restarted). The camera keeps showing the last frame meanwhile.
        // The default address (127.0.0.1:5555) is the USB path: adb forwards it to the phone app's socket.
        bool usb = !options.wifi && options.host == "127.0.0.1" && options.port == "5555" && !options.noAdb;
        auto host = options.host, port = options.port;
        bool needDiscovery = options.wifi;
        size_t streams = 0, failures = 0;
        // setting changes from the PhoneCam app arrive as lines on stdin; ends quietly when stdin closes
        std::thread([] {
            std::string line;
            while (std::getline(std::cin, line))
            {
                if (!line.empty() && line.back() == '\r') line.pop_back();
                // .NET writers put a UTF-8 byte order mark before the first line; it hid the first command
                if (line.rfind("\xEF\xBB\xBF", 0) == 0) line.erase(0, 3);
                if (line.rfind("set ", 0) == 0) SendSettingsToPhone(line.substr(4));
                else if (line == "preview on" || line == "preview off") g_previewOn = line == "preview on";
                else if (!line.empty()) wprintf(L"unknown command (expected: set key=value;...)\n");
            }
        }).detach();
        while (NowUs() < deadline)
        {
            try
            {
                if (usb && !AdbForward(port)) throw std::runtime_error("no phone on USB");
                if (needDiscovery)
                {
                    auto phone = DiscoverPhone(options.discoverTo);
                    if (!phone) throw std::runtime_error("no Streamsthetic found on the network");
                    host = phone->host;
                    port = phone->port;
                    needDiscovery = false;
                    wprintf(L"found %S at %S:%S\n", phone->model.c_str(), host.c_str(), port.c_str());
                }
                SocketSource socket(host, port);
                if (!options.code.empty())
                {
                    auto hello = "P" + options.code;
                    socket.Send(hello.data(), int(hello.size()));
                }
                startConnection();
                {
                    std::lock_guard lock(g_phoneLock);
                    g_phone = &socket;
                }
                auto forgetPhone = wil::scope_exit([&] { std::lock_guard lock(g_phoneLock); g_phone = nullptr; });

                // clock sync: 'T' + big-endian token, answered by a TYPE_CLOCK packet
                std::atomic<bool> stopClock = false;
                std::thread clockThread([&] {
                    for (int sent = 0; !stopClock; sent++)
                    {
                        BYTE request[9] = { 'T' };
                        auto token = NowUs();
                        for (int i = 0; i < 8; i++) request[1 + i] = BYTE(token >> (56 - 8 * i));
                        if (!socket.Send(request, sizeof(request))) return;
                        for (int waited = 0; waited < (sent < 20 ? 200 : 1000) && !stopClock; waited += 50) Sleep(50);
                    }
                });
                // join even when the stream throws: destroying a joinable std::thread terminates the process
                auto joinClock = wil::scope_exit([&] { stopClock = true; clockThread.join(); });
                auto packets = streamFrom(socket);
                joinClock.reset();
                // adb accepts the local connection even when the phone app is not streaming, then closes it:
                // only a connection that carried data counts as a working stream
                if (!packets) throw std::runtime_error("connection carried no stream");
                streams++;
                failures = 0;
                wprintf(L"phone stream ended (%zu so far); reconnecting\n", streams);
                // a stream that was working usually comes straight back (blip, app restart): retry quickly
                if (NowUs() < deadline) Sleep(100);
                continue;
            }
            catch (const std::exception& e) // includes decoder errors (wil::ResultException) and corrupt streams
            {
                // the phone may have a new address (DHCP, other network): look for it again next time
                if (options.wifi) needDiscovery = true;
                if (std::string(e.what()) == "pairing code refused")
                {
                    wprintf(L"the phone refused the pairing code\n");
                    Sleep(2000); // the phone locks pairing for a while after a few wrong tries: do not hammer it
                }
                if (failures++ % 10 == 0)
                {
                    wprintf(L"waiting for the phone%S (%S); is the Streamsthetic app streaming? retrying every second\n",
                        options.wifi ? "" : (" at " + host + ":" + port).c_str(), e.what());
                }
            }
            if (NowUs() < deadline) Sleep(1000);
        }
    }

    auto elapsedS = (NowUs() - start) / 1e6;
    wprintf(L"\nframe packets %zu, decoded %zu (%.1f s, %.0f fps), written to camera %zu, not copied (app reads slower) %zu, not copied (arrived late) %zu, rejected %zu, wrong size %zu, output %ux%u\n",
        framePackets, framesOut, elapsedS, framesOut / std::max(elapsedS, 0.001), framesWritten, pace.skipped, late.total, rejected, wrongSize, outWidth, outHeight);
    wprintf(L"received->decoded%s: median %.2f ms, p95 %.2f ms, max %.2f ms\n", options.noCamera ? L"" : L"+written",
        Median(allDecodeMs), Percentile(allDecodeMs, 0.95), allDecodeMs.empty() ? 0 : *std::max_element(allDecodeMs.begin(), allDecodeMs.end()));

    if (!options.replay.empty() && !options.realtime)
    {
        // self-test: every frame after the first keyframe must decode at a sane size
        bool ok = framesOut + 5 >= framePackets && framesOut > 0 && outWidth >= 640 && outHeight >= 360;
        wprintf(L"SELF-TEST %s\n", ok ? L"PASS" : L"FAIL");
        return ok ? 0 : 2;
    }
    return 0;
}
} // namespace

// --- crash dumps -----------------------------------------------------------------------------------------------

// A crash writes a minidump where the PhoneCam app's "Report a problem" finds it
// (%LOCALAPPDATA%\PhoneCam\diagnostics\problems), then ends the process with the crash code as its exit code
// (no Windows error dialog: the app tells the user and records the problem).
LONG WINAPI WriteCrashDump(EXCEPTION_POINTERS* info)
{
    auto dir = EnvVar(L"LOCALAPPDATA");
    if (!dir.empty())
    {
        for (auto part : { L"\\Streamsthetic", L"\\diagnostics", L"\\problems" })
        {
            dir += part;
            CreateDirectoryW(dir.c_str(), nullptr);
        }
        SYSTEMTIME t;
        GetLocalTime(&t);
        wchar_t path[MAX_PATH];
        swprintf_s(path, L"%s\\%04u%02u%02u-%02u%02u%02u-receiver-crash.dmp", dir.c_str(), t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
        wil::unique_hfile file(CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (file)
        {
            MINIDUMP_EXCEPTION_INFORMATION exception{ GetCurrentThreadId(), info, FALSE };
            MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), file.get(),
                MINIDUMP_TYPE(MiniDumpWithThreadInfo | MiniDumpWithIndirectlyReferencedMemory), &exception, nullptr, nullptr);
        }
        fwprintf(stderr, L"error: the video part of Streamsthetic crashed (code 0x%08X)\n", info->ExceptionRecord->ExceptionCode);
    }
    return EXCEPTION_EXECUTE_HANDLER;
}

int wmain(int argc, wchar_t** argv)
{
    // status lines are read live by the PhoneCam app through a pipe, where the C runtime would otherwise buffer them
    setvbuf(stdout, nullptr, _IONBF, 0);
    SetErrorMode(SEM_NOGPFAULTERRORBOX);
    SetUnhandledExceptionFilter(WriteCrashDump);
    // test of the crash recording (tools/diagnostics_selftest.ps1)
    for (int i = 1; i < argc; i++)
    {
        if (std::wstring_view(argv[i]) == L"--simulate-crash") *static_cast<volatile int*>(nullptr) = 1;
    }
    try
    {
        auto options = ParseArgs(argc, argv);
        WSADATA wsa;
        WSAStartup(MAKEWORD(2, 2), &wsa);
        auto com = wil::CoInitializeEx(COINIT_MULTITHREADED);
        THROW_IF_FAILED(MFStartup(MF_VERSION));
        auto result = Run(options);
        MFShutdown();
        WSACleanup();
        return result;
    }
    catch (const wil::ResultException& e)
    {
        fwprintf(stderr, L"error: 0x%08X\n", e.GetErrorCode());
    }
    catch (const std::exception& e)
    {
        fprintf(stderr, "error: %s\n", e.what());
    }
    return 1;
}
