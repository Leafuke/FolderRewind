#include "../RegistryValue.h"
#include "../SettingsCleanup.h"
#include <objbase.h>
#include <iostream>
#include <stdexcept>
static int checks = 0;
static void Check(bool condition, const char* name) {
    if (!condition) throw std::runtime_error(name);
    ++checks;
}
static void TestSettingsCleanup(const std::wstring& id) {
    wchar_t temporary[MAX_PATH] = {}; Check(GetTempPathW(MAX_PATH,temporary) != 0,"test temp root");
    const auto base = std::wstring(temporary)+L"FolderRewind-settings-tests-"+id;
    const auto root = base+L"\\FolderRewind", backups = root+L"\\backups";
    Check(CreateDirectoryW(base.c_str(),nullptr) != 0,"create isolated base");
    Check(CreateDirectoryW(root.c_str(),nullptr) != 0,"create settings root");
    Check(CreateDirectoryW(backups.c_str(),nullptr) != 0,"create nested backup repository");
    const std::vector<std::wstring> files = {root+L"\\config.json",root+L"\\config.json.recovery.test.json",
        root+L"\\history.json",root+L"\\config.json.recovery.notes.txt",backups+L"\\config.json"};
    for (const auto& path : files) {
        HANDLE file = CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,0,nullptr);
        Check(file != INVALID_HANDLE_VALUE,"create isolated settings fixture");
        DWORD written = 0; const char content[] = "backup bytes must survive";
        const bool saved = WriteFile(file,content,sizeof(content),&written,nullptr) && written == sizeof(content); CloseHandle(file);
        Check(saved,"write fixture");
    }
    DWORD removed = 0;
    Check(SettingsCleanup::Clear(root,removed) == ERROR_SUCCESS && removed == 2,"only settings and recovery copies removed");
    Check(GetFileAttributesW(files[0].c_str()) == INVALID_FILE_ATTRIBUTES && GetFileAttributesW(files[1].c_str()) == INVALID_FILE_ATTRIBUTES,"settings absent");
    for (size_t index = 2; index < files.size(); ++index) {
        HANDLE file = CreateFileW(files[index].c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr);
        Check(file != INVALID_HANDLE_VALUE,"backup/history file retained");
        char bytes[64] = {}; DWORD read = 0;
        const bool loaded = ReadFile(file,bytes,sizeof(bytes),&read,nullptr) != 0; CloseHandle(file);
        Check(loaded && std::string(bytes) == "backup bytes must survive","backup/history bytes unchanged");
    }
    Check(CreateHardLinkW(files[0].c_str(),files[4].c_str(),nullptr) != 0,"create hardlink safety fixture");
    Check(SettingsCleanup::Clear(root,removed) == ERROR_ACCESS_DENIED && removed == 0,"hardlink to backup refused");
    Check(SettingsCleanup::Clear(root+L"\\..\\FolderRewind",removed) == ERROR_INVALID_NAME,"noncanonical target rejected");
    Check(SettingsCleanup::Clear(base,removed) == ERROR_INVALID_NAME,"wrong root rejected");
    // Only explicitly created files/directories, no recursive deletion.
    for (const auto& path : files) if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) Check(DeleteFileW(path.c_str()) != 0,"remove own fixture file");
    Check(RemoveDirectoryW(backups.c_str()) != 0 && RemoveDirectoryW(root.c_str()) != 0 && RemoveDirectoryW(base.c_str()) != 0,"remove empty test directories");
}
int wmain() {
    GUID guid; if (FAILED(CoCreateGuid(&guid))) return 1;
    wchar_t text[40] = {}; StringFromGUID2(guid, text, 40);
    const auto path = L"Software\\Leafuke\\FolderRewind.NativeTests\\" + std::wstring(text);
    HKEY root = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &root, nullptr) != ERROR_SUCCESS) return 1;
    int result = 0;
    try {
        TestSettingsCleanup(text);
        Check(Registry::Read(root, L"Run", L"value").state == Registry::State::Missing, "missing key");
        Check(Registry::Read(nullptr, L"Run", L"value").state == Registry::State::Failed, "API failure is not missing");
        Registry::Value original; original.state = Registry::State::Present; original.type = REG_BINARY; original.bytes = {3,0,0,0,1,2,255,0};
        Check(Registry::Write(root, L"Run", L"value", original) == ERROR_SUCCESS, "write raw");
        Check(Registry::Equal(Registry::Read(root, L"Run", L"value"), original), "raw bytes and type preserved");
        Check(Registry::Read(root, L"Run", L"other").state == Registry::State::Missing, "missing value");
        Check(Registry::Text(original).empty(), "binary is not a command");
        auto changed = original; changed.type = REG_SZ;
        Check(!Registry::Equal(original, changed), "types participate in ownership snapshot");
        changed = original; changed.bytes[0] = 2;
        Check(!Registry::Equal(original, changed), "concurrent disabled-state edit detected");
        std::vector<BYTE> decoded;
        Check(Registry::Unhex(Registry::Hex(original.bytes), decoded) && decoded == original.bytes, "journal round trip");
        Check(!Registry::Unhex(L"0Z", decoded) && !Registry::Unhex(L"A", decoded), "corrupt journal rejected");
        Check(Registry::Unhex(L"-", decoded) && decoded.empty(), "empty raw value supported");
        Check(Registry::Delete(root, L"Run", L"value") == ERROR_SUCCESS, "delete actual value");
        Check(Registry::Read(root, L"Run", L"value").state == Registry::State::Missing, "verify deletion");
        Check(Registry::Write(root, L"Run", L"value", original) == ERROR_SUCCESS && Registry::Equal(Registry::Read(root, L"Run", L"value"), original), "restore original value");
        Registry::Value command; command.state = Registry::State::Present; command.type = REG_SZ;
        const std::wstring expected = L"\"C:\\Test\\FolderRewind.exe\" --startup";
        const auto begin = reinterpret_cast<const BYTE*>(expected.c_str()); command.bytes.assign(begin, begin + (expected.size()+1)*sizeof(wchar_t));
        Check(Registry::Text(command) == expected, "command string decoded exactly");
        command.bytes.resize(command.bytes.size()-2);
        Check(Registry::Text(command).empty(), "unterminated command rejected");
        Registry::Value failure;
        Check(!Registry::Equal(failure, failure) && Registry::Write(root, L"Run", L"value", failure) == ERROR_INVALID_DATA, "errors cannot be restored as empty values");
    } catch (const std::exception& error) { std::cerr << error.what(); result = 1; }
    RegCloseKey(root);
    // Only the fresh GUID subkey created above is removed.
    if (RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str()) != ERROR_SUCCESS) result = 1;
    std::cout << "{\"passed\":" << (result ? "false" : "true") << ",\"checks\":" << checks << "}";
    return result;
}
