# FolderRewind 1.9.0 game discovery release checklist

## Scope

- Windows Steam, GOG, and Epic installation discovery.
- Ludusavi primary/local/secondary manifests plus local FolderRewind override.
- Directory, single-file, and relative-glob backup selections.
- Configuration-level `BackupRun` history and sequential grouped restore.
- MineRewind instance-level discovery through host API version 1.9.0.
- Backup Preset V2 with indefinite V1 import and official-index compatibility.

Heroic, Lutris, registry backup, remote overrides, automatic manifest updates, cross-source atomic restore, and manifest redistribution remain out of scope.

## Release gates

- [x] Opening the Beta page performs no network request; download/update is button-driven.
- [x] Cache generations are atomic and retain the current and previous valid generation.
- [x] Repository and installer contain no Ludusavi manifest or compiled index.
- [x] Registry candidates remain visible and disabled.
- [x] Low-confidence/zero-match resources are not selected by default.
- [x] Draft confirmation performs one configuration save and rolls back memory on failure.
- [x] V1 config, package, official index, and history compatibility tests pass.
- [x] V2 and V1 official indexes are read together and deduplicated by ShareId with V2 priority.
- [x] Ludusavi/PCGamingWiki attribution and the no-redistribution decision are recorded in `THIRD-PARTY-NOTICES.md` and displayed in the Beta page.
- [x] MineRewind 1.9.0 rejects hosts older than 1.9.0 through `MinHostVersion`.

## Verification commands

```powershell
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet test FolderRewind-Plugin-Minecraft/MineRewind.Tests/MineRewind.Tests.csproj -c Release --no-restore
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x86 --no-restore
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 --no-restore
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=ARM64 --no-restore
python ../folderrewind-official-templates/scripts/validate_template.py
python ../folderrewind-official-templates/scripts/validate_presets.py
```

The x64 fixture suite covers Steam/Epic manifests, multi-store identity merge, exact include-glob enumeration, run reuse/partial results/reference protection, and grouped restore continuation. A release candidate should additionally be exercised once through the packaged WinUI workflow on a Windows x64 machine before publishing.
