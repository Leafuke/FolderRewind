---
status: accepted
---

# Minecraft backup may degrade while restore fails closed

When MineRewind or KnotLink consistency is unavailable, a full Minecraft backup may continue as a raw Host backup for both manual and automatic invocations, recording a persistent `SuccessWithWarnings` diagnostic and counting as a completed automation run. Restore is deliberately stricter: if the Config Kind owner is unavailable, a provider-owned scope cannot be resolved, required consistency is missing, or BackupBeforeRestore fails, FolderRewind blocks mutation because it cannot prove that the world is safe to overwrite or preserve MineRewind semantics. Blocking all degraded backups was rejected as too harmful to protection availability, while allowing raw restore was rejected because restore is destructive and its safety cannot be reconstructed afterward.

## Consequences

- Degraded backup warnings remain visible in activity, history, and logs instead of being transient notifications.
- Selected-region backup never falls back to full backup.
- Hot restore completes archive preflight before Save & Exit, performs BackupBeforeRestore after the world exits, and invokes the Host restore continuation at most once.
