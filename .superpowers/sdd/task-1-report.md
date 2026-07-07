# Task 1 Report

## Status

DONE_WITH_CONCERNS

## Scope

- Added Task 1 rename preview/result/move operation contracts.
- Added `FolderRenameService.PreviewRename`.
- Added `FolderRenameService.ResolveUpdatedDisplayName`.
- Added `FolderRenameService.ResolveUpdatedHistoryFolderName`.
- Added focused service tests for the Task 1 rules.

## TDD Evidence

- Red test file added first in `FolderRewind.Tests/Services/FolderRenameServiceTests.cs`.
- Initial red command failed because this test project does not use implicit xUnit usings, so `using Xunit;` was added.
- Red command then failed for the expected missing production service:
  `CS0103: The name 'FolderRenameService' does not exist in the current context`.
- Production files were added after the expected red failure.
- Green command passed after implementation:
  `dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --filter FullyQualifiedName~FolderRenameServiceTests`

## Verification

Focused tests:

```text
Passed: 3
Failed: 0
Skipped: 0
Total: 3
```

## Notes

- `FolderRewind/Package.appxmanifest` had pre-existing user changes and was not touched.
- The brief's literal `new ManagedFolder { ... }` preview test body crashes the test host before Task 1 code runs. A diagnostic test confirmed that constructing `ManagedFolder` alone triggers the crash, likely from existing `ManagedFolder` field initializers that call WinRT-backed localization in this test host. Because the allowed write scope excludes `BackupModels.cs` and `I18n.cs`, the Task 1 test creates the `ManagedFolder` fixture with `RuntimeHelpers.GetUninitializedObject` and then sets the public properties. This keeps the test behavior focused on `FolderRenameService.PreviewRename` without editing out-of-scope production code.

## Review Fix Pass

Status: DONE

Scope:

- Added tests for Windows-reserved device names and trailing dot/space leaf names.
- Added tests for affected config/history counting when stored paths differ only by trailing separator normalization.
- Added centralized private path normalization/comparison helpers in `FolderRenameService`.
- Updated preview validation to reject Windows device names, device names with extensions, and trailing dot/space names before returning a valid preview.

Red run:

```text
Command:
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --filter FullyQualifiedName~FolderRenameServiceTests

Output summary:
Failed: 9
Passed: 3
Skipped: 0
Total: 12

Expected failures included:
- PreviewRename_rejects_windows_reserved_or_trailing_leaf_names accepted CON, prn.txt, AUX, NUL, COM1, LPT9.zip, WorldTwo., and WorldTwo .
- PreviewRename_counts_matching_paths_after_normalization returned AffectedConfigCount = 0 instead of 1.
```

Green run:

```text
Command:
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --filter FullyQualifiedName~FolderRenameServiceTests

Output:
  Determining projects to restore...
  All projects are up-to-date for restore.
  FolderRewind -> D:\Programs\FolderRewind\FolderRewind\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FolderRewind.dll
  FolderRewind.Tests -> D:\Programs\FolderRewind\FolderRewind.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FolderRewind.Tests.dll
D:\Programs\FolderRewind\FolderRewind.Tests\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FolderRewind.Tests.dll (.NETCoreApp,Version=v10.0) test run
Total 1 test file matched the specified pattern.

Passed! - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 29 ms - FolderRewind.Tests.dll (net10.0)
```

Notes:

- `FolderRewind/Package.appxmanifest` remained untouched.
- No Task 2 rename execution, on-disk rename, config mutation, history mutation, or cloud migration work was added.

## Review Fix Pass 2

Status: DONE

Fix:

- Updated `FolderRenameService.PreviewRename` to derive storage-folder names from current/new display-name semantics via `ResolveUpdatedDisplayName(...)`.
- Rejected non-renameable root/share-root source paths before composing a renamed path.

Verification:

```text
Command:
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false --filter FullyQualifiedName~FolderRenameServiceTests

Result:
Passed! - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 26 ms - FolderRewind.Tests.dll (net10.0)
```
