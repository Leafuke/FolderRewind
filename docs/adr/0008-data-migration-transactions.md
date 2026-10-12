---
status: accepted
---

# Metadata migration has recoverable repository commits and explicit local setup

History remains immutable Pack union with Source-owned branches (ADR 0006/0007). A transfer is not a payload backup and cannot establish that historical versions are restorable.

## Decisions

- UI exports v2 `.frhistory` bundles with explicit project selection (all selected by default). The importer also reads v1 single-project bundles. Each project commits independently; reports distinguish committed facts, configuration association and device-local initialization.
- Preserve ConfigId, SourceId and Pack bytes. An orphan waits for the original configuration; project names and path hints are not identities. Runtime cache presence does not determine association.
- New multi-Pack unions are staged in `imports/<id>/`. Before the durable union decision, failure/cancellation leaves existing facts unchanged. After it, retained staged bytes allow idempotent forward recovery. Incomplete unions block repository reads/mutations and recover before graph validation and index rebuilding.
- A normal single Commit Pack retains its existing final-file commit decision and local-state journal. A union intent must not cause capture cleanup to delete payloads for history that can later commit.
- Explicit transfer marks intentionally missing local state as initialization required. Unmarked missing state, corruption, inaccessible files and incomplete prior transactions retain recovery semantics.
- Controlled setup creates Unknown source baselines and an empty local replica catalog only where missing. Valid state survives import; changing a source path explicitly resets that source's baseline and branch continuation. Initialization intents are revision checked and replayable.
- Unknown state cannot supply incremental dependencies or ancestry. The next admitted full capture creates a distinct branch; existing Checkout/restore admission remains authoritative for resuming historical states.
- Configuration import prepares an immutable candidate. The running configuration and its save queue remain active. At restart, the old process drains its writer; the new process creates a safety copy and activates before windows, plugins, Runtime warmup or automation. Interrupted/uncommitted activation rolls back. Applied imports require per-project setup confirmation tied to that particular import.
- Configuration preserves automation/cloud switches. Versioned device-local permissions pause backup and cloud uploads independently per project; one batch confirmation releases selected ready capabilities without replaying startup backups or uploading existing archives. Config activation and its active permission registry roll back together.
- Local restore retains valid workspace/replica state when target identities, source paths and semantics match the pre-import configuration. Cross-device initialization keeps Unknown state. A mode selection is not evidence; damaged state still requires recovery.
- Batch inspection is read-only and verifies metadata, local state and connection evidence. Confirmation rechecks under the configuration-operation lease. Per-project failures/cancellation retain already completed decisions. Target cloud bindings are reused only for matching projects/connections; cloud readiness cannot block local backup.
- v1 transaction/confirmation records remain readable. Preferences erased by earlier builds are never guessed; the user can reread switches from an original export with matching project/source identities, without replacing paths, rules or credentials. History-only local initialization also retains a separate confirmation requirement.

## Consequences

An import can succeed in publishing metadata while still requiring configuration, local setup or transaction/index recovery. Those conditions are reported separately, and committed facts are never described as an uncommitted failure. The migration report always states that archives are unverified.

Payload availability checking, full payload transfer and large-bundle streaming/resource limits are outside this change. GUI acceptance is manual.
