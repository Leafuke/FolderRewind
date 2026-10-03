#include "InstallerDiagnostics.h"
// This translation unit and export are linked ONLY into validation binaries.
extern "C" UINT __stdcall FailForTesting(MSIHANDLE session) {
    HANDLE token = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        const auto description = TokenDescription(token); CloseHandle(token);
        LogInstallerContext(session, L"control-fault", description.substr(0, description.find(L',')), false);
    }
    InstallerLog(session, L"FolderRewind isolated fault injection reached.");
    return ERROR_INSTALL_FAILURE;
}
