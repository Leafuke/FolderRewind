#pragma once
#include <windows.h>
#include <string>
#include <vector>

namespace Registry {
enum class State { Present, Missing, Failed };
struct Value {
    State state = State::Failed;
    LSTATUS error = ERROR_INVALID_DATA;
    LSTATUS openError = ERROR_INVALID_DATA;
    DWORD type = REG_NONE;
    std::vector<BYTE> bytes;
};
inline Value Read(HKEY root, const wchar_t* path, const wchar_t* name, REGSAM view = KEY_WOW64_64KEY) {
    Value result;
    HKEY key = nullptr;
    result.error = RegOpenKeyExW(root, path, 0, KEY_QUERY_VALUE | view, &key);
    result.openError = result.error;
    if (result.error == ERROR_SUCCESS) {
        DWORD size = 0;
        result.error = RegQueryValueExW(key, name, nullptr, &result.type, nullptr, &size);
        if (result.error == ERROR_SUCCESS && size <= 1024 * 1024) {
            // Keep a non-null buffer for an empty value, so a concurrent growth
            // returns MORE_DATA instead of being mistaken for a successful read.
            result.bytes.resize(size ? size : 1);
            result.error = RegQueryValueExW(key, name, nullptr, &result.type, result.bytes.data(), &size);
            if (result.error == ERROR_SUCCESS) result.bytes.resize(size);
        } else if (result.error == ERROR_SUCCESS) result.error = ERROR_FILE_TOO_LARGE;
        RegCloseKey(key);
    }
    result.state = result.error == ERROR_SUCCESS ? State::Present :
        (result.error == ERROR_FILE_NOT_FOUND || result.error == ERROR_PATH_NOT_FOUND ? State::Missing : State::Failed);
    return result;
}
inline bool Equal(const Value& left, const Value& right) {
    return left.state == State::Present && right.state == State::Present && left.type == right.type && left.bytes == right.bytes;
}
inline std::wstring Text(const Value& value) {
    if (value.state != State::Present || value.type != REG_SZ || value.bytes.size() < sizeof(wchar_t) || value.bytes.size() % sizeof(wchar_t)) return L"";
    std::wstring text(value.bytes.size() / sizeof(wchar_t), L'\0');
    memcpy(text.data(), value.bytes.data(), value.bytes.size());
    if (text.back() != L'\0') return L"";
    text.pop_back();
    return text.find(L'\0') == std::wstring::npos ? text : L"";
}
inline LSTATUS Write(HKEY root, const wchar_t* path, const wchar_t* name, const Value& value, REGSAM view = KEY_WOW64_64KEY) {
    if (value.state != State::Present) return ERROR_INVALID_DATA;
    HKEY key = nullptr;
    auto status = RegCreateKeyExW(root, path, 0, nullptr, 0, KEY_SET_VALUE | view, nullptr, &key, nullptr);
    if (status == ERROR_SUCCESS) {
        status = RegSetValueExW(key, name, 0, value.type, value.bytes.data(), static_cast<DWORD>(value.bytes.size()));
        RegCloseKey(key);
    }
    return status;
}
inline LSTATUS Delete(HKEY root, const wchar_t* path, const wchar_t* name, REGSAM view = KEY_WOW64_64KEY) {
    HKEY key = nullptr;
    auto status = RegOpenKeyExW(root, path, 0, KEY_SET_VALUE | view, &key);
    if (status == ERROR_SUCCESS) { status = RegDeleteValueW(key, name); RegCloseKey(key); }
    return status;
}
inline std::wstring Hex(const std::vector<BYTE>& bytes) {
    const wchar_t* digits = L"0123456789ABCDEF";
    std::wstring result;
    for (BYTE byte : bytes) { result += digits[byte >> 4]; result += digits[byte & 15]; }
    return result.empty() ? L"-" : result;
}
inline bool Unhex(const std::wstring& text, std::vector<BYTE>& bytes) {
    if (text == L"-") { bytes.clear(); return true; }
    if (text.size() > 2 * 1024 * 1024 || text.size() % 2 || text.find_first_not_of(L"0123456789ABCDEF") != std::wstring::npos) return false;
    bytes.clear();
    for (size_t i = 0; i < text.size(); i += 2) bytes.push_back(static_cast<BYTE>(wcstoul(text.substr(i, 2).c_str(), nullptr, 16)));
    return true;
}
}
