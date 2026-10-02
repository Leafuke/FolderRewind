#include <windows.h>
#include <msiquery.h>
#include <sddl.h>
#include <objbase.h>
#include <string>
#include <vector>
#include <sstream>
#include <algorithm>

static const wchar_t* RunKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
static const wchar_t* ApprovedKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
static std::wstring GetProperty(MSIHANDLE session, const wchar_t* name)
{
    DWORD size = 0; MsiGetPropertyW(session, name, L"", &size);
    std::vector<wchar_t> value(static_cast<size_t>(size) + 1);
    size = static_cast<DWORD>(value.size());
    return MsiGetPropertyW(session, name, value.data(), &size) == ERROR_SUCCESS ? value.data() : L"";
}
static bool Owned(const std::wstring& command, std::wstring directory)
{
    while (!directory.empty() && directory.back() == L'\\') directory.pop_back();
    const auto expected = L"\"" + directory + L"\\FolderRewind.exe\" --startup";
    return _wcsicmp(command.c_str(), expected.c_str()) == 0;
}
struct Hive
{
    HKEY key = nullptr;
    std::wstring mounted;
    bool absent = false;
    explicit Hive(const std::wstring& sid)
    {
        if (sid == L"-") { RegOpenCurrentUser(KEY_READ | KEY_WRITE, &key); return; }
        PSID parsed = nullptr;
        if (!ConvertStringSidToSidW(sid.c_str(), &parsed)) return;
        LocalFree(parsed);
        if (sid.find(L"S-1-5-21-") != 0 && sid.find(L"S-1-12-1-") != 0) return;
        if (RegOpenKeyExW(HKEY_USERS, sid.c_str(), 0, KEY_READ | KEY_WRITE, &key) == ERROR_SUCCESS) return;
        wchar_t profile[32768] = {}; DWORD bytes = sizeof(profile);
        const auto path = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\" + sid;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, path.c_str(), L"ProfileImagePath", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, profile, &bytes) != ERROR_SUCCESS) return;
        const auto file = std::wstring(profile) + L"\\NTUSER.DAT";
        if (GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND)) { absent = true; return; }
        GUID guid; if (FAILED(CoCreateGuid(&guid))) return;
        wchar_t id[40] = {}; StringFromGUID2(guid, id, 40);
        mounted = L"FolderRewind-Uninstall-" + std::wstring(id);
        if (RegLoadKeyW(HKEY_USERS, mounted.c_str(), file.c_str()) != ERROR_SUCCESS) { mounted.clear(); return; }
        RegOpenKeyExW(HKEY_USERS, mounted.c_str(), 0, KEY_READ | KEY_WRITE, &key);
    }
    ~Hive() { if (key) RegCloseKey(key); if (!mounted.empty()) RegUnLoadKeyW(HKEY_USERS, mounted.c_str()); }
    Hive(const Hive&) = delete;
};
struct Privileges
{
    HANDLE token = nullptr;
    TOKEN_PRIVILEGES previous[2] = {};
    bool enabled = false;
    explicit Privileges(bool machine)
    {
        if (!machine) { enabled = true; return; }
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token)) return;
        const wchar_t* names[] = { SE_BACKUP_NAME, SE_RESTORE_NAME };
        enabled = true;
        for (int i = 0; i < 2; ++i) {
            TOKEN_PRIVILEGES request = {}; request.PrivilegeCount = 1;
            LookupPrivilegeValueW(nullptr, names[i], &request.Privileges[0].Luid);
            request.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
            DWORD size = sizeof(TOKEN_PRIVILEGES); SetLastError(ERROR_SUCCESS);
            if (!AdjustTokenPrivileges(token, FALSE, &request, sizeof(TOKEN_PRIVILEGES), &previous[i], &size) || GetLastError() != ERROR_SUCCESS) enabled = false;
        }
    }
    ~Privileges() { if (token) { for (int i = 0; i < 2; ++i) AdjustTokenPrivileges(token, FALSE, &previous[i], 0, nullptr, nullptr); CloseHandle(token); } }
};
static std::wstring Command(HKEY root, LSTATUS* queryStatus = nullptr)
{
    wchar_t command[32768] = {}; DWORD bytes = sizeof(command);
    const auto status = RegGetValueW(root, RunKey, L"FolderRewind", RRF_RT_REG_SZ | RRF_NOEXPAND | RRF_SUBKEY_WOW6464KEY, nullptr, command, &bytes);
    if (queryStatus) *queryStatus = status;
    return status == ERROR_SUCCESS ? command : L"";
}
static std::wstring Approval(HKEY root)
{
    BYTE bytes[1024] = {}; DWORD count = sizeof(bytes);
    if (RegGetValueW(root, ApprovedKey, L"FolderRewind", RRF_RT_REG_BINARY | RRF_SUBKEY_WOW6464KEY, nullptr, bytes, &count) != ERROR_SUCCESS) return L"-";
    const wchar_t* digits = L"0123456789ABCDEF"; std::wstring hex;
    for (DWORD i = 0; i < count; ++i) { hex += digits[bytes[i] >> 4]; hex += digits[bytes[i] & 15]; }
    return hex;
}
struct CleanupData { bool machine = false; std::wstring directory, journal, product, sid, name; };
static bool Data(MSIHANDLE session, CleanupData& data)
{
    const auto raw = GetProperty(session, L"CustomActionData");
    const auto first = raw.find(L'|'), second = raw.find(L'|', first == std::wstring::npos ? 0 : first + 1);
    if (first != 1 || second == std::wstring::npos) return false;
    data.machine = raw[0] == L'M';
    if (!data.machine && raw[0] != L'U') return false;
    data.directory = raw.substr(2, second - 2);
    const auto third = raw.find(L'|', second + 1), fourth = raw.find(L'|', third == std::wstring::npos ? 0 : third + 1);
    if (third == std::wstring::npos || fourth == std::wstring::npos) return false;
    const auto guidText = raw.substr(second + 1, third - second - 1); GUID guid = {};
    data.product = raw.substr(third + 1, fourth - third - 1);
    const auto fifth = raw.find(L'|', fourth + 1);
    if (fifth == std::wstring::npos) return false;
    data.sid = raw.substr(fourth + 1, fifth - fourth - 1);
    data.name = raw.substr(fifth + 1);
    GUID product = {}; PSID sid = nullptr;
    if (data.product.size() != 38 || FAILED(CLSIDFromString(data.product.c_str(), &product)) || !ConvertStringSidToSidW(data.sid.c_str(), &sid)) return false;
    LocalFree(sid);
    if (guidText.size() != 38 || FAILED(CLSIDFromString(guidText.c_str(), &guid))) return false;
    if (data.directory.size() < 4 || data.directory[1] != L':' || data.directory.find(L'\n') != std::wstring::npos || data.directory.find(L'\t') != std::wstring::npos) return false;
    wchar_t base[MAX_PATH] = {};
    if (data.machine) { if (!GetWindowsDirectoryW(base, MAX_PATH)) return false; data.journal = std::wstring(base) + L"\\Temp\\"; }
    else { if (!GetTempPathW(MAX_PATH, base)) return false; data.journal = base; }
    data.journal += L"FolderRewind-Uninstall-" + guidText + L".dat";
    return true;
}
static bool ValidateCleanupContext(CleanupData& data)
{
    // ALLUSERS and directory properties alone cannot authorize elevated
    // cleanup. Require a matching, registered MSI context and install location.
    DWORD count = 0;
    const auto context = data.machine ? MSIINSTALLCONTEXT_MACHINE : MSIINSTALLCONTEXT_USERUNMANAGED;
    const auto user = data.machine ? nullptr : data.sid.c_str();
    auto status = MsiGetProductInfoExW(data.product.c_str(), user, context, INSTALLPROPERTY_INSTALLLOCATION, L"", &count);
    if (status != ERROR_SUCCESS && status != ERROR_MORE_DATA) return false;
    std::vector<wchar_t> location(static_cast<size_t>(count) + 1);
    count = static_cast<DWORD>(location.size());
    if (MsiGetProductInfoExW(data.product.c_str(), user, context, INSTALLPROPERTY_INSTALLLOCATION, location.data(), &count) != ERROR_SUCCESS) return false;
    std::wstring registered(location.data()), requested(data.directory);
    while (!registered.empty() && registered.back() == L'\\') registered.pop_back();
    while (!requested.empty() && requested.back() == L'\\') requested.pop_back();
    if (registered.empty() || _wcsicmp(registered.c_str(), requested.c_str()) != 0) return false;
    count = 0;
    MsiGetProductInfoExW(data.product.c_str(), user, context, INSTALLPROPERTY_INSTALLEDPRODUCTNAME, L"", &count);
    std::vector<wchar_t> name(static_cast<size_t>(count) + 1);
    count = static_cast<DWORD>(name.size());
    if (MsiGetProductInfoExW(data.product.c_str(), user, context, INSTALLPROPERTY_INSTALLEDPRODUCTNAME, name.data(), &count) != ERROR_SUCCESS) return false;
    data.name = name.data();
    return true;
}
extern "C" UINT __stdcall PrepareStartupCleanup(MSIHANDLE session)
{
    GUID guid; if (FAILED(CoCreateGuid(&guid))) return ERROR_INSTALL_FAILURE;
    wchar_t id[40] = {}; StringFromGUID2(guid, id, 40);
    const auto scope = GetProperty(session, L"ALLUSERS") == L"1" ? L"M|" : L"U|";
    CleanupData verified;
    verified.machine = scope[0] == L'M';
    verified.directory = GetProperty(session, L"INSTALLFOLDER");
    verified.product = GetProperty(session, L"ProductCode");
    verified.sid = GetProperty(session, L"UserSID");
    if (!ValidateCleanupContext(verified)) return ERROR_INSTALL_FAILURE;
    const auto data = scope + verified.directory + L"|" + id + L"|" + verified.product + L"|" + verified.sid + L"|" + verified.name;
    for (const auto action : { L"CleanupStartupUser", L"RollbackStartupUser", L"CommitStartupUser", L"CleanupStartupMachine", L"RollbackStartupMachine", L"CommitStartupMachine" })
        if (MsiSetPropertyW(session, action, data.c_str()) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    return ERROR_SUCCESS;
}
static std::vector<std::wstring> Users(const CleanupData& data)
{
    // MSI's service may cache HKCU for its own account. Address the verified
    // installing user's loaded hive by SID instead of relying on HKCU.
    if (!data.machine) return { data.sid };
    std::vector<std::wstring> result; HKEY profiles = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList", 0, KEY_READ | KEY_WOW64_64KEY, &profiles) != ERROR_SUCCESS) return result;
    for (DWORD index = 0;; ++index) {
        wchar_t name[256] = {}; DWORD size = 256;
        auto status = RegEnumKeyExW(profiles, index, name, &size, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break;
        if (status != ERROR_SUCCESS) { result.clear(); break; }
        std::wstring sid(name);
        PSID parsed = nullptr;
        if ((sid.find(L"S-1-5-21-") == 0 || sid.find(L"S-1-12-1-") == 0) && ConvertStringSidToSidW(sid.c_str(), &parsed)) { result.push_back(sid); LocalFree(parsed); }
    }
    RegCloseKey(profiles); return result;
}
struct Entry { std::wstring sid, command, approval; };
static bool DeleteValue(HKEY root, const wchar_t* path)
{
    HKEY key = nullptr;
    auto status = RegOpenKeyExW(root, path, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &key);
    if (status == ERROR_FILE_NOT_FOUND) return true;
    if (status != ERROR_SUCCESS) return false;
    status = RegDeleteValueW(key, L"FolderRewind"); RegCloseKey(key);
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND;
}
#include "ShortcutCleanup.h"
extern "C" UINT __stdcall CleanupStartup(MSIHANDLE session)
{
    CleanupData data; if (!Data(session, data)) return ERROR_INSTALL_FAILURE;
    Privileges privileges(data.machine); if (!privileges.enabled) return ERROR_INSTALL_FAILURE;
    std::vector<Entry> entries;
    const auto users = Users(data);
    if (data.machine && users.empty()) return ERROR_INSTALL_FAILURE;
    for (const auto& sid : users) {
        Hive hive(sid); if (!hive.key) { if (hive.absent) continue; return ERROR_INSTALL_FAILURE; }
        LSTATUS status = ERROR_SUCCESS;
        const auto command = Command(hive.key, &status);
        const auto message = L"FolderRewind startup cleanup: query=" + std::to_wstring(status) + L", commandLength=" + std::to_wstring(command.size()) +
            L", directoryLength=" + std::to_wstring(data.directory.size()) + L", owned=" + (Owned(command, data.directory) ? L"yes" : L"no");
        MSIHANDLE record = MsiCreateRecord(1);
        MsiRecordSetStringW(record, 0, L"[1]"); MsiRecordSetStringW(record, 1, message.c_str());
        MsiProcessMessage(session, INSTALLMESSAGE_INFO, record); MsiCloseHandle(record);
        if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND && status != ERROR_PATH_NOT_FOUND) return ERROR_INSTALL_FAILURE;
        if (Owned(command, data.directory)) entries.push_back({sid, command, Approval(hive.key)});
    }
    std::wstring snapshot = L"FolderRewind Startup Cleanup v1\n";
    for (const auto& entry : entries) snapshot += entry.sid + L"\t" + entry.command + L"\t" + entry.approval + L"\n";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr)) return ERROR_INSTALL_FAILURE;
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    HANDLE file = CreateFileW(data.journal.c_str(), GENERIC_WRITE, 0, &security, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    LocalFree(descriptor); if (file == INVALID_HANDLE_VALUE) return ERROR_INSTALL_FAILURE;
    DWORD written = 0, size = static_cast<DWORD>(snapshot.size() * sizeof(wchar_t));
    bool saved = WriteFile(file, snapshot.data(), size, &written, nullptr) && written == size && FlushFileBuffers(file);
    CloseHandle(file); if (!saved) return ERROR_INSTALL_FAILURE;
    std::vector<ShortcutSnapshot> shortcuts;
    if (!SnapshotShortcuts(data, users, shortcuts)) {
        for (const auto& item : shortcuts) DeleteFileW(item.backup.c_str());
        return ERROR_INSTALL_FAILURE;
    }
    if (!SaveShortcutMetadata(data, shortcuts)) {
        for (const auto& item : shortcuts) DeleteFileW(item.backup.c_str());
        return ERROR_INSTALL_FAILURE;
    }
    for (const auto& item : shortcuts) {
        if (item.owned) {
            const auto attributes = GetFileAttributesW(item.path.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_READONLY)) SetFileAttributesW(item.path.c_str(), attributes & ~FILE_ATTRIBUTE_READONLY);
            if (!DeleteFileW(item.path.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) return ERROR_INSTALL_FAILURE;
        }
    }
    for (const auto& entry : entries) {
        Hive hive(entry.sid); if (!hive.key) return ERROR_INSTALL_FAILURE;
        if (Command(hive.key) != entry.command) continue;
        if (!DeleteValue(hive.key, RunKey)) return ERROR_INSTALL_FAILURE;
        if (Approval(hive.key) == entry.approval && entry.approval != L"-" && !DeleteValue(hive.key, ApprovedKey)) return ERROR_INSTALL_FAILURE;
    }
    return ERROR_SUCCESS;
}
extern "C" UINT __stdcall RollbackStartup(MSIHANDLE session)
{
    CleanupData data; if (!Data(session, data)) return ERROR_INSTALL_FAILURE;
    Privileges privileges(data.machine); if (!privileges.enabled) return ERROR_INSTALL_FAILURE;
    HANDLE file = CreateFileW(data.journal.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    const auto size = GetFileSize(file, nullptr);
    if (size > 1024 * 1024 || size % sizeof(wchar_t)) { CloseHandle(file); return ERROR_INSTALL_FAILURE; }
    std::wstring snapshot(size / sizeof(wchar_t), L'\0'); DWORD read = 0;
    const bool loaded = ReadFile(file, snapshot.data(), size, &read, nullptr) && size == read; CloseHandle(file);
    if (!loaded) return ERROR_INSTALL_FAILURE;
    std::wistringstream stream(snapshot); std::wstring line;
    if (!std::getline(stream, line) || line != L"FolderRewind Startup Cleanup v1") return ERROR_INSTALL_FAILURE;
    bool restored = true;
    while (std::getline(stream, line)) {
        const auto first = line.find(L'\t'), second = line.find(L'\t', first == std::wstring::npos ? 0 : first + 1);
        if (first == std::wstring::npos || second == std::wstring::npos) { restored = false; continue; }
        const auto sid = line.substr(0, first), command = line.substr(first + 1, second - first - 1), approval = line.substr(second + 1);
        if (!Owned(command, data.directory) || (!data.machine && sid != data.sid && sid != L"-")) { restored = false; continue; }
        Hive hive(sid); if (!hive.key) { restored = false; continue; }
        const auto existing = Command(hive.key);
        if (!existing.empty()) continue; // Preserve any entry created after cleanup.
        HKEY key = nullptr;
        if (RegCreateKeyExW(hive.key, RunKey, 0, nullptr, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &key, nullptr) != ERROR_SUCCESS) { restored = false; continue; }
        restored &= RegSetValueExW(key, L"FolderRewind", 0, REG_SZ, reinterpret_cast<const BYTE*>(command.c_str()), static_cast<DWORD>((command.size()+1)*sizeof(wchar_t))) == ERROR_SUCCESS;
        RegCloseKey(key);
        if (approval != L"-" && approval.size() <= 2048 && approval.size() % 2 == 0 && Approval(hive.key) == L"-") {
            std::vector<BYTE> bytes;
            for (size_t index = 0; index < approval.size(); index += 2) {
                if (approval.find_first_not_of(L"0123456789ABCDEF") != std::wstring::npos) { restored = false; break; }
                bytes.push_back(static_cast<BYTE>(wcstoul(approval.substr(index,2).c_str(), nullptr, 16)));
            }
            if (RegCreateKeyExW(hive.key, ApprovedKey, 0, nullptr, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &key, nullptr) == ERROR_SUCCESS) {
                restored &= RegSetValueExW(key, L"FolderRewind", 0, REG_BINARY, bytes.data(), static_cast<DWORD>(bytes.size())) == ERROR_SUCCESS;
                RegCloseKey(key);
            } else restored = false;
        }
    }
    restored &= FinishShortcuts(data, true);
    if (restored) DeleteFileW(data.journal.c_str());
    return restored ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
}
extern "C" UINT __stdcall CommitStartup(MSIHANDLE session)
{
    CleanupData data; if (!Data(session, data)) return ERROR_INSTALL_FAILURE;
    if (!FinishShortcuts(data, false)) return ERROR_INSTALL_FAILURE;
    return DeleteFileW(data.journal.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
}

// Referenced only by the isolated fault-injection MSI, never by production authoring.
extern "C" UINT __stdcall FailForTesting(MSIHANDLE) { return ERROR_INSTALL_FAILURE; }
