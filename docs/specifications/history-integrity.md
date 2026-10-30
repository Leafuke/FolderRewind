# History integrity and recovery

The ownership decision is recorded in [ADR 0007](../adr/0007-source-owned-history-branches.md); [ADR 0006](../adr/0006-history-graph-foundation.md) explains immutable history. This specification describes current Source-owned history, not the superseded configuration-wide checkpoint model.

## Identities and authority

- A Source Version is immutable content state. Version parents describe content ancestry; Representation dependencies describe physical reconstruction.
- A Source Checkpoint belongs to exactly one Source and references its Version, effective boundary and checkpoint parents. Checkpoint parents describe history continuation.
- Each Source owns its Branches. Updates consume tips only within the same Branch; merge provenance can reference another Branch without moving its tip.
- A Backup Run records one invocation and participating Source outcomes. It owns neither payloads nor retention policy. Merge does not invent a Backup Run.
- A Representation materializes a Version; a Replica identifies one physical copy. Missing or retired bytes do not delete the logical Version.
- A durable Commit Pack publishes shared facts atomically. Devices converge by immutable Pack union, not replacement of a mutable history snapshot. Later local-state or cleanup failure cannot retract a durable commit.

## Shared and local storage

The repository is under the configuration directory at `history/<encoded-config-id>/`. GUID ConfigIds use lowercase N format; other identities use `c-` followed by their UTF-8 SHA-256. Shared descriptors and Packs are distinct from rebuildable indexes and device-local state.

```text
repository.json
packs/<first-two-id-characters>/<PackId>.frpack
index/
local-state/
transactions/
quarantine/
```

Workspace Source state carries `BaseVersionId`, boundary/baseline evidence, `ActiveBranchId`, `ActiveBranchUpdateId` and `CheckpointAncestryAnchorId`. Cloud synchronization does not change those local fields. The Local Replica Catalog and observations describe this device; capture-baseline caches are disposable and cannot become authority when missing or corrupt.

Pins, Branch tips, Workspace baselines, active operations and unfinished durable merge sessions protect recoverable materialization. Presentation suppression is not physical deletion. Artifact reachability begins at Representation Artifact roots; the Artifact ledger owns its dependency graph, not History roots.

## Mutation and recovery

- Preserve lock order: configuration operation gate, runtime mutation/recovery gate, then local-state storage locks. Internal recovery uses its typed operation lease and must not re-enter the outer operation.
- Resolve inputs, materialize, verify integrity and establish permitted target boundaries before mutation. Exact admission requires verified physical materializability, not metadata or a filename alone.
- The restore journal owns physical rollback, any intended History publication and local-state completion. Before commit, recover only mutations with recorded ownership and execution evidence. After the intended Pack is durable, complete local state without retracting the fact.
- Run required recovery before normal mutation or GC. Conflicting ownership, damaged local state or an unresolved journal fails closed. Rebuildable indexes and disposable caches have separate repair rules.
- Ordinary restore does not switch Source Branches. Checkout and merge update only their selected Source state; a multi-source restore can share a transaction while preserving independent Source ownership.
- Replica publication follows the protocol's completed payload/integrity work. Remote absence is not deletion evidence. Replica reuse follows the selected verification policy; it must not be described as a fresh download/hash check when that was not performed.

## Compatibility

Released 1.8.2 fixtures define the supported direct legacy upgrade boundary. Legacy provenance with unknown source scope does not acquire deletion authority from later content verification. Archive-scan recovery cannot invent ancestry or advance a Branch. Borrowed legacy archives remain outside automatic retention deletion.

History import, configuration activation and local capability confirmation are separate durable decisions, as specified by [ADR 0008](../adr/0008-data-migration-transactions.md). Source-directory moves in the code repository do not authorize a configuration migration, storage-layout change or modification of user data.
