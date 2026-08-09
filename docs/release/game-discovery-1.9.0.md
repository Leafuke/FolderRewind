# FolderRewind 1.9.0 game discovery release checklist

## Scope

- Windows Steam, GOG, and Epic installation discovery with cancellable, root-isolated diagnostics.
- Ludusavi primary and manually selected secondary manifests plus a local FolderRewind override.
- Compiled Ludusavi index v3 with validated current/previous cache generations.
- User-reviewed configuration creation and three-way rediscovery proposals.
- Editable per-source `All` or `Include` scopes followed by configuration-level filters.
- Source `HistoryItem` records, optional configuration-level `BackupRun` grouping, and both history views.
- MineRewind instance-level discovery through host API version 1.9.0.

Heroic, Lutris, registry backup, automatic game-directory `.ludusavi.yaml` discovery, remote overrides, generic ProviderSettings/UserRoots UI, and cross-source atomic restore remain out of scope.

The old unpublished `HistoryMode`, discovery-origin structure, Ludusavi index v2, and BackupRun v1 have no migration path. No released FolderRewind data uses those formats.

## Automated release gates

- [x] Opening the Beta page performs no network request and edits discovery settings through a working copy.
- [x] Provider output is a candidate or change proposal; only explicit review changes an ordinary user-owned `BackupConfig`.
- [x] Backup sets retain provider identity after game candidates merge for display, and MineRewind instances create separate configurations.
- [x] Rediscovery compares the reviewed baseline, current configuration, and new upstream snapshot while preserving user changes by default.
- [x] Ludusavi alias, root/base/game, literal placeholder, recursive-directory, and future-path semantics follow index v3 tests.
- [x] Source-scope rules are bounded, validated, compiled once, cancellable, and applied before configuration filters.
- [x] Unsafe broad roots require confirmation, and source/archive overlap is blocked in both UI and backup execution.
- [x] Steam's most-recent account is selected by default; other accounts and fallback wildcards remain visible but unselected.
- [x] Scanner failures are isolated by provider root or manifest and reported as structured diagnostics.
- [x] HTTP 304 still checks local inputs; changed, removed, or corrupt inputs rebuild or recover through current/previous generations.
- [x] Configuration save is single-shot and restores both configuration and review baseline on failure.
- [x] Configuration backup persists a run only after at least one new source archive and never hides source history.
- [x] One retention count governs regular runs and per-source histories while important and retained-run references stay protected.
- [x] Source and run history views expose their respective restore, comment, importance, synchronization, and deletion behavior.
- [x] Manual source and configuration backups share one operation-comment path without rewriting comments on reused archives.
- [x] Ludusavi/PCGamingWiki attribution and the no-redistribution decision remain recorded in `THIRD-PARTY-NOTICES.md` and visible in the Beta page.

## Verification commands

```powershell
dotnet test FolderRewind.Tests/FolderRewind.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet test FolderRewind-Plugin-Minecraft/MineRewind.Tests/MineRewind.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet build FolderRewind/FolderRewind.csproj -c Release -p:Platform=x64 --no-restore
```

## Manual acceptance

- Opening discovery does not connect to the network or mutate live configuration settings.
- Download, update check, primary import, and manually selected secondary manifest all scan successfully.
- Steam multi-account defaults and warnings match the documented policy.
- A discovered configuration supports single-source backup, scope editing, source history, and ordinary restore.
- Configuration backup creates a run; single-source backup creates only source history; both views remember the last selection.
- Broad roots, source/archive overlap, and upstream narrowing cannot expand or shrink protection without explicit confirmation.

A release candidate must be exercised once through the packaged WinUI workflow on Windows x64 before publication.
