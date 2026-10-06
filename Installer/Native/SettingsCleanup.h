#pragma once
#include <windows.h>
#include <string>
#include <vector>

namespace SettingsCleanup {
inline bool IsSettingsFile(const std::wstring& name) {
    if (_wcsicmp(name.c_str(), L"config.json") == 0 || _wcsicmp(name.c_str(), L"config.json.bak") == 0) return true;
    const std::wstring prefix = L"config.json.recovery.", suffix = L".json";
    return name.size() > prefix.size()+suffix.size() && name.compare(0,prefix.size(),prefix) == 0 &&
        name.compare(name.size()-suffix.size(),suffix.size(),suffix) == 0;
}
struct Handles {
    std::vector<HANDLE> values;
    ~Handles() { for (HANDLE handle : values) CloseHandle(handle); }
};
inline std::wstring FinalPath(HANDLE handle) {
    const DWORD size = GetFinalPathNameByHandleW(handle,nullptr,0,FILE_NAME_NORMALIZED);
    if (!size) return L"";
    std::vector<wchar_t> buffer(static_cast<size_t>(size)+1);
    const DWORD written = GetFinalPathNameByHandleW(handle,buffer.data(),static_cast<DWORD>(buffer.size()),FILE_NAME_NORMALIZED);
    return written && written < buffer.size() ? buffer.data() : L"";
}
inline DWORD Clear(const std::wstring& directory, DWORD& removed) {
    removed = 0;
    // Only this exact known-folder child is supported. Never recursively delete
    // an app-data root: users can store backup repositories underneath it.
    if (directory.size() < 4 || directory[1] != L':' || directory[2] != L'\\' ||
        directory.substr(directory.find_last_of(L'\\')+1) != L"FolderRewind") return ERROR_INVALID_NAME;
    std::vector<wchar_t> full(32768);
    const auto length = GetFullPathNameW(directory.c_str(),static_cast<DWORD>(full.size()),full.data(),nullptr);
    if (!length || length >= full.size() || _wcsicmp(full.data(),directory.c_str()) != 0) return ERROR_INVALID_NAME;
    Handles locked;
    // Hold every directory without FILE_SHARE_DELETE to prevent a parent swap
    // between path validation and deleting a selected file by its own handle.
    for (size_t end = 3;;) {
        const auto path = directory.substr(0,end);
        HANDLE handle = CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ | FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,nullptr);
        if (handle == INVALID_HANDLE_VALUE) { const auto error = GetLastError(); return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND ? ERROR_SUCCESS : error; }
        locked.values.push_back(handle);
        BY_HANDLE_FILE_INFORMATION info = {};
        if (!GetFileInformationByHandle(handle,&info)) return GetLastError();
        if (!(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) return ERROR_ACCESS_DENIED;
        if (end == directory.size()) break;
        end = directory.find(L'\\',end == 3 ? 3 : end+1);
        if (end == std::wstring::npos) end = directory.size();
    }
    const auto root = FinalPath(locked.values.back());
    if (root.empty()) return ERROR_INVALID_NAME;
    WIN32_FIND_DATAW item = {};
    HANDLE search = FindFirstFileW((directory+L"\\*").c_str(),&item);
    if (search == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : GetLastError();
    DWORD result = ERROR_SUCCESS;
    do {
        const std::wstring name(item.cFileName);
        if (!IsSettingsFile(name)) continue;
        HANDLE file = CreateFileW((directory+L"\\"+name).c_str(),FILE_READ_ATTRIBUTES | DELETE,FILE_SHARE_READ,nullptr,OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,nullptr);
        if (file == INVALID_HANDLE_VALUE) { result = GetLastError(); break; }
        BY_HANDLE_FILE_INFORMATION info = {};
        if (!GetFileInformationByHandle(file,&info)) result = GetLastError();
        else if (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY) || info.nNumberOfLinks != 1 ||
            _wcsicmp(FinalPath(file).c_str(),(root+L"\\"+name).c_str()) != 0) result = ERROR_ACCESS_DENIED;
        else {
            FILE_DISPOSITION_INFO disposition = {TRUE};
            if (!SetFileInformationByHandle(file,FileDispositionInfo,&disposition,sizeof(disposition))) result = GetLastError();
            else ++removed;
        }
        CloseHandle(file);
        if (result != ERROR_SUCCESS) break;
    } while (FindNextFileW(search,&item));
    if (result == ERROR_SUCCESS && GetLastError() != ERROR_NO_MORE_FILES) result = GetLastError();
    FindClose(search);
    return result;
}
}
