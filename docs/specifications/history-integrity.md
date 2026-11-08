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

## Backup size statistics

Capture publishes a Host-owned `VersionMetadataSnapshot` with producer `folderrewind.core`, schema `backup-size`, version `1`, in the same Commit Pack as its Version. Its JSON payload contains nonnegative Int64 `addedStoredBytes` and `logicalContentBytes`. Plugin metadata replacement or inheritance must preserve the current capture's Host snapshot and must never inherit another Version's capture costs. Failed or canceled captures publish no such snapshot.

The optional `backupType` string records the actual capture type (`Full`, `Smart`, `Rolling`, `SemanticFull`, or `SemanticSmart`), independently of later configuration edits or Representation rewrites. Native Representation metadata may also contain `hostBackupType`. Older entries fall back to their Representation kind, legacy backup-type evidence, or the bound Artifact root's capture mode; unknown semantic modes remain generic Semantic rather than being guessed from configuration.

Representation metadata `hostStoredBytes` records the final output size; Native Artifact `hostRestoreDependencyBytes` records its complete, deduplicated closure including actual Envelope bytes. These invariant-culture integers are observations after sealing, not space-budget estimates. Chain rewriting recomputes new Representation storage facts while preserving the original Version capture snapshot. Native `nativeClosureStorageBytes` remains a capacity-budget field.

Presentation reads these facts without opening backup contents. Null/missing/unrecognized measurements mean unknown, including after old-format import; zero is a known measurement. A reused or unchanged Run Source contributes zero new backup output. Missing local payloads or remote-only availability do not erase committed statistics. Old records may acquire local, cancelable, on-demand size observations from verified archive listings or bound Native capture evidence, but these observations neither invent original Added Storage nor publish authoritative replacements. Statistics never trigger cloud downloads.

For Capture entries without recorded Added Storage, presentation falls back to the selected ordinary archive's current local file length (Full, Smart, Rolling or legacy archive). Only that archive is counted, independently of its dependencies; Native Artifact roots are excluded. This temporary display value does not publish a capture snapshot. Recorded values, including zero, take precedence; missing or unreadable files remain unknown, and reused or unchanged entries still display zero.

## Mutation and recovery

- Preserve lock order: configuration operation gate, runtime mutation/recovery gate, then local-state storage locks. Internal recovery uses its typed operation lease and must not re-enter the outer operation.
- Resolve inputs, materialize, verify integrity and establish permitted target boundaries before mutation. Exact admission requires verified physical materializability, not metadata or a filename alone.
- The restore journal owns physical rollback, any intended History publication and local-state completion. Before commit, recover only mutations with recorded ownership and execution evidence. After the intended Pack is durable, complete local state without retracting the fact.
- Run required recovery before normal mutation or GC. Conflicting ownership, damaged local state or an unresolved journal fails closed. Rebuildable indexes and disposable caches have separate repair rules.
- Ordinary restore does not switch Source Branches. Checkout and merge update only their selected Source state; a multi-source restore can share a transaction while preserving independent Source ownership.
- Replica publication follows the protocol's completed payload/integrity work. Remote absence is not deletion evidence. Replica reuse follows the selected verification policy; it must not be described as a fresh download/hash check when that was not performed.

## Compatibility

The disposable History SQLite index uses schema 5. Upgrades from valid schema 3 and 4 databases remove redundant relation tables and duplicated object columns in one transaction, retaining replica observations. Cancellation or an upgrade failure leaves the previous schema intact. Invalid caches use the existing controlled rebuild path; immutable History Pack payloads and identities are unchanged.

Released 1.8.2 fixtures define the supported direct legacy upgrade boundary. Legacy provenance with unknown source scope does not acquire deletion authority from later content verification. Archive-scan recovery cannot invent ancestry or advance a Branch. Borrowed legacy archives remain outside automatic retention deletion.

History import, configuration activation and local capability confirmation are separate durable decisions, as specified by [ADR 0008](../adr/0008-data-migration-transactions.md). Source-directory moves in the code repository do not authorize a configuration migration, storage-layout change or modification of user data.
