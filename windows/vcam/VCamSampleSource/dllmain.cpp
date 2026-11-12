#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "FrameGenerator.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "Activator.h"

// 31621596-4208-4d72-9066-1f9507074423 (PhoneCam)
GUID CLSID_VCam = { 0x31621596,0x4208,0x4d72,{0x90,0x66,0x1f,0x95,0x07,0x07,0x44,0x23} };
HMODULE _hModule;

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved)
{
	switch (dwReason)
	{
	case DLL_PROCESS_ATTACH:
		_hModule = hModule;
		WinTraceRegister();
		WINTRACE(L"DllMain DLL_PROCESS_ATTACH '%s'", GetCommandLine());
		DisableThreadLibraryCalls(hModule);

		wil::SetResultLoggingCallback([](wil::FailureInfo const& failure) noexcept
			{
				wchar_t str[2048];
				if (SUCCEEDED(wil::GetFailureLogString(str, _countof(str), failure)))
				{
					WinTrace(2, 0, str); // 2 => error
				}
			});
		break;

	case DLL_PROCESS_DETACH:
		WINTRACE(L"DllMain DLL_PROCESS_DETACH '%s'", GetCommandLine());
		WinTraceUnregister();
		break;
	}
	return TRUE;
}

struct ClassFactory : winrt::implements<ClassFactory, IClassFactory>
{
	unsigned _slot;
	explicit ClassFactory(unsigned slot) : _slot(slot) {}
	STDMETHODIMP CreateInstance(IUnknown* outer, GUID const& riid, void** result) noexcept final
	{
		RETURN_HR_IF_NULL(E_POINTER, result);
		*result = nullptr;
		if (outer)
			RETURN_HR(CLASS_E_NOAGGREGATION);

		auto vcam = winrt::make_self<Activator>();
		RETURN_IF_FAILED(vcam->Initialize(_slot));
		auto hr = vcam->QueryInterface(riid, result);
		if (FAILED(hr))
		{
			auto iid = GUID_ToStringW(riid);
			WINTRACE(L"ClassFactory QueryInterface failed on IID %s", iid.c_str());
		}
		return hr;
	}

	STDMETHODIMP LockServer(BOOL) noexcept final
	{
		return S_OK;
	}
};

__control_entrypoint(DllExport)
STDAPI DllCanUnloadNow()
{
	if (winrt::get_module_lock())
	{
		WINTRACE(L"DllCanUnloadNow S_FALSE");
		return S_FALSE;
	}

	winrt::clear_factory_cache();
	WINTRACE(L"DllCanUnloadNow S_OK");
	return S_OK;
}

_Check_return_
STDAPI DllGetClassObject(_In_ REFCLSID rclsid, _In_ REFIID riid, _Outptr_ LPVOID FAR* ppv)
{
	WINTRACE(L"DllGetClassObject rclsid:%s riid:%s", GUID_ToStringW(rclsid).c_str(), GUID_ToStringW(riid).c_str());
	RETURN_HR_IF_NULL(E_POINTER, ppv);
	*ppv = nullptr;

	for (unsigned slot = 1; slot <= 4; ++slot)
	{
		if (rclsid == PhoneCamSlotClsid(slot))
			return winrt::make_self<ClassFactory>(slot)->QueryInterface(riid, ppv);
	}

	RETURN_HR(E_NOINTERFACE);
}

using registry_key = winrt::handle_type<registry_traits>;

STDAPI DllRegisterServer()
{
	std::wstring exePath = wil::GetModuleFileNameW(_hModule).get();
	WINTRACE(L"DllRegisterServer '%s'", exePath.c_str());
    // Frame Server runs outside the user process: all four COM classes must be machine registered.
    for (unsigned slot = 1; slot <= 4; ++slot)
    {
        auto clsid = GUID_ToStringW(PhoneCamSlotClsid(slot), false);
        std::wstring path = L"Software\\Classes\\CLSID\\" + clsid + L"\\InprocServer32";
        registry_key key;
        RETURN_IF_WIN32_ERROR(RegWriteKey(HKEY_LOCAL_MACHINE, path.c_str(), key.put()));
        RETURN_IF_WIN32_ERROR(RegWriteValue(key.get(), nullptr, exePath));
        RETURN_IF_WIN32_ERROR(RegWriteValue(key.get(), L"ThreadingModel", L"Both"));
    }
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    // Missing slots are normal when uninstalling an older one-camera installation.
    LSTATUS firstError = ERROR_SUCCESS;
    for (unsigned slot = 1; slot <= 4; ++slot)
    {
        auto clsid = GUID_ToStringW(PhoneCamSlotClsid(slot), false);
        std::wstring path = L"Software\\Classes\\CLSID\\" + clsid;
        auto error = RegDeleteTree(HKEY_LOCAL_MACHINE, path.c_str());
        if (error != ERROR_SUCCESS && error != ERROR_FILE_NOT_FOUND && firstError == ERROR_SUCCESS)
            firstError = error;
    }
    RETURN_IF_WIN32_ERROR(firstError);
    return S_OK;
}
