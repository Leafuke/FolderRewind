# Native Artifact integration into 1.9.x

## Approved scope (2026-10-16)

Integrate Host 920162588f2a383b8107d8360cd5effca361086b with source 8e3bdab5855810c0cec5ea07e79ab22813d42a95; integrate MineRewind 8ffb5cb85d143f35c1d3f7a6eb0d42cc473a4398 with source 19e11a7. Use independent worktrees on codex/integrate-native-artifact-1.9. Commit locally by stage; merge each local 1.9.x only after all safety gates pass. Do not push, publish NuGet, tag or release.

- Experimental capture remains disabled by default. Host owns orchestration and target mutation; plugins own domain semantics.
- Preserve officially released Core/history/workspace/migration/journal readers. Unpublished P2/P3 formats are not a compatibility promise: identify unsupported experiments, never reinterpret or automatically delete them.
- New Native namespace and evidence v2 include directories. Preserve RequiresFullCapture, ContentRealizationId and pre-capture concurrency fences.
- Unify representation consumers with explicit credentials/storage/verification contexts. Deep Native verification includes recipe reconstruction and source file evidence. Native deletion releases authorized roots and uses DAG GC, not Core chain rewriting.
- Integrate capture participants with durable History packs and existing Cloud Jobs. Recovery must repeat within the same process. Freeze transformer/materializer session capabilities for each operation.
- Use controlled plaintext workspaces and cumulative budgets. Do not enlarge 16 GiB/file/node limits. Bound object downloads, descriptors, receipts and process output.
- Cloud Job owns the frozen target and ReplicaId. New objects receive readback/hash verification; existing objects receive closure/receipt/size availability checks. Same-size external corruption is detected by explicit deep verify/restore, not falsely claimed as freshly verified on normal reuse.
- Native migration has a distinct payload type and virtual paths; candidate credentials remain operation-local. Recheck closure evidence before granting recovery.
- API 3.10 remains BCL-only and assembly identity 3.0.0.0. Host owns directory evidence; do not expand file-view API. Commit a source-hash-versioned contract nupkg with provenance/SHA into the independent plugin repository. Never bundle its DLL into frplugin. MineRewind version becomes 1.9.9.
- Existing app data/worlds may be read to select samples; all writes, restores and crash experiments use isolated copies. Real cloud/multi-device/channel acceptance is deferred to release.

## Stages and progress

| Stage | Deliverable | Status |
|---|---|---|
| M0 | Freeze refs/tools/samples, baseline checks, tracked decisions | Complete |
| M1 | Merge full source histories; preserve target architecture; build/Core checks | Complete (integration foundation only) |
| M2 | Logical tree/evidence, commit/restore consumers, Deep/readiness/deletion | Local contract implemented; production acceptance pending |
| M3 | Budgets, secure workspaces, bounded I/O, leases, recovery/GC | Session/recovery implemented; resource work pending |
| M4 | Native payload protocol throughout Cloud Job lifecycle | Pending |
| M5 | Mixed migration, candidate passwords, isolated recovery/evidence | Pending |
| M6 | Saved snapshot/checkpoint identity, warnings, stable version protocol, cleanup | Pending |
| M7 | Contract snapshot/plugin package/CI, end-to-end and final local merges | Pending |

Every stage requires relevant checks and a Conventional Commit. A buildable merge foundation is an intermediate integration commit, not release acceptance.

## Findings disposition

R01 consumers: one factory with isolated contexts. R02 remote-only: explicit PreparationRequired and correct root binding. R03 directories: evidence v2 plus directory-aware change detection. R04 Deep: reconstruct bytes, retain operation leases. R05 cloud reuse: approved availability/receipt policy, explicit deep evidence separate. R06 migration: Native transport type, virtual paths, closure evidence and candidate credentials. R07 budgets: symmetric closure limits and streaming bounds. R08 plaintext: Host-owned ACL workspaces. R09 upgrade: operation-scoped capability bundle. R10 failures: independent orphan collection and repeatable recovery. R11 GC: unified cleanup guard, one configuration-wide pass and physical accounting. R12 filenames: VersionId identity and reject ambiguous legacy names. R13 settings/warnings: actual saved snapshot, revision/SourceIds handoff and shared diagnostics. R14 delivery: exact contract snapshot, both plugin suites and clean payload inventory.

Remove unpublished paired P2 production writing/state and unreachable archive-first P3 conversion after replacement checks. Preserve independent CoreFull fallback and the Full/checkpoint codec used by the new path. Defer automatic compaction, P4/P5, shared remote blob physical GC, random-access storage, broad framework/UI changes and unrelated dependencies.

## Acceptance gates

- Sequential Host, Runtime, Abstractions, MineRewind adapter and World suites; real 7z/rclone and crash probe; no shared bin/obj concurrent builds.
- Closed-feature regression: Full/Smart/Rolling, Balanced/Strict, ordinary/Quick/SafetySnapshot, same-volume recovery, migration and persistent Cloud Jobs.
- Production-entry tests: preview/restore/export/Checkout/cleanup/delete; directory-only changes and path type swaps; metadata/cache loss and capture after restore.
- At least 30 versions with checkpoint/retention/failure/cancellation; bounded fixtures test node limits. Crash at graph/pack/local state/object/descriptor/manifest/fact boundaries; repeated recovery is idempotent.
- Cloud: two targets, changed target/retry, missing blob/receipt, same-size corruption under the approved evidence policy, cancel/abandon and remote-only recovery.
- Migration: Core/Native/mixed, encrypted/unencrypted, wrong credentials, path rebinding, restart and dependencies changed before authorization.
- Final exact frplugin: clean restore/build, no Abstractions DLL, upgrade/activation/capture/restore. Winapp scripted UI plus screenshot inspection in Sandbox when available, otherwise isolated MSI test distribution.
- Performance evidence compares current 1.9.x under equal conditions; report medians/maxima/process/space. Original P3 strict latency exceptions remain, while integrity/recovery/resource bounds remain hard gates.

## Evidence

Current implementation and check results are recorded here as work proceeds. Target branches remain unchanged until every hard gate passes. Final merge order: plugin first, Host second. Remote cloud, multi-device and release-channel checks remain explicit release prerequisites.

M0: SDK 10.0.402; WinApp 0.7.1; pinned real 7z, rclone v1.75.1 and RestoreProtocolProbe prepared. Host Core/restore/cloud-related baseline: 136 passed, zero skipped/failed. Independent plugin adapter baseline: 93 passed, zero skipped/failed. Clean public-only plugin restore exposed the existing unavailable 3.9.0 contract dependency; baseline was restored against a package built from the frozen target contract in an isolated feed/cache. This is evidence for the approved pinned contract snapshot, not a public-package publication claim. Raw TRX stays under each worktree's ignored artifacts/integration/baseline.

M1: resolved all 12 conflict files. Preserved target Cloud admission/jobs and migration continuation while composing reserved VersionId, Artifact participants and ContentRealizationId. WinUI x64 Debug solution build passed (one pre-existing MSTEST0037 warning); Host focused regression 79 passed, Runtime 159 passed, Abstractions 15 passed, all zero skipped/failed. Independent source merge: adapter 93 and World 74 passed using an isolated contract candidate. These checks establish a buildable foundation only: Native Cloud Job dispatch and the other M2-M7 gaps remain open; target 1.9.x refs have not changed.

M2 local-contract checkpoint: introduced a separate native-artifacts-v2 namespace and Host evidence v2 (canonical directories, parent/type/case collision checks, directory fingerprint/cache/change detection). Preserved official cache v2 readability while discarding experimental v3 caches. Unified local restore/export/deletion consumers around the operation codec; Deep and restore independently hash reconstructed source bytes and verify evidence identity. Unsupported experimental representations fail explicitly while Core alternatives remain selectable; remote-only Native reports PreparationRequired when an eligible shared replica exists.

Native deletion now uses a separate prepared authorization/catalog transaction and DAG collection. It rechecks Workspace, pins, branches, Safety Snapshots, Cloud Jobs, Merge Sessions and preparation fingerprints, commits History/catalog before collection, and preserves shared nodes. Configuration cleanup runs Native GC once after Core groups, accounts actual payload bytes and object counts, and collects orphans even with KeepCount disabled. Existing Core chain rewriting remains intact. Review found no new XAML/API/MVVM issues; deletion confirmation and Native object reporting use both resource languages.

Validation: full WinUI x64 Debug solution build passed (one existing MSTEST0037 warning). Host contract/restore/Checkout/Core-chain/cleanup regression: 174 passed, zero skipped/failed, with real 7z and RestoreProtocolProbe. After final inventory validation/report edits, 33 focused Host tests passed; Runtime 159 and Abstractions 15 passed. TRX: ignored artifacts/integration/m2. The initial no-probe run with skipped process tests is not acceptance evidence; the verified run enabled the probe. Production plugin entrypoint acceptance and secure resources/session/Cloud/migration contexts are still open in M3-M7; this checkpoint is not release acceptance.

M3 session/recovery checkpoint: added an operation lease that atomically pins the session manifest and capability set. Native capture acquires it before source hashing and uses the same session for transform, materializer selection, validation and view/stream disposal; upgrades drain without rejecting this operation's later capability selections. Native read/Deep uses the same pattern. Removed success-cached RecoverOnce; Native journal replay now runs repeatedly inside the existing configuration/History recovery gates. Pack-durable participant failures end only in-memory ownership, retaining persistent roots for repeatable recovery. Deletion/export also enter the common recovery path before mutation.

Validation: Runtime 160 passed (including replacement-drain/session identity), Host focused 65 passed (including durable participant failure and two consecutive in-process recovery scans), zero skipped/failed. WinUI solution build passed after the recovery wiring; resource quotas, private workspaces, bounded transfers/process output and durable physical-GC cleanup remain M3 work, not waived gates.

M3 bounded-I/O checkpoint: Native capture/export/import/read now share the unchanged 16 GiB closure limits for both logical and storage bytes. Downloads carry the expected object length and one cumulative transfer/readback budget. rclone object downloads use bounded stdout streaming, remove only files created by the current attempt, and reap the process on failure. Receipt reads are limited to 4 KiB; new receipt publication is checked. Normal reuse still provides receipt/size availability evidence, as approved. 7z stdout/stderr and rclone diagnostic streams are bounded and terminate the writer immediately on overflow.

Artifact descriptors, ledgers, intents, envelope metadata, ZIP descriptors and Host evidence have bounded reads. Ledger metadata is checked before atomic replacement; oversized writes fail before publication. An invalid Native directory cache is discarded to permit a self-contained capture. Enabling the acceptance build found an older test transport missing the target branch's manifest-read contract; it now delegates that contract. Both normal/acceptance build paths remain available.

Validation: opt-in NativeArtifactAcceptance WinUI x64 Debug solution build passed; Runtime 161 tests passed; 35 Host resource/Native contract/real-rclone/real-7z tests passed, zero skipped/failed. Tests explicitly cover exact/cumulative lengths, same-size corruption under availability-only reuse, short/oversized transfers, preserving occupied targets, oversized receipts, process-output termination and symmetric logical/storage closure limits. Private plaintext workspace ownership, per-volume temporary-space reservations, cumulative decoded file limits and durable physical-GC replay are still open M3 gates. Cloud Job and migration payload wiring remain M4/M5; no final acceptance or merge-back is claimed.

M3 private-workspace/space checkpoint: plaintext Artifact staging, captures and decoded reads now use protected user-owned ACLs and reject reparse ancestors. Deletion checks ownership and aliases. Stable plugin snapshots use private, locked process containers; startup reclaims only inactive proven owners and keeps unknown legacy directories. Initialization runs off the UI thread and failed initialization can retry.

Temporary writes reserve shared outstanding capacity by actual volume (including mount/UNC identity on Windows), with separate reservations for packing, decoding, ZIP transfer staging, object downloads and restored source trees. Tiny deltas no longer require 16 GiB of free space solely because that is the maximum input quota. Decoders inspect declared node bytes and share a closure-wide entry/byte budget before extraction. New Native nodes include Host-derived logical entry counts; inherited closures and import/export use the same limits, including canonical envelope metadata within storage limits. Import verifies all nodes before publication and avoids the former redundant full decode after commit. Public plugin file-view API is unchanged; the older generic Artifact ledger still accepts absent entry counts, while the new Native namespace requires them.

Validation: Runtime 165 passed, including owner ACLs, live/stale snapshot owners, same-volume aliases, concurrent reservations and cumulative decoded quotas. Host focused 20 passed with real 7z/rclone, including declared-byte and shared-entry rejection before output creation. Acceptance-mode WinUI x64 Debug solution build passed (one existing MSTEST0037 warning). GC physical replay and repair interruption safety remain open M3/M4 items; the final production sample matrix will also audit stable-snapshot space costs and Core behavior.
