---
status: accepted
---

# History is an immutable graph synchronized by Commit Pack union

FolderRewind models history as immutable logical facts instead of a mutable list of archive records. One atomic Commit Pack may publish a Backup Run, new Source Versions and Representations, a Configuration Checkpoint, a Branch Update, and related annotations; devices converge by set union so concurrent history is preserved instead of resolved by last-writer-wins replacement.

## Core invariants

- A Source Version is logical state. Its parents describe source lineage; Representation dependencies describe physical reconstruction and never substitute for lineage.
- Native capture creates at most one semantic parent per Source Version. A Merge Version may have the two distinct input Version parents, is a self-contained Exact state, and is not attributed to a Backup Run.
- A Configuration Checkpoint is a configuration state vector with its own semantic parents. Ordinary checkpoints have at most one parent; a three-way Merge checkpoint has the fixed target/source checkpoint pair. These edges are not Branch causality or Representation dependencies.
- A Backup Run records an attempt and outcomes but does not own payloads or determine retention. Merge publishes no synthetic Backup Run.
- Branch state is derived from immutable Branch Update tips. A child consumes a tip only within the same Branch; a Merge Update may cite a source-Branch parent for causality without moving or consuming the source tip.
- Workspace, Local Replica Catalog, Replica Observations, and Capture Baseline Cache are device-local state. Cloud synchronization never changes Workspace or invents ancestry.
- `HistoryWorkspace.CheckpointAncestryAnchorId` identifies where the next configuration-state commit continues. Source restore preserves it, checkout changes it to the selected checkpoint, safety snapshot advances only the local anchor/baselines, and Merge changes it to the Merge result.
- A Version Representation identifies a materialization strategy; a Storage Replica identifies one physical copy. Missing or retired payloads do not delete the Source Version.
- Plugin Artifact reachability starts from `VersionRepresentation.ArtifactRootId`. The Artifact ledger owns immutable payload nodes and dependency integrity, but no History roots.
- Pins, Branch tips, Workspace baselines, active operations, and unfinished durable Merge Sessions protect materialization. Presentation suppression is not deletion, and Released materialization policy is not logical Version deletion.

## Persistence boundaries and data layout

Each configuration has one repository under `history/<encoded-config-id>/` in the configuration directory:

```text
repository.json
packs/<prefix>/<PackId>.frpack
index/history-index.db
local-state/workspace.json
local-state/replicas.json
local-state/capture-baselines/<SourceId>.json
local-state/merge-sessions/sessions.db
local-state/merge-sessions/<SessionId>/
transactions/<HistoryTransactionId>/restore-journal.json
quarantine/
```

`packs/` and `repository.json` are shared authority. The SQLite index is rebuildable; `local-state/` is durable only for this device; capture baselines are disposable. Backup payload paths remain physical realizations referenced through Representations and the Local Replica Catalog rather than identities derived from filenames.

Cloud keeps metadata and payload transport separate:

```text
<remote-base>/_folderrewind/history/<encoded-config-id>/repository.json
<remote-base>/_folderrewind/history/<encoded-config-id>/packs/<prefix>/<PackId>.frpack
<remote-base>/_folderrewind/replicas/<ReplicaId>/payload
<remote-base>/_folderrewind/replicas/<ReplicaId>/manifest.json
```

Metadata synchronization is Commit Pack set union. Replica upload is physical-first and metadata-last: a shared Active replica fact is committed only after its payload and immutable manifest verify successfully. Local paths and observations never enter Cloud packs, and absence from a remote listing never implies deletion.

## Legacy migration and recovery

Only released 1.8.x history and Smart metadata are accepted as Legacy inputs. Migration assigns deterministic identities, imports old restore points as parentless Source Versions, validates a complete staging repository, atomically installs it, and then persists the repository binding; once bound, normal runtime does not read Legacy metadata.

The shared workspace-mutation journal owns filesystem rollback, optional Commit Pack intent, Workspace and Local Replica Catalog completion. Before Pack durability, recovery rolls back only Sources whose mutation was recorded as started. After the exact intended Pack is durable, recovery completes local state and never retracts immutable facts. Startup recovery runs before ordinary mutation or GC; an unresolved journal fails closed. The index is rebuilt from Packs, conflicting or corrupt incoming Packs are quarantined, corrupt Workspace or Local Replica Catalog state fails closed until recovery, and a missing/corrupt Capture Baseline Cache merely forces the next capture to Full. Archive scan recovery may add a parentless Version but never advances a Branch.

## Scope

FolderRewind 1.9 adds Configuration Checkpoint merge-base discovery, no-op/fast-forward-like classification, a conservative generic file-level three-way provider, durable conflict Sessions, manual whole-side/file resolutions, and one configuration-level atomic Apply. Merge provenance records explicit target/source roles; parent order is never used to infer them.

The first release deliberately has no recursive/virtual merge base, rename detection, automatic text diff3, or Minecraft region/chunk/NBT semantics. MineRewind uses the same file-level provider as ordinary folders and only contributes environment coordination. Reconciliation remains a same-Branch convergence operation and must not be used to encode a true Branch Merge.
