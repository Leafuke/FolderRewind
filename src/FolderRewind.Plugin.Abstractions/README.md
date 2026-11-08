# FolderRewind Plugin Abstractions

Public, BCL-only contracts for FolderRewind Plugin API 3. Plugins reference this package, never the application, Core or Runtime projects.

The target framework is `net10.0`. The package version is declared by its project; assembly identity remains `FolderRewind.Plugin.Abstractions, Version=3.0.0.0` throughout API 3.x. A Host accepts a matching API major and a requested minor no newer than its own. Source development versions do not imply that a package is already publicly available.

API 3.18 adds optional backup-run coordination, idle recovery status/reset, and a Host-supplied restore output byte budget. A run observes only its requested Sources and effective boundaries; source leases retain the physical input path. Capture cancellation ends before History commit, while run ownership lasts through authoritative terminal submission and idempotent completion. Terminal submission does not acknowledge remote consumption. Legacy `IBackupConsistencyCapability` implementations remain supported.

Restore coordinators may register one read-only mutation safety check before continuation. The Host invokes it after waiting/preparation and before changing the destination; validation must never send protocol requests or start nested mutations.

API 3.18 also requires every spatial preview provider to implement `ISpatialPreviewDemandCapability`. Each view submits a complete priority-ordered demand with a stable non-empty `ViewId` and increasing positive `Revision` before rendering. Tile requests carry that identity and revision. Older revisions are ignored; identical revisions are idempotent, conflicting contents are rejected. An empty demand releases the view. A generation admits at most eight view identities and 256 distinct tiles across views. Single-call cancellation does not cancel work shared with other calls or views. Closing a generation releases all demand and rejects late calls; there is no legacy rendering branch. Navigation can attach localized diagnostics through its additive `Diagnostics` property. See the [demand protocol](../../docs/specifications/spatial-preview-demand.md) for lifecycle and budget invariants.

Plugins register declared capabilities during activation. The Host owns configuration persistence, operations, history, transactions, integrity, encryption, retention, cloud ordering and target writes. In-process capability declarations gate formal Host services and disclose intent; they do not sandbox arbitrary .NET or OS access.

Distribute root-manifest `.frplugin` packages without the Host Abstractions DLL. See the [plugin guide](https://folderrewind.top/en/docs/plugins/overview) for capability, lifecycle, settings and packaging documentation. Public API changes must pass the contract baseline tests.
