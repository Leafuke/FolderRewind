#include <windows.h>
#include <msiquery.h>
#include <string>
#include <vector>
#include <shlobj.h>
#include <winternl.h>
#include "WindowsVersion.h"
#include <tlhelp32.h>

static std::wstring Property(MSIHANDLE session, const wchar_t* name)
{
    DWORD size = 0;
    MsiGetPropertyW(session, name, L"", &size);
    std::vector<wchar_t> buffer(static_cast<size_t>(size) + 1);
    size = static_cast<DWORD>(buffer.size());
    if (MsiGetPropertyW(session, name, buffer.data(), &size) != ERROR_SUCCESS) return L"";
    return buffer.data();
}

static UINT Fail(MSIHANDLE session, const wchar_t* en, const wchar_t* zh)
{
    MSIHANDLE record = MsiCreateRecord(1);
    MsiRecordSetStringW(record, 0, L"[1]");
    MsiRecordSetStringW(record, 1, Property(session, L"ProductLanguage") == L"2052" ? zh : en);
    MsiProcessMessage(session, INSTALLMESSAGE_ERROR, record);
    MsiCloseHandle(record);
    return ERROR_INSTALL_FAILURE;
}

static bool RunningFrom(std::wstring directory, bool machine)
{
    while (!directory.empty() && directory.back() == L'\\') directory.pop_back();
    const auto target = directory + L"\\FolderRewind.exe";
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return true;
    PROCESSENTRY32W entry = {}; entry.dwSize = sizeof(entry);
    bool running = false;
    if (Process32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(entry.szExeFile, L"FolderRewind.exe") != 0) continue;
        HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ProcessID);
        if (!process) { if (machine) { running = true; break; } continue; }
        wchar_t path[32768] = {}; DWORD length = ARRAYSIZE(path);
        if (QueryFullProcessImageNameW(process, 0, path, &length)) running = _wcsicmp(path, target.c_str()) == 0;
        else running = true;
        CloseHandle(process);
        if (running) break;
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot); return running;
}

static bool RegisteredFamily(MSIHANDLE session, bool machine, std::wstring& location)
{
    const auto upgrade = Property(session, L"UpgradeCode");
    const auto sid = Property(session, L"UserSID");
    wchar_t product[39] = {};
    for (DWORD index = 0; MsiEnumRelatedProductsW(upgrade.c_str(), 0, index, product) == ERROR_SUCCESS; ++index) {
        DWORD length = 0;
        const auto status = MsiGetProductInfoExW(product, machine ? nullptr : sid.c_str(),
            machine ? MSIINSTALLCONTEXT_MACHINE : MSIINSTALLCONTEXT_USERUNMANAGED,
            INSTALLPROPERTY_INSTALLEDPRODUCTNAME, L"", &length);
        if (status == ERROR_SUCCESS || status == ERROR_MORE_DATA) {
            length = 0;
            MsiGetProductInfoExW(product, machine ? nullptr : sid.c_str(), machine ? MSIINSTALLCONTEXT_MACHINE : MSIINSTALLCONTEXT_USERUNMANAGED,
                INSTALLPROPERTY_INSTALLLOCATION, L"", &length);
            std::vector<wchar_t> value(static_cast<size_t>(length) + 1); length = static_cast<DWORD>(value.size());
            if (MsiGetProductInfoExW(product, machine ? nullptr : sid.c_str(), machine ? MSIINSTALLCONTEXT_MACHINE : MSIINSTALLCONTEXT_USERUNMANAGED,
                INSTALLPROPERTY_INSTALLLOCATION, value.data(), &length) == ERROR_SUCCESS) location = value.data();
            return true;
        }
    }
    return false;
}

extern "C" UINT __stdcall InstallerPreflight(MSIHANDLE session)
{
    if (!Property(session, L"REMOVE").empty()) {
        const auto existing = Property(session, Property(session, L"ALLUSERS") == L"1" ? L"REMEMBEREDMACHINEPATH" : L"REMEMBEREDUSERPATH");
        if (!existing.empty() && RunningFrom(existing, Property(session, L"ALLUSERS") == L"1"))
            return Fail(session, L"Exit FolderRewind normally before uninstalling or changing features. Use Quit in the tray menu and wait for active tasks to finish.", L"请先正常退出 FolderRewind，再卸载或修改选项。请在托盘菜单中选择“退出”，并等待任务清理完成。");
        if (!existing.empty()) MsiSetPropertyW(session, L"INSTALLFOLDER", existing.c_str());
        return ERROR_SUCCESS;
    }
    bool supported = false;
    if (FAILED(SupportedWindows(supported))) return Fail(session, L"Cannot verify the installed Windows version.", L"无法验证当前 Windows 系统版本。");
    if (!supported)
        return Fail(session, L"FolderRewind requires Windows 10 version 1809 or later.", L"FolderRewind 需要 Windows 10 1809 或更新版本。");
    const bool machine = Property(session, L"ALLUSERS") == L"1" ||
        (Property(session, L"ALLUSERS") == L"2" && Property(session, L"MSIINSTALLPERUSER") != L"1");
    USHORT native = 0;
    if (FAILED(NativeMachine(native))) return Fail(session, L"Cannot verify the Windows architecture.", L"无法验证 Windows 系统架构。");
    const auto architecture = Property(session, L"PAYLOADARCH");
    if ((architecture == L"arm64" && native != IMAGE_FILE_MACHINE_ARM64) ||
        (architecture == L"x64" && native != IMAGE_FILE_MACHINE_AMD64))
        return Fail(session, L"This package does not support your Windows architecture.", L"此安装包不支持当前 Windows 系统架构。");
    // A failed legacy uninstall can leave directory markers behind. Treat MSI
    // registration as authoritative; orphaned markers cannot block a new install.
    std::wstring userPath, machinePath;
    const bool hasUserInstall = RegisteredFamily(session, false, userPath);
    const bool hasMachineInstall = RegisteredFamily(session, true, machinePath);
    if (hasUserInstall && userPath.empty()) userPath = Property(session, L"REMEMBEREDUSERPATH");
    if (hasMachineInstall && machinePath.empty()) machinePath = Property(session, L"REMEMBEREDMACHINEPATH");
    if ((machine && hasUserInstall) || (!machine && hasMachineInstall))
        return Fail(session, L"FolderRewind is installed for a different scope. Uninstall that installation before changing scope. Your configuration, plugins and backups are preserved.",
            L"FolderRewind 已在另一个安装范围中安装。请先卸载原安装，再更改范围重新安装；配置、插件和备份会保留。");
    auto path = Property(session, L"INSTALLFOLDER");
    const auto remembered = machine ? machinePath : userPath;
    if (path.empty() && !remembered.empty()) path = remembered;
    if (path.empty()) {
        if (machine) path = Property(session, L"ProgramFiles64Folder") + Property(session, L"ProductName");
        else path = Property(session, L"LocalAppDataFolder") + L"Programs\\" + Property(session, L"ProductName");
    }
    if (path.size() < 4 || path[1] != L':' || path[2] != L'\\' || path.find(L"..") != std::wstring::npos || path.find_first_of(L"|\r\n\t") != std::wstring::npos)
        return Fail(session, L"Choose an absolute local installation folder.", L"请选择有效的本地绝对安装路径。");
    if (!remembered.empty() && _wcsicmp(path.c_str(), remembered.c_str()) != 0) {
        auto normalized = remembered;
        while (!normalized.empty() && normalized.back() == L'\\') normalized.pop_back();
        while (!path.empty() && path.back() == L'\\') path.pop_back();
        if (_wcsicmp(path.c_str(), normalized.c_str()) != 0)
            return Fail(session, L"Upgrades and maintenance must retain the existing folder. Uninstall first to change the installation path.", L"升级和维护必须保留现有安装目录。要更改目录，请先卸载再安装。");
    }
    if (!machine && Property(session, L"Installed").empty()) {
        // MSI redirects ProgramFilesFolder for a per-user package. Resolve the
        // actual protected directories instead of rejecting the user default.
        std::vector<std::wstring> protectedDirectories;
        for (const auto folder : { &FOLDERID_ProgramFilesX64, &FOLDERID_ProgramFilesX86 }) {
            PWSTR actual = nullptr;
            if (SUCCEEDED(SHGetKnownFolderPath(*folder, 0, nullptr, &actual))) { protectedDirectories.emplace_back(actual); CoTaskMemFree(actual); }
        }
        wchar_t windows[32768] = {};
        if (GetWindowsDirectoryW(windows, ARRAYSIZE(windows))) protectedDirectories.emplace_back(windows);
        for (auto protectedPath : protectedDirectories) {
            if (!protectedPath.empty() && protectedPath.back() != L'\\') protectedPath += L'\\';
            if (!protectedPath.empty() && path.size() >= protectedPath.size() && _wcsnicmp(path.c_str(), protectedPath.c_str(), protectedPath.size()) == 0)
                return Fail(session, L"This folder requires administrator permissions. Choose another folder or select All users in Setup options.", L"此目录需要管理员权限。请选择其他目录，或在安装选项中选择“所有用户”。");
        }
        std::wstring parent = path;
        while (GetFileAttributesW(parent.c_str()) == INVALID_FILE_ATTRIBUTES) {
            if (parent.size() <= 3) break;
            auto split = parent.find_last_of(L'\\');
            if (split == std::wstring::npos || split < 2) break;
            parent.resize(split + (split == 2 ? 1 : 0));
        }
        const auto probe = parent + L"\\.FolderRewind-write-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
        HANDLE handle = CreateFileW(probe.c_str(), GENERIC_WRITE | DELETE, 0, nullptr, CREATE_NEW,
            FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
            return Fail(session, L"This folder requires administrator permissions. Choose a writable folder or select All users in Setup options.", L"此目录需要管理员权限。请选择可写目录，或在安装选项中选择“所有用户”。");
        CloseHandle(handle);
    }
    if (path.back() != L'\\') path += L'\\';
    if (RunningFrom(path, machine))
        return Fail(session, L"Exit FolderRewind normally before installation, upgrade or repair. Use Quit in the tray menu and wait for active tasks to finish.", L"安装、升级或维修前，请先在托盘菜单中正常退出 FolderRewind，并等待任务清理完成。");
    MsiSetPropertyW(session, L"INSTALLFOLDER", path.c_str());
    MsiSetPropertyW(session, L"ARPINSTALLLOCATION", path.c_str());
    return ERROR_SUCCESS;
}
