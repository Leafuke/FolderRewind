#include <windows.h>
#include <msiquery.h>
#include <sddl.h>
#include <objbase.h>
#include <string>
#include <vector>
#include <sstream>
#include <algorithm>
#include <set>
#include "RegistryValue.h"
#include "InstallerDiagnostics.h"

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
    LSTATUS error = ERROR_INVALID_SID;
    explicit Hive(const std::wstring& sid)
    {
        if (sid == L"-") { error = RegOpenCurrentUser(KEY_READ | KEY_WRITE, &key); return; }
        PSID parsed = nullptr;
        if (!ConvertStringSidToSidW(sid.c_str(), &parsed)) return;
        LocalFree(parsed);
        if (sid.find(L"S-1-5-21-") != 0 && sid.find(L"S-1-12-1-") != 0) return;
        error = RegOpenKeyExW(HKEY_USERS, sid.c_str(), 0, KEY_READ | KEY_WRITE, &key);
        if (error == ERROR_SUCCESS) return;
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) return;
        wchar_t profile[32768] = {}; DWORD bytes = sizeof(profile);
        const auto path = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\" + sid;
        error = RegGetValueW(HKEY_LOCAL_MACHINE, path.c_str(), L"ProfileImagePath", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, profile, &bytes);
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) { absent = true; return; }
        if (error != ERROR_SUCCESS) return;
        const auto file = std::wstring(profile) + L"\\NTUSER.DAT";
        if (GetFileAttributesW(file.c_str()) == INVALID_FILE_ATTRIBUTES && (GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND)) { absent = true; return; }
        GUID guid; if (FAILED(CoCreateGuid(&guid))) return;
        wchar_t id[40] = {}; StringFromGUID2(guid, id, 40);
        mounted = L"FolderRewind-Uninstall-" + std::wstring(id);
        error = RegLoadKeyW(HKEY_USERS, mounted.c_str(), file.c_str());
        if (error != ERROR_SUCCESS) { mounted.clear(); return; }
        error = RegOpenKeyExW(HKEY_USERS, mounted.c_str(), 0, KEY_READ | KEY_WRITE, &key);
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
    MsiSetPropertyW(session, L"FolderRewindCleanupAuthorized", L"");
    // An advertised product has no trustworthy installation location. Let MSI
    // remove its own registration, but never infer ownership of external values.
    if (MsiQueryProductStateW(GetProperty(session, L"ProductCode").c_str()) == INSTALLSTATE_ADVERTISED) {
        InstallerLog(session, L"FolderRewind advertised registration: external startup cleanup not authorized.");
        return ERROR_SUCCESS;
    }
    GUID guid; if (FAILED(CoCreateGuid(&guid))) return ERROR_INSTALL_FAILURE;
    wchar_t id[40] = {}; StringFromGUID2(guid, id, 40);
    const auto scope = GetProperty(session, L"ALLUSERS") == L"1" ? L"M|" : L"U|";
    CleanupData verified;
    verified.machine = scope[0] == L'M';
    verified.directory = GetProperty(session, L"INSTALLFOLDER");
    verified.product = GetProperty(session, L"ProductCode");
    verified.sid = GetProperty(session, L"UserSID");
    LogInstallerContext(session, L"prepare-startup", verified.sid, verified.machine);
    if (!ValidateCleanupContext(verified)) return ERROR_INSTALL_FAILURE;
    const auto data = scope + verified.directory + L"|" + id + L"|" + verified.product + L"|" + verified.sid + L"|" + verified.name;
    for (const auto action : { L"CleanupStartupUser", L"RollbackStartupUser", L"CommitStartupUser", L"CleanupStartupMachine", L"RollbackStartupMachine", L"CommitStartupMachine" })
        if (MsiSetPropertyW(session, action, data.c_str()) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    return MsiSetPropertyW(session, L"FolderRewindCleanupAuthorized", L"1");
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
struct Entry { std::wstring sid; Registry::Value command, approval; };
static bool Journal(const std::wstring& path, const std::wstring& text, bool create = false)
{
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr)) return false;
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    HANDLE file = CreateFileW(path.c_str(), create ? GENERIC_WRITE : FILE_APPEND_DATA, 0, &security,
        create ? CREATE_NEW : OPEN_EXISTING, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    LocalFree(descriptor);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0, size = static_cast<DWORD>(text.size() * sizeof(wchar_t));
    const bool saved = WriteFile(file, text.data(), size, &written, nullptr) && written == size && FlushFileBuffers(file);
    CloseHandle(file);
    return saved;
}
static Registry::Value ReadStartup(MSIHANDLE session, HKEY root, const wchar_t* path, const std::wstring& sid)
{
    auto value = Registry::Read(root, path, L"FolderRewind");
    InstallerLog(session, L"FolderRewind registry-read: hive=HKU\\" + sid + L", path=" + path + L", view=64, status=" +
        std::to_wstring(value.error) + L", type=" + std::to_wstring(value.type) + L", bytes=" + std::to_wstring(value.bytes.size()));
    return value;
}
#include "ShortcutCleanup.h"
extern "C" UINT __stdcall CleanupStartup(MSIHANDLE session)
{
    CleanupData data; if (!Data(session, data)) return ERROR_INSTALL_FAILURE;
    LogInstallerContext(session, L"cleanup-startup", data.sid, data.machine);
    Privileges privileges(data.machine); if (!privileges.enabled) return ERROR_INSTALL_FAILURE;
    std::vector<Entry> entries;
    const auto users = Users(data);
    if (data.machine && users.empty()) return ERROR_INSTALL_FAILURE;
    for (const auto& sid : users) {
        Hive hive(sid);
        if (!hive.key) { InstallerLog(session, L"FolderRewind hive-open failed: " + sid + L", status=" + std::to_wstring(hive.error)); if (hive.absent) continue; return ERROR_INSTALL_FAILURE; }
        const auto command = ReadStartup(session, hive.key, RunKey, sid);
        if (command.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
        if (!Owned(Registry::Text(command), data.directory)) continue;
        const auto approval = ReadStartup(session, hive.key, ApprovedKey, sid);
        if (approval.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
        entries.push_back({sid, command, approval});
    }
    std::wstring snapshot = L"FolderRewind Startup Cleanup v2\n";
    for (const auto& entry : entries) {
        snapshot += L"S\t" + entry.sid + L"\tR\t" + std::to_wstring(entry.command.type) + L"\t" + Registry::Hex(entry.command.bytes) + L"\n";
        if (entry.approval.state == Registry::State::Present)
            snapshot += L"S\t" + entry.sid + L"\tA\t" + std::to_wstring(entry.approval.type) + L"\t" + Registry::Hex(entry.approval.bytes) + L"\n";
    }
    if (!Journal(data.journal, snapshot, true)) return ERROR_INSTALL_FAILURE;
    std::vector<ShortcutSnapshot> shortcuts;
    if (!SnapshotShortcuts(data, users, shortcuts) || !SaveShortcutMetadata(data, shortcuts)) return ERROR_INSTALL_FAILURE;
    for (const auto& item : shortcuts) {
        if (item.owned && IsInstallationShortcut(item.path, data)) {
            const auto attributes = GetFileAttributesW(item.path.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_READONLY)) SetFileAttributesW(item.path.c_str(), attributes & ~FILE_ATTRIBUTE_READONLY);
            if (!DeleteFileW(item.path.c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) return ERROR_INSTALL_FAILURE;
        }
    }
    for (const auto& entry : entries) {
        Hive hive(entry.sid); if (!hive.key) return ERROR_INSTALL_FAILURE;
        auto current = ReadStartup(session, hive.key, RunKey, entry.sid);
        if (current.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
        if (!Registry::Equal(current, entry.command)) continue;
        for (int index = 0; index < 2; ++index) {
            const auto path = index == 0 ? RunKey : ApprovedKey;
            const auto& original = index == 0 ? entry.command : entry.approval;
            const auto marker = index == 0 ? L"R" : L"A";
            if (original.state != Registry::State::Present) continue;
            if (index == 1) {
                const auto run = ReadStartup(session, hive.key, RunKey, entry.sid);
                if (run.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
                if (run.state == Registry::State::Present) break; // A concurrent writer owns the new entry.
            }
            current = ReadStartup(session, hive.key, path, entry.sid);
            if (current.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
            if (!Registry::Equal(current, original)) continue;
            const auto status = Registry::Delete(hive.key, path, L"FolderRewind");
            InstallerLog(session, L"FolderRewind registry-delete: sid=" + entry.sid + L", value=" + marker + L", status=" + std::to_wstring(status));
            if (status == ERROR_FILE_NOT_FOUND) continue; // Did not delete this value.
            if (status != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
            // Record successful deletion durably before any later action may fail.
            if (!Journal(data.journal, L"D\t" + entry.sid + L"\t" + marker + L"\n")) {
                const auto remaining = Registry::Read(hive.key, path, L"FolderRewind");
                if (remaining.state == Registry::State::Missing) Registry::Write(hive.key, path, L"FolderRewind", original);
                return ERROR_INSTALL_FAILURE;
            }
            const auto remaining = ReadStartup(session, hive.key, path, entry.sid);
            if (remaining.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
            if (remaining.state == Registry::State::Missing)
                InstallerLog(session, L"FolderRewind startup-delete-verified: " + entry.sid + L"/" + marker);
            else InstallerLog(session, L"FolderRewind startup-concurrent-write-preserved: " + entry.sid + L"/" + marker);
        }
    }
    return ERROR_SUCCESS;
}
extern "C" UINT __stdcall RollbackStartup(MSIHANDLE session)
{
    CleanupData data; if (!Data(session, data)) return ERROR_INSTALL_FAILURE;
    LogInstallerContext(session, L"rollback-startup", data.sid, data.machine);
    Privileges privileges(data.machine); if (!privileges.enabled) return ERROR_INSTALL_FAILURE;
    HANDLE file = CreateFileW(data.journal.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    const auto size = GetFileSize(file, nullptr);
    if (size > 4 * 1024 * 1024 || size % sizeof(wchar_t)) { CloseHandle(file); return ERROR_INSTALL_FAILURE; }
    std::wstring snapshot(size / sizeof(wchar_t), L'\0'); DWORD read = 0;
    const bool loaded = ReadFile(file, snapshot.data(), size, &read, nullptr) && size == read; CloseHandle(file);
    if (!loaded) return ERROR_INSTALL_FAILURE;
    struct Saved { std::wstring sid, marker; Registry::Value value; };
    std::vector<Saved> saved;
    std::set<std::wstring> deleted;
    std::wistringstream stream(snapshot); std::wstring line;
    if (!std::getline(stream, line) || line != L"FolderRewind Startup Cleanup v2") return ERROR_INSTALL_FAILURE;
    while (std::getline(stream, line)) {
        std::wistringstream fields(line); std::vector<std::wstring> parts; std::wstring part;
        while (std::getline(fields, part, L'\t')) parts.push_back(part);
        if (parts.size() < 3 || (!data.machine && parts[1] != data.sid) || (parts[2] != L"R" && parts[2] != L"A")) return ERROR_INSTALL_FAILURE;
        if (parts[0] == L"D" && parts.size() == 3) deleted.insert(parts[1] + L"/" + parts[2]);
        else if (parts[0] == L"S" && parts.size() == 5) {
            if (parts[3].empty() || parts[3].find_first_not_of(L"0123456789") != std::wstring::npos) return ERROR_INSTALL_FAILURE;
            Registry::Value value; value.state = Registry::State::Present; value.error = ERROR_SUCCESS;
            value.type = wcstoul(parts[3].c_str(), nullptr, 10);
            if (!Registry::Unhex(parts[4], value.bytes)) return ERROR_INSTALL_FAILURE;
            if (parts[2] == L"R" && !Owned(Registry::Text(value), data.directory)) return ERROR_INSTALL_FAILURE;
            saved.push_back({parts[1], parts[2], value});
        } else return ERROR_INSTALL_FAILURE;
    }
    bool restored = true;
    for (const auto& entry : saved) {
        if (!deleted.count(entry.sid + L"/" + entry.marker)) continue;
        Hive hive(entry.sid); if (!hive.key) { restored = false; continue; }
        const auto path = entry.marker == L"R" ? RunKey : ApprovedKey;
        const auto existing = ReadStartup(session, hive.key, path, entry.sid);
        if (existing.state == Registry::State::Failed) { restored = false; continue; }
        if (existing.state == Registry::State::Present) continue; // Never overwrite another writer.
        if (entry.marker == L"A") {
            const auto run = ReadStartup(session, hive.key, RunKey, entry.sid);
            if (run.state == Registry::State::Failed) { restored = false; continue; }
            if (!Owned(Registry::Text(run), data.directory)) continue;
        }
        const auto status = Registry::Write(hive.key, path, L"FolderRewind", entry.value);
        if (status != ERROR_SUCCESS || !Registry::Equal(ReadStartup(session, hive.key, path, entry.sid), entry.value)) { restored = false; continue; }
        InstallerLog(session, L"FolderRewind startup-restore-verified: " + entry.sid + L"/" + entry.marker);
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
