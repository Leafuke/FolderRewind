# Discovery proposes user-owned backup configurations

FolderRewind treats automatic discovery as a provider-driven proposal and review workflow, not as a backup mode. Providers report games, independently identified backup sets, and resources; only explicit review creates or changes an ordinary `BackupConfig`, after which the user owns its source scopes, filters, history, retention, and restore behavior.

## Considered Options

- Keeping a discovery-specific `HistoryMode` was rejected because it hid source history and made behavior depend on how a configuration was created. A `HistoryItem` is always one source archive; a `BackupRun` is only optional grouping metadata for a configuration-level operation.
- Treating a merged game title as persistent identity was rejected because providers and backup sets can merge for display without sharing ownership. A set is reconciled by `ProviderId + DefinitionId + SetId`, with a unique external-ID match used only to reconnect an upstream rename.
- Automatically synchronizing discovery into an existing configuration was rejected because the configuration belongs to the user. Rediscovery compares the reviewed baseline, current configuration, and new candidates; conflicts, upstream removals, and upstream narrowing preserve the user's version by default.
- Folding discovered globs into the configuration whitelist was rejected because that would leak rules across roots. Each source has an editable hard `SourceScope`; configuration filters can only narrow it.

## Consequences

- Discovery providers cannot persist `BackupConfig` instances directly, and merged game candidates are presentation only. Ludusavi uses set ID `main`; specialized providers such as MineRewind use a stable provider-owned set ID so separate instances remain separate configurations.
- `ReviewedDiscoveryBaseline` records the complete upstream snapshot shown at the end of the last review, not just applied changes. User edits do not rewrite it, so three-way comparison can distinguish upstream changes, user overrides, and simultaneous conflicts. Unapplied changes become explicit user overrides until upstream changes again.
- Steam `<storeUserId>` resources select only the most-recent account by default. Other local accounts remain visible but unselected; if no active account can be established, discovery proposes a single-segment wildcard with a cross-account warning and leaves it unselected. Rules without the placeholder are resolved once.
- The Ludusavi primary manifest may be downloaded or imported. A secondary manifest remains an explicit manual input; FolderRewind does not search game directories for `.ludusavi.yaml`, and registry resources remain visible but unsupported.
- The compiled-index generation identity includes every manifest input plus compiler and schema versions. `current.json` points to a fully validated current generation and one previous generation: generation data is written and validated before the pointer changes, current corruption falls back to previous and repairs the pointer, and two invalid generations trigger rebuilding from available inputs. HTTP 304 validates only the remote primary input and never skips local-input checks.
- Configuration-level backup creates a run context but persists a `BackupRun` only when at least one source creates a new archive. Source history remains authoritative, and retained run references protect rather than own history items.
- Because this design replaces an unpublished 1.9.0 Beta, `HistoryMode`, the old discovery-origin shape, index v2, and BackupRun v1 are deliberately invalidated without migration.
