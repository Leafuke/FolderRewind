# FolderRewind Plugin System v3

Plugin System v3 targets FolderRewind 1.9.0 and uses the independently versioned, BCL-only `FolderRewind.Plugin.Abstractions` 3.0 contract. The App, a plugin product, and the Plugin API have separate version lines. Plugins activate in-process, so Manifest service declarations both gate access to FolderRewind's formal Host API and disclose user impact; they are not a .NET or OS security sandbox.

The Host owns configuration, backup/restore orchestration, immutable Artifact identity and graph transactions, History, retention, Cloud ordering, integrity, progress and cancellation. Plugins may propose revision-bound Config changes, contribute policies/scopes/consistency/coordinators/commands, and—when statically declared—transform a committed core Artifact into an owned semantic Artifact plus provide its restore materializer. They do not mutate old archives from an after-backup hook or bypass Safe Restore.

Distribution uses root-manifest `.frplugin` ZIPs. Installation validates every canonical path and expanded-size bound before staging, rejects bundled Abstractions, validates Manifest/settings/PE metadata without loading assemblies or executing plugin code, stores versioned current/previous known-good payloads, and records a recovery journal. Official Catalog and Manual provenance remain distinct; newly installed plugins default to Disabled and first execute only after explicit Enable.

Runtime routing and physical cleanup are separate guarantees. Disable, replacement, rollback, and discard remove capabilities and cancel the plugin lifetime before giving `DeactivateAsync` a bounded five-second grace period. A timeout cannot retain the transition gate: the Host continues with logical isolation, reports `RequiresRestart`, and keeps the affected load context until restart. Destructive uninstall likewise uses a journal plus same-volume code/data quarantine, commits configuration atomically, and either rolls back or finishes cleanup during startup recovery.

The Host repository validates the bundled MineRewind artifact as a fixed-hash black box. MineRewind restores, builds, tests, and packs in its own repository against the public `FolderRewind.Plugin.Abstractions 3.0.0` NuGet package; neither repository uses the other's application source as a build input.

The current unpublished MineRewind 1.9.0 hardening candidate comes from MineRewind commit `5b237ff`; its bundled `.frplugin` SHA-256 is `48eb2ab4e70cbb10c92f81d036540caaa35ca42dc294e014482c919fa3852d84`. Automated hardening is complete, but the M5R release gate remains rejected until the real MineBackup/KnotLink checklist passes.

See the frozen [execution plan](../plans/FolderRewind_Plugin_System_v3_Execution_Plan.md), the [domain context](../../CONTEXT.md), and the [M5 manual test checklist](MANUAL_TEST_M5.md).
