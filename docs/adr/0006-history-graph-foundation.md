---
status: accepted
---

# History is an immutable graph synchronized by Commit Pack union

FolderRewind models history as immutable logical facts instead of a mutable list of archive records. One atomic Commit Pack may publish a Backup Run, new Source Versions and Representations, a Configuration Checkpoint, a Branch Update, and related annotations; devices converge by set union so concurrent history is preserved instead of resolved by last-writer-wins replacement.

## Core invariants

- A Source Version is logical state. Its parents describe source lineage; Representation dependencies describe physical reconstruction and never substitute for lineage.
- Native backup creates at most one parent per Source Version and one parent per ordinary Branch Update. The schema can read multiple parents, but this phase creates neither Merge Versions nor reconciliation updates.
- A Configuration Checkpoint is a configuration state vector. A Backup Run records the attempt and outcomes but does not own payloads or determine retention.
- Branch state is derived from immutable Branch Update tips. Concurrent tips remain visible divergence until the user selects a concrete tip for checkout.
- Workspace, Local Replica Catalog, Replica Observations, and Capture Baseline Cache are device-local state. Cloud synchronization never changes Workspace or invents ancestry.
- A Version Representation identifies a materialization strategy; a Storage Replica identifies one physical copy. Missing or retired payloads do not delete the Source Version.
- Plugin Artifact reachability starts from `VersionRepresentation.ArtifactRootId`. The Artifact ledger owns immutable payload nodes and dependency integrity, but no History roots.
- Pins, Branch tips, Workspace baselines, and active operations protect materialization. Presentation suppression is not deletion, and Released materialization policy is not logical Version deletion.

## Persistence boundaries and data layout

Each configuration has one repository under `history/<encoded-config-id>/` in the configuration directory:

```text
repository.json
packs/<prefix>/<PackId>.frpack
index/history-index.db
local-state/workspace.json
local-state/replicas.json
local-state/capture-baselines/<SourceId>.json
transactions/<HistoryTransactionId>/journal.json
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

Repository journals complete device-local state after a durable Pack commit or clean up work that never reached the commit point. The index is rebuilt from Packs, conflicting or corrupt incoming Packs are quarantined, corrupt Workspace or Local Replica Catalog state fails closed until recovery, and a missing/corrupt Capture Baseline Cache merely forces the next capture to Full. Archive scan recovery may add a parentless Version but never advances a Branch.

## Scope

This foundation preserves divergence but does not implement merge-base discovery, Source Version Merge, semantic conflict resolution, or automatic Branch reconciliation. Those features must add explicit multi-parent creation rules rather than reinterpret existing single-parent facts.
