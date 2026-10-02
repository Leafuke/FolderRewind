#pragma once
#include <shlobj.h>
#include <shobjidl.h>
#include <set>

struct ShortcutSnapshot { std::wstring path, backup; bool owned = false; };
static bool SnapshotShortcuts(const CleanupData& data, const std::vector<std::wstring>& users, std::vector<ShortcutSnapshot>& shortcuts)
{
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return false;
    std::set<std::wstring> paths;
    const std::vector<std::wstring> names = data.name.empty() || data.name == L"FolderRewind" ? std::vector<std::wstring>{L"FolderRewind"} : std::vector<std::wstring>{L"FolderRewind", data.name};
    const KNOWNFOLDERID* shared[] = { &FOLDERID_PublicDesktop, &FOLDERID_CommonPrograms };
    const KNOWNFOLDERID* personal[] = { &FOLDERID_Desktop, &FOLDERID_Programs };
    HANDLE token = nullptr;
    if (!data.machine) OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token);
    for (int index = 0; index < 2; ++index) {
        PWSTR folder = nullptr;
        if (SUCCEEDED(SHGetKnownFolderPath(data.machine ? *shared[index] : *personal[index], 0, token, &folder))) {
            for (const auto& name : names) paths.insert(std::wstring(folder) + (index == 0 ? L"\\" + name + L".lnk" : L"\\" + name + L"\\" + name + L".lnk"));
            CoTaskMemFree(folder);
        }
    }
    if (token) CloseHandle(token);
    if (data.machine) for (const auto& sid : users) {
        Hive hive(sid); if (!hive.key) continue;
        wchar_t profile[32768] = {}; DWORD bytes = sizeof(profile);
        const auto profileKey = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\" + sid;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, profileKey.c_str(), L"ProfileImagePath", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, profile, &bytes) != ERROR_SUCCESS) continue;
        for (int index = 0; index < 2; ++index) {
            wchar_t folder[32768] = {}; bytes = sizeof(folder);
            const auto status = RegGetValueW(hive.key, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders", index == 0 ? L"Desktop" : L"Programs",
                RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, nullptr, folder, &bytes);
            std::wstring path = status == ERROR_SUCCESS ? folder : std::wstring(profile) + (index == 0 ? L"\\Desktop" : L"\\AppData\\Roaming\\Microsoft\\Windows\\Start Menu\\Programs");
            const std::wstring marker = L"%USERPROFILE%";
            size_t position;
            while ((position = path.find(marker)) != std::wstring::npos) path.replace(position, marker.size(), profile);
            if (path.find(L'%') != std::wstring::npos) continue;
            for (const auto& name : names) paths.insert(path + (index == 0 ? L"\\" + name + L".lnk" : L"\\" + name + L"\\" + name + L".lnk"));
        }
    }
    bool okay = true;
    for (const auto& path : paths) {
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) {
            if (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND) continue;
            okay = false; break;
        }
        IShellLinkW* link = nullptr; IPersistFile* file = nullptr;
        if (FAILED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link)))) { okay = false; break; }
        wchar_t target[32768] = {};
        const HRESULT result = link->QueryInterface(IID_PPV_ARGS(&file));
        if (FAILED(result) || FAILED(file->Load(path.c_str(), STGM_READ)) || FAILED(link->GetPath(target, ARRAYSIZE(target), nullptr, SLGP_RAWPATH))) {
            if (file) file->Release(); link->Release(); okay = false; break;
        }
        file->Release(); link->Release();
        const auto backup = data.journal + L".shortcut-" + std::to_wstring(shortcuts.size());
        if (!CopyFileW(path.c_str(), backup.c_str(), TRUE)) { okay = false; break; }
        shortcuts.push_back({path, backup, Owned(L"\"" + std::wstring(target) + L"\" --startup", data.directory)});
    }
    if (SUCCEEDED(initialized)) CoUninitialize();
    return okay;
}
static bool SaveShortcutMetadata(const CleanupData& data, const std::vector<ShortcutSnapshot>& shortcuts)
{
    std::wstring text = L"FolderRewind Shortcut Cleanup v1\n";
    for (const auto& item : shortcuts) text += (item.owned ? L"O\t" : L"F\t") + item.path + L"\n";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr)) return false;
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    HANDLE file = CreateFileW((data.journal + L".shortcuts").c_str(), GENERIC_WRITE, 0, &security, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    LocalFree(descriptor); if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0, size = static_cast<DWORD>(text.size() * sizeof(wchar_t));
    const bool saved = WriteFile(file, text.data(), size, &written, nullptr) && written == size && FlushFileBuffers(file);
    CloseHandle(file); return saved;
}
static bool IsInstallationShortcut(const std::wstring& path, const CleanupData& data)
{
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized) && initialized != RPC_E_CHANGED_MODE) return false;
    IShellLinkW* link = nullptr; IPersistFile* file = nullptr;
    wchar_t target[32768] = {};
    bool owned = false;
    if (SUCCEEDED(CoCreateInstance(CLSID_ShellLink, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&link))) &&
        SUCCEEDED(link->QueryInterface(IID_PPV_ARGS(&file))) && SUCCEEDED(file->Load(path.c_str(), STGM_READ)) &&
        SUCCEEDED(link->GetPath(target, ARRAYSIZE(target), nullptr, SLGP_RAWPATH)))
        owned = Owned(L"\"" + std::wstring(target) + L"\" --startup", data.directory);
    if (file) file->Release();
    if (link) link->Release();
    if (SUCCEEDED(initialized)) CoUninitialize();
    return owned;
}
static bool FinishShortcuts(const CleanupData& data, bool rollback)
{
    const auto metadata = data.journal + L".shortcuts";
    HANDLE file = CreateFileW(metadata.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND;
    const auto size = GetFileSize(file, nullptr);
    if (size > 1024 * 1024 || size % sizeof(wchar_t)) { CloseHandle(file); return false; }
    std::wstring text(size / sizeof(wchar_t), L'\0'); DWORD read = 0;
    const bool loaded = ReadFile(file, text.data(), size, &read, nullptr) && read == size; CloseHandle(file);
    if (!loaded) return false;
    std::wistringstream stream(text); std::wstring line;
    if (!std::getline(stream, line) || line != L"FolderRewind Shortcut Cleanup v1") return false;
    bool okay = true; size_t index = 0;
    while (std::getline(stream, line)) {
        const auto backup = data.journal + L".shortcut-" + std::to_wstring(index++);
        if (line.size() < 4 || line[1] != L'\t' || (line[0] != L'O' && line[0] != L'F')) { okay = false; continue; }
        const auto path = line.substr(2);
        const bool missing = GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES;
        // MSI rollback may recreate its authored shortcut after we took the
        // snapshot. Restore the original (including a foreign replacement)
        // over that owned link, but preserve a concurrent unrelated new link.
        if ((rollback || line[0] == L'F') && (missing || IsInstallationShortcut(path, data))) {
            auto directory = path.substr(0, path.find_last_of(L'\\'));
            SHCreateDirectoryExW(nullptr, directory.c_str(), nullptr);
            if (!CopyFileW(backup.c_str(), path.c_str(), missing ? TRUE : FALSE)) { okay = false; continue; }
        }
        const auto attributes = GetFileAttributesW(backup.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_READONLY)) SetFileAttributesW(backup.c_str(), attributes & ~FILE_ATTRIBUTE_READONLY);
        if (!DeleteFileW(backup.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) okay = false;
    }
    if (okay) DeleteFileW(metadata.c_str());
    return okay;
}
