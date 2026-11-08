---
status: accepted
---

# Minecraft backup may degrade while restore fails closed

When MineRewind or KnotLink consistency is unavailable, a full Minecraft backup may continue as a raw Host backup for both manual and automatic invocations, recording a persistent `SuccessWithWarnings` diagnostic and counting as a completed automation run. Restore is deliberately stricter: if the Config Kind owner is unavailable, a provider-owned scope cannot be resolved, required consistency is missing, or BackupBeforeRestore fails, FolderRewind blocks mutation because it cannot prove that the world is safe to overwrite or preserve MineRewind semantics. Blocking all degraded backups was rejected as too harmful to protection availability, while allowing raw restore was rejected because restore is destructive and its safety cannot be reconstructed afterward.

## Consequences

- Degraded backup warnings remain visible in activity, history, and logs instead of being transient notifications.
- Invocation source intentionally does not convert an otherwise permitted full-backup degradation into `Blocked`; Automation records success with warnings and does not retry it.
- Selected-region backup never falls back to full backup.
- Hot restore completes archive preflight before Save & Exit, performs BackupBeforeRestore after the world exits, and invokes the Host restore continuation at most once.

## API 3.18 coordination rules

An incomplete world observation remains unresolved after projection onto an effective boundary, even when the reported failure belongs to an excluded subtree or diagnostic truncation hides it. This conservative policy can block `Require` because of an unrelated unreadable or over-budget directory; scope-aware traversal is deferred. A complete observation with exactly one relevant online world may coordinate that world alongside offline worlds, without re-resolving the unfiltered Source during protocol acquisition.

Internal restore safety snapshots reuse the outer restore's registered read-only check, configuration lease and affected Source identities. They recheck at capture entry and before each Source read, retain the final pre-mutation check, and never start or complete a separate backup protocol session. A caller already holding a configuration operation may only borrow an existing channel without waiting. A world or coordination change during restore preparation blocks the restore before target mutation and asks the user to retry; it does not automatically restart preparation. An already committed valid safety snapshot remains a History fact.

`Prefer` is the default intent: unavailable coordination, unresolved world evidence and an expired local collaboration window permit raw capture with persistent warnings. `Require` blocks uncoordinated capture for an applicable online or unresolved world, while ordinary directories and worlds whose locks are released remain available. Read failures, integrity failures and unresolved region selections never become successful degradation. A standalone region directory or `.mca` file does not establish a world identity. World-relative runtime exclusions must not exclude a same-named ordinary file outside a recognized world.

The optional `IBackupRunCoordinationCapability` observes a Host operation and its actual Sources, physical bindings and read-only effective boundaries. Creation sends no protocol messages. Source leases retain their original input paths and do not assert stable copies. The Host owns one batch session across Sources and Native fallback, holding its Runtime lease until authoritative terminal dispatch and session completion. Old capabilities retain their existing path. API 3.18 additions preserve the SDK assembly identity `3.0.0.0`.

The shared KnotLink channel is admitted before a configuration operation lock. A change that requires admission releases the configuration operation and retries preparation. Restore, checkout and merge apply use the same channel; their internal safety captures borrow outer ownership and must not finish a parent session. Necessary handshake replies bypass terminal queuing. A bounded terminal queue serializes messages that can unfreeze the mod, including unrelated operations and idle sends, so they cannot enter another Host run's freeze window. Terminal cleanup uses one independent five-second budget and propagates cancellation into the transport. Failed or cancelled sends remain quarantined until explicit idle reset.

Online restore coordinators await the Host's `AdmitKnotLinkChannelAsync` callback before taking their own handshake/save/restore protocol locks. Waiting for channel admission while holding a handshake lock could otherwise deadlock a backup that already owns the channel and still needs that handshake lock. Cancellation before admission sends no restore terminal because no restore protocol session has begun.

Restore coordinators can register one read-only mutation safety check before continuation. Core invokes it after configuration/history admission, materialization and safety snapshots, immediately before its first destination change in either mutation backend. The check re-observes every affected world and its lock; it cannot send KnotLink requests or start nested Host mutations while the configuration lock is held. This closes the preparation/waiting gap without changing transaction facts or making offline analysis/export depend on game exit.

The Host's 150-second collaboration window begins immediately before sending `pre_hot_backup`; acknowledgements and fallback do not restart it. It is a local deadline, not a remotely verified lease. Once all necessary inputs for that world are sealed, or its last live read ends, expiry cannot cancel History commit. During capture expiry, `Prefer` continues the same input with revoked coordination assurance; `Require` rejects its output. History outcome and whether a new artifact was created remain independent of terminal delivery. No-change completion sends a truthful successful terminal after History decides the result; unavailable and recovery-pending outcomes do not become ordinary success.

Capture cancellation is attached to the corresponding Source lease. A world deadline does not cancel ordinary/offline Sources in the same batch; a user cancellation still cancels the batch. Source-specific diagnostics remain associated with that Source.

Capture diagnostics are part of the immutable `BackupRun`, including no-change runs. Terminal completion happens after that commit: its diagnostics are persisted in the existing notification record linked by `RunId`, and in logs, without rewriting a committed Run or an old Version. Shutdown waits for coordination owners, restore interactions and terminal dispatch in addition to visible task completion.

An uncertain acknowledgement wait, active transport disconnect or failed critical terminal sets `NeedsReset`. Further online collaboration is disabled; raw `Prefer` backups and safe offline work remain available. An idle reset performs bounded pending-message cleanup and clears only local state. Settings explains that resetting restores compatibility with the existing protocol and cannot establish ownership of historical remote acknowledgements. No remote save acknowledgement, exactly-once delivery or verified remote unfreeze is inferred. Correlated, replay-resistant acknowledgements require a future two-ended protocol upgrade; this change does not alter the mod or KnotLink server.
