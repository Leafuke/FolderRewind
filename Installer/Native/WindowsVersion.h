#pragma once
#include <windows.h>
#include <cstdlib>

// MSI's custom action server can apply version-compatibility shims even to
// RtlGetVersion. Read the installed OS's native registry view in both hosts.
inline HRESULT SupportedWindows(bool& supported)
{
    const wchar_t* key = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    DWORD major = 0, bytes = sizeof(major);
    auto status = RegGetValueW(HKEY_LOCAL_MACHINE, key, L"CurrentMajorVersionNumber", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &major, &bytes);
    if (status == ERROR_FILE_NOT_FOUND) { supported = false; return S_OK; }
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    wchar_t build[32] = {}; bytes = sizeof(build);
    status = RegGetValueW(HKEY_LOCAL_MACHINE, key, L"CurrentBuildNumber", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, build, &bytes);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    wchar_t* end = nullptr;
    const auto number = wcstoul(build, &end, 10);
    if (!end || end == build || *end) return E_INVALIDARG;
    supported = major >= 10 && number >= 17763;
    return S_OK;
}

inline HRESULT NativeMachine(USHORT& machine)
{
    USHORT process = 0;
    return IsWow64Process2(GetCurrentProcess(), &process, &machine) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}
