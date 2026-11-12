// No registration, administrator rights, camera installation, or running receiver needed.
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <wrl/client.h>
#include <iostream>
#include <stdexcept>
#include "../VCamSampleSource/PhoneCamShared.h"
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) { if (FAILED(hr)) throw hr; }
static void Require(bool value) { if (!value) throw E_FAIL; }
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    try
    {
        Check(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
        Check(MFStartup(MF_VERSION));
        auto module = LoadLibraryW(argv[1]);
        if (!module) throw HRESULT_FROM_WIN32(GetLastError());
        auto factory = reinterpret_cast<HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**)>(GetProcAddress(module, "DllGetClassObject"));
        Require(factory != nullptr);
        const wchar_t* expectedIds[] = {
            L"{31621596-4208-4d72-9066-1f9507074423}", L"{31621596-4208-4d72-9066-1f9507074424}",
            L"{31621596-4208-4d72-9066-1f9507074425}", L"{31621596-4208-4d72-9066-1f9507074426}" };
        const wchar_t* expectedMaps[] = { L"Global\\PhoneCamFrame", L"Global\\PhoneCamFrame2", L"Global\\PhoneCamFrame3", L"Global\\PhoneCamFrame4" };
        ComPtr<IMFMediaSource> sources[4];
        for (unsigned slot = 1; slot <= 4; ++slot)
        {
            GUID expected;
            Check(CLSIDFromString(expectedIds[slot - 1], &expected));
            Require(PhoneCamSlotClsid(slot) == expected);
            Require(PhoneCamSourceClsid(slot) == expectedIds[slot - 1]);
            Require(PhoneCamSharedName(slot) == expectedMaps[slot - 1]);
            ComPtr<IClassFactory> cf;
            Check(factory(expected, IID_PPV_ARGS(&cf)));
            ComPtr<IMFActivate> activation;
            Check(cf->CreateInstance(nullptr, IID_PPV_ARGS(&activation)));
            GUID actual;
            Check(activation->GetGUID(MFT_TRANSFORM_CLSID_Attribute, &actual));
            Require(actual == expected);
            Check(activation->ActivateObject(IID_PPV_ARGS(&sources[slot - 1])));
            ComPtr<IMFPresentationDescriptor> pd;
            Check(sources[slot - 1]->CreatePresentationDescriptor(&pd));
            BOOL selected;
            ComPtr<IMFStreamDescriptor> sd;
            Check(pd->GetStreamDescriptorByIndex(0, &selected, &sd));
            ComPtr<IMFMediaTypeHandler> handler;
            Check(sd->GetMediaTypeHandler(&handler));
            DWORD count;
            Check(handler->GetMediaTypeCount(&count));
            Require(count == 12);
            bool portrait = false;
            for (DWORD i = 0; i < count; ++i)
            {
                ComPtr<IMFMediaType> type;
                Check(handler->GetMediaTypeByIndex(i, &type));
                UINT32 width, height;
                Check(MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height));
                portrait |= width == 2160 && height == 3840;
            }
            Require(portrait);
            std::wcout << L"PASS slot " << slot << L": distinct source identity and 12 video formats\n";
        }
        for (unsigned i = 0; i < 4; ++i)
        {
            for (unsigned j = 0; j < i; ++j) Require(sources[i].Get() != sources[j].Get());
            Check(sources[i]->Shutdown());
            sources[i].Reset();
        }
        ComPtr<IClassFactory> unknown;
        Require(FAILED(factory(GUID_NULL, IID_PPV_ARGS(&unknown))));
        FreeLibrary(module);
        MFShutdown();
        CoUninitialize();
        std::cout << "CAMERA SLOT TEST PASS\n";
        return 0;
    }
    catch (HRESULT hr) { std::cerr << "FAIL HRESULT 0x" << std::hex << hr << '\n'; return 1; }
}
