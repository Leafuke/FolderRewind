#include "RegistryValue.h"
#include "InstallerDiagnostics.h"
#include <objbase.h>
#include <sstream>

static std::wstring Property(MSIHANDLE session, const wchar_t* name) {
    DWORD size = 0; MsiGetPropertyW(session, name, L"", &size);
    std::vector<wchar_t> buffer(static_cast<size_t>(size) + 1); size = static_cast<DWORD>(buffer.size());
    return MsiGetPropertyW(session, name, buffer.data(), &size) == ERROR_SUCCESS ? buffer.data() : L"";
}
static bool Guid(const std::wstring& text) {
    GUID guid = {}; return text.size() == 38 && SUCCEEDED(CLSIDFromString(text.c_str(), &guid));
}
struct Context {
    bool machine = false;
    std::wstring sid, code, family, directory, journal, name;
    HKEY root = nullptr;
    ~Context() { if (root && root != HKEY_LOCAL_MACHINE) RegCloseKey(root); }
};
static bool Decode(MSIHANDLE session, Context& context) {
    std::wistringstream input(Property(session, L"CustomActionData"));
    std::vector<std::wstring> fields; std::wstring field;
    while (std::getline(input, field, L'|')) fields.push_back(field);
    if (fields.size() != 7 || (fields[0] != L"M" && fields[0] != L"U") || !Guid(fields[2]) || !Guid(fields[3]) || !Guid(fields[5])) return false;
    context.machine = fields[0] == L"M"; context.sid = fields[1]; context.code = fields[2]; context.family = fields[3]; context.directory = fields[4]; context.name = fields[6];
    if (context.directory.size() < 4 || context.directory[1] != L':' || context.directory[2] != L'\\' || context.directory.find_first_of(L"\r\n\t") != std::wstring::npos) return false;
    if (context.machine) context.root = HKEY_LOCAL_MACHINE;
    else {
        PSID sid = nullptr; if (!ConvertStringSidToSidW(context.sid.c_str(), &sid)) return false; LocalFree(sid);
        if (RegOpenKeyExW(HKEY_USERS, context.sid.c_str(), 0, KEY_READ | KEY_WRITE, &context.root) != ERROR_SUCCESS) return false;
    }
    wchar_t directory[MAX_PATH] = {};
    if (context.machine) {
        if (!GetWindowsDirectoryW(directory, MAX_PATH)) return false;
        context.journal = std::wstring(directory) + L"\\Temp\\";
    } else {
        if (!GetTempPathW(MAX_PATH, directory)) return false;
        context.journal = directory;
    }
    context.journal += L"FolderRewind-Bundle-" + fields[5] + L".dat";
    LogInstallerContext(session, L"bundle-location", context.sid, context.machine);
    return true;
}
static std::wstring Key(const Context& context) { return L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + context.code; }
static bool Owned(const Context& context, REGSAM view, MSIHANDLE session) {
    const auto path = Key(context);
    const auto family = Registry::Read(context.root, path.c_str(), L"BundleUpgradeCode", view);
    InstallerLog(session, L"FolderRewind bundle-owner-read: code=" + context.code + L", root=" + RegistryHandleName(context.root) + L", view=" + std::to_wstring(view) + L", open-status=" + std::to_wstring(family.openError) + L", family-status=" + std::to_wstring(family.error) + L", type=" + std::to_wstring(family.type));
    if (family.state != Registry::State::Present || family.type != REG_MULTI_SZ || family.bytes.size() % sizeof(wchar_t)) return false;
    std::wstring codes(family.bytes.size() / sizeof(wchar_t), L'\0');
    memcpy(codes.data(), family.bytes.data(), family.bytes.size());
    bool matches = false;
    for (size_t begin = 0; begin < codes.size();) {
        const auto end = codes.find(L'\0', begin); if (end == std::wstring::npos) return false;
        if (_wcsicmp(codes.substr(begin, end-begin).c_str(), context.family.c_str()) == 0) matches = true;
        begin = end + 1;
    }
    const auto nameValue = Registry::Read(context.root, path.c_str(), L"DisplayName", view);
    const auto providerValue = Registry::Read(context.root, path.c_str(), L"BundleProviderKey", view);
    const auto name = Registry::Text(nameValue);
    const auto provider = Registry::Text(providerValue);
    InstallerLog(session, L"FolderRewind bundle-owner-match: family=" + std::wstring(matches ? L"yes" : L"no") + L", name-status=" + std::to_wstring(nameValue.error) + L", name=" + name + L", provider-status=" + std::to_wstring(providerValue.error) + L", provider=" + provider);
    return matches && name == context.name && _wcsicmp(provider.c_str(), context.code.c_str()) == 0;
}
static Registry::Value Desired(const Context& context) {
    Registry::Value value; value.state = Registry::State::Present; value.type = REG_SZ; value.error = ERROR_SUCCESS;
    const auto begin = reinterpret_cast<const BYTE*>(context.directory.c_str());
    value.bytes.assign(begin, begin + (context.directory.size()+1)*sizeof(wchar_t)); return value;
}
extern "C" UINT __stdcall PrepareBundleLocation(MSIHANDLE session) {
    const auto code = Property(session, L"FOLDERREWIND_BUNDLECODE");
    const auto family = Property(session, L"FOLDERREWIND_BUNDLEFAMILY");
    if (code.empty()) return ERROR_SUCCESS;
    if (!Guid(code) || !Guid(family) || family != Property(session, L"ExpectedBundleFamily")) return ERROR_INSTALL_FAILURE;
    GUID guid; if (FAILED(CoCreateGuid(&guid))) return ERROR_INSTALL_FAILURE;
    wchar_t text[40] = {}; StringFromGUID2(guid, text, 40);
    const auto data = (Property(session, L"ALLUSERS") == L"1" ? L"M|" : L"U|") + Property(session, L"UserSID") + L"|" + code + L"|" + family + L"|" +
        Property(session, L"INSTALLFOLDER") + L"|" + text + L"|" + Property(session, L"ProductName");
    for (const auto action : {L"WriteBundleUser",L"RollbackBundleUser",L"CommitBundleUser",L"WriteBundleMachine",L"RollbackBundleMachine",L"CommitBundleMachine"})
        if (MsiSetPropertyW(session, action, data.c_str()) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    return ERROR_SUCCESS;
}
extern "C" UINT __stdcall WriteBundleLocation(MSIHANDLE session) {
    Context context; if (!Decode(session, context)) return ERROR_INSTALL_FAILURE;
    REGSAM view = 0;
    // The Burn executable is x86 for both payload architectures. HKCU may be
    // shared between views; prefer the actual bootstrapper's 32-bit view.
    for (const auto candidate : {KEY_WOW64_32KEY, KEY_WOW64_64KEY}) if (Owned(context, candidate, session)) { view = candidate; break; }
    if (!view) { InstallerLog(session, L"FolderRewind bundle ownership/view verification failed."); return ERROR_INSTALL_FAILURE; }
    const auto path = Key(context);
    const auto before = Registry::Read(context.root, path.c_str(), L"InstallLocation", view);
    if (before.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)", SDDL_REVISION_1, &descriptor, nullptr)) return ERROR_INSTALL_FAILURE;
    SECURITY_ATTRIBUTES security = {sizeof(security), descriptor, FALSE};
    HANDLE file = CreateFileW(context.journal.c_str(), GENERIC_WRITE, 0, &security, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    LocalFree(descriptor); if (file == INVALID_HANDLE_VALUE) return ERROR_INSTALL_FAILURE;
    const auto snapshot = std::to_wstring(view) + L"\n" + (before.state == Registry::State::Missing ? L"M" : L"P") + L"\n" + std::to_wstring(before.type) + L"\n" + Registry::Hex(before.bytes) + L"\n";
    DWORD written = 0; const DWORD size = static_cast<DWORD>(snapshot.size()*sizeof(wchar_t));
    const bool saved = WriteFile(file, snapshot.data(), size, &written, nullptr) && written == size && FlushFileBuffers(file); CloseHandle(file);
    if (!saved) return ERROR_INSTALL_FAILURE;
    // Never create a missing Bundle key: Burn owns its lifetime.
    HKEY key = nullptr;
    if (RegOpenKeyExW(context.root, path.c_str(), 0, KEY_SET_VALUE | view, &key) != ERROR_SUCCESS) return ERROR_INSTALL_FAILURE;
    const auto desired = Desired(context);
    const auto status = RegSetValueExW(key, L"InstallLocation", 0, desired.type, desired.bytes.data(), static_cast<DWORD>(desired.bytes.size())); RegCloseKey(key);
    InstallerLog(session, L"FolderRewind bundle-location-write: view=" + std::to_wstring(view) + L", status=" + std::to_wstring(status));
    return status == ERROR_SUCCESS && Registry::Equal(Registry::Read(context.root, path.c_str(), L"InstallLocation", view), desired) ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
}
extern "C" UINT __stdcall RollbackBundleLocation(MSIHANDLE session) {
    Context context; if (!Decode(session, context)) return ERROR_INSTALL_FAILURE;
    HANDLE file = CreateFileW(context.journal.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    DWORD size = GetFileSize(file, nullptr), read = 0;
    if (size > 4*1024*1024 || size % sizeof(wchar_t)) { CloseHandle(file); return ERROR_INSTALL_FAILURE; }
    std::wstring text(size/sizeof(wchar_t), L'\0');
    const bool loaded = ReadFile(file, text.data(), size, &read, nullptr) && read == size; CloseHandle(file);
    if (!loaded) return ERROR_INSTALL_FAILURE;
    std::wistringstream input(text); std::wstring viewText, state, type, hex;
    if (!std::getline(input, viewText) || !std::getline(input, state) || !std::getline(input, type) || !std::getline(input, hex)) return ERROR_INSTALL_FAILURE;
    const auto view = static_cast<REGSAM>(wcstoul(viewText.c_str(), nullptr, 10));
    if ((view != KEY_WOW64_32KEY && view != KEY_WOW64_64KEY) || (state != L"P" && state != L"M")) return ERROR_INSTALL_FAILURE;
    const auto path = Key(context);
    const auto current = Registry::Read(context.root, path.c_str(), L"InstallLocation", view);
    if (current.state == Registry::State::Failed) return ERROR_INSTALL_FAILURE;
    if (current.state == Registry::State::Present && Owned(context, view, session) && Registry::Equal(current, Desired(context))) {
        Registry::Value old; old.state = Registry::State::Present; old.type = wcstoul(type.c_str(), nullptr, 10);
        if (!Registry::Unhex(hex, old.bytes)) return ERROR_INSTALL_FAILURE;
        // Open only: rollback must never recreate a removed Bundle registration.
        HKEY key = nullptr;
        auto status = RegOpenKeyExW(context.root, path.c_str(), 0, KEY_SET_VALUE | view, &key);
        if (status == ERROR_SUCCESS) {
            status = state == L"M" ? RegDeleteValueW(key, L"InstallLocation") : RegSetValueExW(key, L"InstallLocation", 0, old.type, old.bytes.data(), static_cast<DWORD>(old.bytes.size()));
            RegCloseKey(key);
        }
        if (status != ERROR_SUCCESS && status != ERROR_FILE_NOT_FOUND) return ERROR_INSTALL_FAILURE;
    }
    DeleteFileW(context.journal.c_str()); return ERROR_SUCCESS;
}
extern "C" UINT __stdcall CommitBundleLocation(MSIHANDLE session) {
    Context context; if (!Decode(session, context)) return ERROR_INSTALL_FAILURE;
    return DeleteFileW(context.journal.c_str()) || GetLastError() == ERROR_FILE_NOT_FOUND ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
}
