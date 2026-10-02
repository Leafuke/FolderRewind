#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <msiquery.h>
#include <objbase.h>
#include <winternl.h>
#include <string>
#include <vector>
#include <BootstrapperApplication.h>
#include <IBootstrapperEngine.h>
#include <BAFunctions.h>
#include <dutil.h>
#include <shelutil.h>
#include "WindowsVersion.h"

static IBootstrapperEngine* engine = nullptr;
static BOOTSTRAPPER_DISPLAY display = BOOTSTRAPPER_DISPLAY_UNKNOWN;
static HWND window = nullptr;
static HWND launchCheckbox = nullptr;
static HWND folderEdit = nullptr;
static HWND userScopeButton = nullptr, machineScopeButton = nullptr;
static HHOOK dialogHook = nullptr;
static std::wstring existingScope, existingPath;
static bool applied = false;

static std::wstring Variable(const wchar_t* name, bool format = false)
{
    SIZE_T length = 0;
    engine->GetVariableString(name, nullptr, &length);
    std::vector<wchar_t> value(length + 1);
    length = value.size();
    if (FAILED(engine->GetVariableString(name, value.data(), &length))) return L"";
    if (!format) return value.data();
    SIZE_T formattedLength = 0;
    engine->FormatString(value.data(), nullptr, &formattedLength);
    std::vector<wchar_t> result(formattedLength + 1);
    formattedLength = result.size();
    return SUCCEEDED(engine->FormatString(value.data(), result.data(), &formattedLength)) ? result.data() : L"";
}
static LONGLONG Number(const wchar_t* name)
{
    LONGLONG value = 0;
    if (FAILED(engine->GetVariableNumeric(name, &value))) value = _wcstoi64(Variable(name).c_str(), nullptr, 10);
    return value;
}
static std::wstring RegistryPath(HKEY root)
{
    // Directory markers can survive a failed legacy uninstall or rollback.
    // Only registered installations in this MSI family may lock path/scope.
    const auto family = Variable(L"FolderRewindMsiUpgradeCode");
    const auto context = root == HKEY_LOCAL_MACHINE ? MSIINSTALLCONTEXT_MACHINE : MSIINSTALLCONTEXT_USERUNMANAGED;
    wchar_t product[39] = {};
    for (DWORD index = 0; MsiEnumRelatedProductsW(family.c_str(), 0, index, product) == ERROR_SUCCESS; ++index) {
        DWORD length = 0;
        const auto status = MsiGetProductInfoExW(product, nullptr, context, INSTALLPROPERTY_INSTALLEDPRODUCTNAME, L"", &length);
        if (status != ERROR_SUCCESS && status != ERROR_MORE_DATA) continue;
        length = 0;
        MsiGetProductInfoExW(product, nullptr, context, INSTALLPROPERTY_INSTALLLOCATION, L"", &length);
        std::vector<wchar_t> location(static_cast<size_t>(length) + 1); length = static_cast<DWORD>(location.size());
        if (MsiGetProductInfoExW(product, nullptr, context, INSTALLPROPERTY_INSTALLLOCATION, location.data(), &length) == ERROR_SUCCESS && location[0])
            return location.data();
        // A registered legacy product may predate ARPINSTALLLOCATION.
        wchar_t buffer[32768] = {};
        DWORD bytes = sizeof(buffer);
        const auto key = Variable(L"FolderRewindRegistryKey");
        if (!key.empty() && RegGetValueW(root, key.c_str(), L"InstallFolder", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, buffer, &bytes) == ERROR_SUCCESS)
            return buffer;
    }
    return L"";
}
static bool Chinese() { return Number(L"FolderRewindSelectedLanguage") == 2052; }
static LRESULT CALLBACK DialogActivationProc(int code, WPARAM wp, LPARAM lp)
{
    if (code == HCBT_ACTIVATE && !Chinese()) {
        const HWND dialog = reinterpret_cast<HWND>(wp);
        wchar_t title[256] = {}, name[64] = {};
        GetWindowTextW(dialog, title, ARRAYSIZE(title)); GetClassNameW(dialog, name, ARRAYSIZE(name));
        if (_wcsicmp(name, L"#32770") == 0 && wcsncmp(title, L"FolderRewind", 12) == 0) {
            const int ids[] = { IDOK, IDCANCEL, IDYES, IDNO, IDRETRY, IDABORT, IDIGNORE };
            const wchar_t* labels[] = { L"OK", L"Cancel", L"&Yes", L"&No", L"&Retry", L"&Abort", L"&Ignore" };
            for (size_t index = 0; index < ARRAYSIZE(ids); ++index) {
                HWND control = GetDlgItem(dialog, ids[index]);
                if (control && GetClassNameW(control, name, ARRAYSIZE(name)) && _wcsicmp(name, L"Button") == 0)
                    SetWindowTextW(control, labels[index]);
            }
        }
    }
    return CallNextHookEx(dialogHook, code, wp, lp);
}
static void ShowError(const wchar_t* en, const wchar_t* zh)
{
    engine->Log(BOOTSTRAPPER_LOG_LEVEL_ERROR, en);
    if (display == BOOTSTRAPPER_DISPLAY_FULL) MessageBoxW(window, Chinese() ? zh : en, L"FolderRewind", MB_OK | MB_ICONWARNING);
}
static LRESULT CALLBACK ScopeWindowProc(HWND hwnd, UINT message, WPARAM wp, LPARAM lp, UINT_PTR id, DWORD_PTR)
{
    if (message == WM_COMMAND && HIWORD(wp) == BN_CLICKED && folderEdit && existingPath.empty() &&
        (reinterpret_cast<HWND>(lp) == userScopeButton || reinterpret_cast<HWND>(lp) == machineScopeButton)) {
        wchar_t current[32768] = {}; GetWindowTextW(folderEdit, current, ARRAYSIZE(current));
        const auto userDefault = Variable(L"FolderRewindUserFolder", true);
        const auto machineDefault = Variable(L"FolderRewindMachineFolder", true);
        if (_wcsicmp(current, userDefault.c_str()) == 0 || _wcsicmp(current, machineDefault.c_str()) == 0) {
            const auto& chosen = reinterpret_cast<HWND>(lp) == machineScopeButton ? machineDefault : userDefault;
            engine->SetVariableString(L"InstallFolder", chosen.c_str(), FALSE);
            SetWindowTextW(folderEdit, chosen.c_str());
        }
    }
    if (message == WM_NCDESTROY) RemoveWindowSubclass(hwnd, ScopeWindowProc, id);
    return DefSubclassProc(hwnd, message, wp, lp);
}
static HRESULT WINAPI FunctionsProc(BA_FUNCTIONS_MESSAGE message, const LPVOID args, LPVOID results, LPVOID)
{
    if (message == BA_FUNCTIONS_MESSAGE_ONTHEMELOADED) {
        window = static_cast<BA_FUNCTIONS_ONTHEMELOADED_ARGS*>(args)->hWnd;
        if (!SetWindowSubclass(window, ScopeWindowProc, 0xF1901, 0)) return HRESULT_FROM_WIN32(GetLastError());
        dialogHook = SetWindowsHookExW(WH_CBT, DialogActivationProc, nullptr, GetCurrentThreadId());
        if (!dialogHook) return HRESULT_FROM_WIN32(GetLastError());
    }
    if (message == BA_FUNCTIONS_MESSAGE_ONTHEMECONTROLLOADED) {
        const auto control = static_cast<BA_FUNCTIONS_ONTHEMECONTROLLOADED_ARGS*>(args);
        if (control->wzName && _wcsicmp(control->wzName, L"LaunchAfterInstallChosen") == 0) launchCheckbox = control->hWnd;
        if (control->wzName && _wcsicmp(control->wzName, L"InstallFolder") == 0) folderEdit = control->hWnd;
        if (control->wzName && _wcsicmp(control->wzName, L"ScopeCurrentUserButton") == 0) userScopeButton = control->hWnd;
        if (control->wzName && _wcsicmp(control->wzName, L"ScopeAllUsersButton") == 0) machineScopeButton = control->hWnd;
    }
    if (message == BA_FUNCTIONS_MESSAGE_ONDETECTCOMPLETE) {
        const auto product = Variable(L"FolderRewindProductCode");
        if (existingPath.empty() && MsiQueryProductStateW(product.c_str()) == INSTALLSTATE_DEFAULT) {
            DWORD length = 0; MsiGetProductInfoW(product.c_str(), INSTALLPROPERTY_INSTALLLOCATION, L"", &length);
            std::vector<wchar_t> location(static_cast<size_t>(length) + 1); length = static_cast<DWORD>(location.size());
            if (MsiGetProductInfoW(product.c_str(), INSTALLPROPERTY_INSTALLLOCATION, location.data(), &length) == ERROR_SUCCESS)
                existingPath = location.data();
            wchar_t assignment[2] = {}; length = ARRAYSIZE(assignment);
            if (MsiGetProductInfoW(product.c_str(), INSTALLPROPERTY_ASSIGNMENTTYPE, assignment, &length) == ERROR_SUCCESS)
                existingScope = assignment[0] == L'1' ? L"PerMachine" : L"PerUser";
        }
        if (!existingPath.empty()) {
            engine->SetVariableString(L"InstallFolder", existingPath.c_str(), FALSE);
            engine->SetVariableString(L"WixStdBAScope", existingScope.c_str(), FALSE);
        } else {
            const auto expanded = Variable(L"InstallFolder", true);
            engine->SetVariableString(L"InstallFolder", expanded.c_str(), FALSE);
        }
        // Maintenance reuses the transform registered by Windows Installer.
        const bool maintenance = MsiQueryProductStateW(product.c_str()) != INSTALLSTATE_UNKNOWN;
        engine->SetVariableNumeric(L"FolderRewindMaintenance", maintenance ? 1 : 0);
        engine->SetVariableString(L"FolderRewindMsiTransform", !maintenance && Chinese() ? L":zh-CN.mst" : L"", FALSE);
    }
    if (message == BA_FUNCTIONS_MESSAGE_ONPLANBEGIN) {
        auto result = static_cast<BA_ONPLANBEGIN_RESULTS*>(results);
        const auto scope = Variable(L"WixStdBAScope");
        if (!existingScope.empty() && scope != existingScope) {
            ShowError(L"Uninstall the existing installation before changing installation scope. Configuration, plugins and backups are preserved.", L"更改安装范围前，请先卸载原安装。配置、插件和备份会保留。");
            result->fCancel = TRUE;
            return S_OK;
        }
        auto path = Variable(L"InstallFolder", true);
        const auto userDefault = Variable(L"FolderRewindUserFolder", true);
        if (scope == L"PerMachine" && path == userDefault) {
            path = Variable(L"FolderRewindMachineFolder", true);
            engine->SetVariableString(L"InstallFolder", path.c_str(), FALSE);
        }
        if (scope == L"PerUser") {
            const auto protectedDirectory = Variable(L"ProgramFiles64Folder", true);
            if (!protectedDirectory.empty() && path.size() >= protectedDirectory.size() && _wcsnicmp(path.c_str(), protectedDirectory.c_str(), protectedDirectory.size()) == 0) {
                ShowError(L"This folder needs administrator permissions. Choose another folder or select All users.", L"此目录需要管理员权限。请选择其他目录，或选择“所有用户”。");
                result->fCancel = TRUE;
                return S_OK;
            }
        }
        if (display == BOOTSTRAPPER_DISPLAY_FULL && Number(L"WixBundleAction") >= BOOTSTRAPPER_ACTION_INSTALL) {
            const auto summary = (Chinese() ? L"请确认安装选项：\n\n范围：" : L"Confirm installation options:\n\nScope: ") +
                std::wstring(scope == L"PerMachine" ? (Chinese() ? L"所有用户（需要管理员权限）" : L"All users (administrator permissions)") : (Chinese() ? L"仅当前用户" : L"Current user")) +
                (Chinese() ? L"\n目录：" : L"\nFolder: ") + path +
                (Number(L"DesktopShortcutChosen") ? (Chinese() ? L"\n创建桌面快捷方式" : L"\nCreate desktop shortcut") : (Chinese() ? L"\n不创建桌面快捷方式" : L"\nNo desktop shortcut"));
            if (MessageBoxW(window, summary.c_str(), L"FolderRewind", MB_OKCANCEL | MB_ICONINFORMATION) != IDOK) result->fCancel = TRUE;
        }
    }
    if (message == BA_FUNCTIONS_MESSAGE_ONPLANMSIFEATURE) {
        const auto event = static_cast<BA_ONPLANMSIFEATURE_ARGS*>(args);
        auto result = static_cast<BA_ONPLANMSIFEATURE_RESULTS*>(results);
        if (Number(L"WixBundleAction") > BOOTSTRAPPER_ACTION_UNINSTALL && _wcsicmp(event->wzFeatureId, L"MainFeature") == 0)
            result->requestedState = BOOTSTRAPPER_FEATURE_STATE_LOCAL;
        if (Number(L"WixBundleAction") > BOOTSTRAPPER_ACTION_UNINSTALL && _wcsicmp(event->wzFeatureId, L"DesktopShortcutFeature") == 0)
            result->requestedState = Number(L"DesktopShortcutChosen") ? BOOTSTRAPPER_FEATURE_STATE_LOCAL : BOOTSTRAPPER_FEATURE_STATE_ABSENT;
    }
    if (message == BA_FUNCTIONS_MESSAGE_ONAPPLYCOMPLETE) applied = SUCCEEDED(static_cast<BA_ONAPPLYCOMPLETE_ARGS*>(args)->hrStatus);
    if (message == BA_FUNCTIONS_MESSAGE_ONTHEMECONTROLWMCOMMAND) {
        const auto event = static_cast<BA_FUNCTIONS_ONTHEMECONTROLWMCOMMAND_ARGS*>(args);
        auto result = static_cast<BA_FUNCTIONS_ONTHEMECONTROLWMCOMMAND_RESULTS*>(results);
        if (HIWORD(event->wParam) == BN_CLICKED && event->wzName && folderEdit && existingPath.empty() &&
            (_wcsicmp(event->wzName, L"ScopeCurrentUserButton") == 0 || _wcsicmp(event->wzName, L"ScopeAllUsersButton") == 0)) {
            wchar_t current[32768] = {}; GetWindowTextW(folderEdit, current, ARRAYSIZE(current));
            const auto userDefault = Variable(L"FolderRewindUserFolder", true);
            const auto machineDefault = Variable(L"FolderRewindMachineFolder", true);
            if (_wcsicmp(current, userDefault.c_str()) == 0 || _wcsicmp(current, machineDefault.c_str()) == 0) {
                const auto& chosen = _wcsicmp(event->wzName, L"ScopeAllUsersButton") == 0 ? machineDefault : userDefault;
                engine->SetVariableString(L"InstallFolder", chosen.c_str(), FALSE);
                SetWindowTextW(folderEdit, chosen.c_str());
            }
        }
        if (HIWORD(event->wParam) == BN_CLICKED && event->wzName && _wcsicmp(event->wzName, L"FinishButton") == 0) {
            if (applied && display == BOOTSTRAPPER_DISPLAY_FULL && Number(L"WixBundleAction") > BOOTSTRAPPER_ACTION_UNINSTALL && launchCheckbox && SendMessageW(launchCheckbox, BM_GETCHECK, 0, 0) == BST_CHECKED) {
                const auto target = Variable(L"LaunchTarget", true);
                const auto directory = Variable(L"InstallFolder", true);
                if (FAILED(ShelExecUnelevated(target.c_str(), nullptr, L"open", directory.c_str(), SW_SHOWNORMAL)))
                    ShowError(L"Installation finished, but the application could not be launched. Use the Start menu shortcut.", L"安装已完成，但无法启动应用。请使用开始菜单中的快捷方式启动。");
            }
            PostMessageW(window, WM_CLOSE, 0, 0);
            result->fProcessed = TRUE;
        }
        if (HIWORD(event->wParam) == BN_CLICKED && event->wzName && _wcsicmp(event->wzName, L"ModifyFeaturesButton") == 0) {
            engine->Plan(BOOTSTRAPPER_ACTION_MODIFY, existingScope == L"PerMachine" ? BOOTSTRAPPER_SCOPE_PER_MACHINE : BOOTSTRAPPER_SCOPE_PER_USER);
            result->fProcessed = TRUE;
        }
    }
    return S_OK;
}

extern "C" HRESULT WINAPI BAFunctionsCreate(const BA_FUNCTIONS_CREATE_ARGS* args, BA_FUNCTIONS_CREATE_RESULTS* results)
{
    if (!args || !results || !args->pCommand || !args->pEngine) return E_INVALIDARG;
    engine = args->pEngine;
    engine->AddRef();
    display = args->pCommand->display;
    results->pfnBAFunctionsProc = FunctionsProc;
    results->pvBAFunctionsProcContext = nullptr;
    existingPath = RegistryPath(HKEY_CURRENT_USER);
    existingScope = existingPath.empty() ? L"" : L"PerUser";
    const auto machinePath = RegistryPath(HKEY_LOCAL_MACHINE);
    if (!machinePath.empty()) { existingPath = machinePath; existingScope = L"PerMachine"; }
    int count = 0;
    std::wstring fullCommand = L"setup ";
    if (args->pCommand->wzCommandLine) fullCommand += args->pCommand->wzCommandLine;
    LPWSTR* command = CommandLineToArgvW(fullCommand.c_str(), &count);
    bool explicitLanguage = false, desktopOverride = false;
    int language = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_CHINESE ? 2052 : 1033;
    for (int index = 0; command && index < count; ++index) {
        if (_wcsnicmp(command[index], L"DesktopShortcutChosen=", 22) == 0) desktopOverride = true;
        if (_wcsicmp(command[index], L"-lang") == 0 || _wcsicmp(command[index], L"/lang") == 0) {
            explicitLanguage = true;
            if (index + 1 < count) language = _wcsicmp(command[index + 1], L"zh-CN") == 0 ? 2052 : (_wcsicmp(command[index + 1], L"en-US") == 0 ? 1033 : _wtoi(command[index + 1]));
        }
    }
    if (command) LocalFree(command);
    if (!existingPath.empty() && !desktopOverride) {
        DWORD chosen = 0, size = sizeof(chosen);
        RegGetValueW(existingScope == L"PerMachine" ? HKEY_LOCAL_MACHINE : HKEY_CURRENT_USER,
            Variable(L"FolderRewindRegistryKey").c_str(), L"DesktopShortcut", RRF_RT_REG_DWORD | RRF_SUBKEY_WOW6464KEY, nullptr, &chosen, &size);
        engine->SetVariableNumeric(L"DesktopShortcutChosen", chosen == 1 ? 1 : 0);
    }

    if (language != 1033 && language != 2052) return E_INVALIDARG;
    if (!explicitLanguage && display == BOOTSTRAPPER_DISPLAY_FULL) {
        TASKDIALOG_BUTTON languages[] = { {2052, L"简体中文"}, {1033, L"English"} };
        TASKDIALOG_BUTTON buttons[] = { {IDOK, L"继续 / Continue"}, {IDCANCEL, L"取消 / Cancel"} };
        TASKDIALOGCONFIG config = { sizeof(config) };
        config.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_POSITION_RELATIVE_TO_WINDOW;
        config.pszWindowTitle = L"FolderRewind Setup / 安装";
        config.pszMainInstruction = L"选择安装语言 / Choose installation language";
        config.pszContent = L"应用中的语言设置不会改变。\nThis does not change the application's language setting.";
        config.cButtons = ARRAYSIZE(buttons); config.pButtons = buttons; config.nDefaultButton = IDOK;
        config.cRadioButtons = ARRAYSIZE(languages); config.pRadioButtons = languages; config.nDefaultRadioButton = language;
        int button = 0;
        HRESULT result = TaskDialogIndirect(&config, &button, &language, nullptr);
        if (FAILED(result)) return result;
        if (button != IDOK) { engine->Quit(ERROR_INSTALL_USEREXIT); return HRESULT_FROM_WIN32(ERROR_INSTALL_USEREXIT); }
    }
    ULONG languageCount = 0;
    const wchar_t zh[] = L"zh-CN\0", en[] = L"en-US\0";
    if (!SetProcessPreferredUILanguages(MUI_LANGUAGE_NAME, language == 2052 ? zh : en, &languageCount)) return HRESULT_FROM_WIN32(GetLastError());
    if (!SetThreadUILanguage(static_cast<LANGID>(language))) return HRESULT_FROM_WIN32(GetLastError());
    engine->SetVariableNumeric(L"FolderRewindSelectedLanguage", language);
    engine->Log(BOOTSTRAPPER_LOG_LEVEL_STANDARD, language == 2052 ? L"FolderRewind: selected zh-CN before standard BA localization." : L"FolderRewind: selected en-US before standard BA localization.");
    bool supported = false;
    const HRESULT versionResult = SupportedWindows(supported);
    if (FAILED(versionResult)) return versionResult;
    if (!supported) {
        if (display == BOOTSTRAPPER_DISPLAY_FULL) MessageBoxExW(nullptr, language == 2052 ? L"需要 Windows 10 1809 或更新版本。" : L"Windows 10 version 1809 or later is required.", L"FolderRewind", MB_OK | MB_ICONERROR, static_cast<WORD>(language));
        return HRESULT_FROM_WIN32(ERROR_OLD_WIN_VERSION);
    }
    USHORT native = 0;
    const HRESULT architectureResult = NativeMachine(native);
    if (FAILED(architectureResult)) return architectureResult;
    const auto arch = Variable(L"FolderRewindArchitecture");
    if ((arch == L"arm64" && native != IMAGE_FILE_MACHINE_ARM64) || (arch == L"x64" && native != IMAGE_FILE_MACHINE_AMD64)) {
        if (display == BOOTSTRAPPER_DISPLAY_FULL) MessageBoxW(nullptr, language == 2052 ? L"此安装包不支持当前 Windows 架构。" : L"This package does not support your Windows architecture.", L"FolderRewind", MB_OK | MB_ICONERROR);
        return HRESULT_FROM_WIN32(ERROR_INSTALL_PLATFORM_UNSUPPORTED);
    }
    return S_OK;
}
extern "C" void WINAPI BAFunctionsDestroy(const BA_FUNCTIONS_DESTROY_ARGS*, BA_FUNCTIONS_DESTROY_RESULTS*)
{
    if (dialogHook) { UnhookWindowsHookEx(dialogHook); dialogHook = nullptr; }
    if (engine) { engine->Release(); engine = nullptr; }
}
