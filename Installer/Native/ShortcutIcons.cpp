#include <windows.h>
#include <msiquery.h>
#include <shobjidl.h>
#include <string>
#include <vector>
#include <sstream>
#include "InstallerDiagnostics.h"

static std::wstring Property(MSIHANDLE session,const wchar_t* name) {
    DWORD size = 0; MsiGetPropertyW(session,name,L"",&size);
    std::vector<wchar_t> value(static_cast<size_t>(size)+1); size = static_cast<DWORD>(value.size());
    return MsiGetPropertyW(session,name,value.data(),&size) == ERROR_SUCCESS ? value.data() : L"";
}
extern "C" UINT __stdcall PrepareShortcutIcons(MSIHANDLE session) {
    const auto name = Property(session,L"ProductName");
    const auto data = Property(session,L"INSTALLFOLDER") + L"FolderRewind.exe|" +
        Property(session,L"ProgramMenuFolder") + name + L"\\" + name + L".lnk|" + Property(session,L"DesktopFolder") + name + L".lnk";
    if (MsiSetPropertyW(session,L"FinalizeShortcutIconsUser",data.c_str()) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    return MsiSetPropertyW(session,L"FinalizeShortcutIconsMachine",data.c_str());
}
extern "C" UINT __stdcall FinalizeShortcutIcons(MSIHANDLE session) {
    std::wistringstream stream(Property(session,L"CustomActionData")); std::wstring executable,path;
    if (!std::getline(stream,executable,L'|') || executable.size() < 4 || executable[1] != L':' || executable[2] != L'\\') return ERROR_INSTALL_FAILURE;
    const HRESULT initialized = CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return ERROR_INSTALL_FAILURE;
    bool okay = true;
    while (std::getline(stream,path,L'|')) {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND)) continue;
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) { okay = false; break; }
        IShellLinkW* link = nullptr; IPersistFile* file = nullptr;
        auto status = CoCreateInstance(CLSID_ShellLink,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&link));
        if (SUCCEEDED(status)) status = link->QueryInterface(IID_PPV_ARGS(&file));
        if (SUCCEEDED(status)) status = file->Load(path.c_str(),STGM_READWRITE);
        wchar_t target[32768] = {};
        if (SUCCEEDED(status)) status = link->GetPath(target,ARRAYSIZE(target),nullptr,SLGP_RAWPATH);
        if (SUCCEEDED(status) && _wcsicmp(target,executable.c_str()) == 0) {
            status = link->SetIconLocation(executable.c_str(),0);
            if (SUCCEEDED(status)) status = file->Save(path.c_str(),TRUE);
            if (SUCCEEDED(status)) InstallerLog(session,L"FolderRewind shortcut icon uses installed executable: " + path);
        }
        if (file) file->Release(); if (link) link->Release();
        if (FAILED(status)) { okay = false; break; }
    }
    if (SUCCEEDED(initialized)) CoUninitialize();
    return okay ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
}
