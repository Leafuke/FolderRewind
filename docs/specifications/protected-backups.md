# Protected backup protocol (2026-10-03)

Versions and protocol framing are unchanged. Source: the actual FolderRewind checkout, not the MineBackup reference mirror.

- `MARK_IMPORTANT`: existing config/folder (or plugin current-save selector), `file`, optional `important=true|false`. Successful responses now include `file` and lowercase `important` as well as the message. Repeating the same flag succeeds.
- `GET_IMPORTANCE`: same target and `file`; returns `file` and lowercase `important`. Missing entries return an error. This queries Version Pin, not BackupRun importance or restore readiness.
- `BACKUP`: new optional `protect=true|false`, default false. It is not supported on `BACKUP_ALL` or `AUTO_BACKUP`.

Protected creation requires one complete source with unrestricted effective scope and no filter rules after inherited configuration and request overrides are resolved. Ordinary backups retain their existing scope behavior. Capture facts and Pin=true are committed together in one immutable history pack. Reused/no-change captures resolve through their actual result or reliable workspace baseline, require a deep Ready Exact assessment, and preserve the original version comment/name. They do not manufacture another archive.

After the commit, normal retention sees the Pin before cleanup. Success is reported only after a Ready Exact result, persisted Pin and filename can be confirmed. The final `command_completed` carries `result=created|reused`, `file`, `important=true`. Intermediate source-success signals are not protection acknowledgements. Recovery-required or unconfirmed states never receive this success receipt; callers must not retry automatically. A confirmed Pin is not a promise that the physical archive filename never changes.

MineRewind includes the `GET_IMPORTANCE` current-save selector. Use the companion plugin package with the companion desktop build; version numbers remain unchanged. Current filename ambiguity and compaction behavior are intentionally not modified.
