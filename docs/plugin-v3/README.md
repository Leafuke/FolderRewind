# FolderRewind Plugin System v3

Plugin System v3 targets FolderRewind 1.9.0 and uses the BCL-only `FolderRewind.Plugin.Abstractions` 3.0 contract. Plugins activate in-process, so Manifest service declarations communicate compatibility and user impact; they are not a security sandbox.

The Host owns configuration, backup/restore orchestration, immutable Artifact identity and graph transactions, History, retention, Cloud ordering, integrity, progress and cancellation. Plugins may propose revision-bound Config changes, contribute policies/scopes/consistency/coordinators/commands, and—when statically declared—transform a committed core Artifact into an owned semantic Artifact plus provide its restore materializer. They do not mutate old archives from an after-backup hook or bypass Safe Restore.

Distribution uses root-manifest `.frplugin` ZIPs. Installation validates every canonical path and expanded-size bound before staging, rejects bundled Abstractions, stores versioned current/previous known-good payloads, and records a recovery journal. Official Catalog and Manual provenance remain distinct; newly installed plugins default to Disabled.

See the frozen [execution plan](../plans/FolderRewind_Plugin_System_v3_Execution_Plan.md), the [domain context](../../CONTEXT.md), and the [M5 manual test checklist](MANUAL_TEST_M5.md).
