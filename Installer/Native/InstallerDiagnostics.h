#pragma once
#include <windows.h>
#include <msiquery.h>
#include <sddl.h>
#include <string>
#include <vector>
inline std::wstring RegistryHandleName(HKEY key) {
    using Query = LONG (NTAPI*)(HANDLE, int, void*, ULONG, ULONG*);
    const auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryKey"));
    if (!query) return L"unavailable";
    ULONG size = 0; query(key, 3, nullptr, 0, &size);
    if (size < sizeof(ULONG) || size > 65536) return L"unavailable";
    std::vector<BYTE> value(size);
    if (query(key, 3, value.data(), size, &size) < 0) return L"unavailable";
    const auto length = *reinterpret_cast<const ULONG*>(value.data());
    if (length > size - sizeof(ULONG) || length % sizeof(wchar_t)) return L"invalid";
    return std::wstring(reinterpret_cast<const wchar_t*>(value.data()+sizeof(ULONG)), length / sizeof(wchar_t));
}
inline void InstallerLog(MSIHANDLE session, const std::wstring& message) {
    MSIHANDLE record = MsiCreateRecord(1);
    MsiRecordSetStringW(record, 0, L"[1]"); MsiRecordSetStringW(record, 1, message.c_str());
    MsiProcessMessage(session, INSTALLMESSAGE_INFO, record); MsiCloseHandle(record);
}
inline std::wstring TokenDescription(HANDLE token) {
    DWORD size = 0;
    GetTokenInformation(token, TokenUser, nullptr, 0, &size);
    std::vector<BYTE> buffer(size);
    if (!size || !GetTokenInformation(token, TokenUser, buffer.data(), size, &size)) return L"query-error=" + std::to_wstring(GetLastError());
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid)) return L"invalid-sid";
    std::wstring result(sid); LocalFree(sid);
    TOKEN_ELEVATION elevation = {};
    if (GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size)) result += L", elevated=" + std::to_wstring(elevation.TokenIsElevated);
    return result;
}
inline void LogInstallerContext(MSIHANDLE session, const std::wstring& action, const std::wstring& sid, bool machine) {
    HANDLE process = nullptr, thread = nullptr;
    std::wstring text = L"FolderRewind " + action + L": pid=" + std::to_wstring(GetCurrentProcessId()) + L", targetSid=" + sid + L", scope=" + (machine ? L"machine" : L"user");
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &process)) { text += L", process=" + TokenDescription(process); CloseHandle(process); }
    if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &thread)) { text += L", thread=" + TokenDescription(thread); CloseHandle(thread); }
    else text += L", thread-token-error=" + std::to_wstring(GetLastError());
    InstallerLog(session, text);
    HKEY current = nullptr, user = nullptr;
    const auto currentStatus = RegOpenCurrentUser(KEY_READ, &current);
    const auto userStatus = RegOpenKeyExW(HKEY_USERS, sid.c_str(), 0, KEY_READ, &user);
    InstallerLog(session, L"FolderRewind hive-resolution: current-status=" + std::to_wstring(currentStatus) + L", current=" + (current ? RegistryHandleName(current) : L"-") +
        L", user-status=" + std::to_wstring(userStatus) + L", user=" + (user ? RegistryHandleName(user) : L"-"));
    if (current) RegCloseKey(current);
    if (user) RegCloseKey(user);
    for (const auto root : {HKEY_CURRENT_USER, HKEY_USERS}) {
        const auto prefix = root == HKEY_USERS ? sid + L"\\" : std::wstring();
        for (const auto view : {KEY_WOW64_32KEY, KEY_WOW64_64KEY}) {
            const auto path = prefix + L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
            HKEY key = nullptr;
            const auto status = RegOpenKeyExW(root, path.c_str(), 0, KEY_READ | view, &key);
            DWORD count = 0;
            if (key) RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, &count, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr);
            InstallerLog(session, L"FolderRewind uninstall-hive: root=" + std::wstring(root == HKEY_USERS ? L"HKU" : L"HKCU") + L", view=" + std::to_wstring(view) + L", status=" + std::to_wstring(status) + L", children=" + std::to_wstring(count) + L", resolved=" + (key ? RegistryHandleName(key) : L"-"));
            if (key) RegCloseKey(key);
        }
    }
}
