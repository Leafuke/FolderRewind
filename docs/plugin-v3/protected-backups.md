# Protected backup protocol (2026-10-03)

Versions and protocol framing are unchanged. Source: the actual FolderRewind checkout, not the MineBackup reference mirror.

- `MARK_IMPORTANT`: existing config/folder (or plugin current-save selector), `file`, optional `important=true|false`. Successful responses now include `file` and lowercase `important` as well as the message. Repeating the same flag succeeds.
- `GET_IMPORTANCE`: same target and `file`; returns `file` and lowercase `important`. Missing entries return an error. This queries Version Pin, not BackupRun importance or restore readiness.
- `BACKUP`: new optional `protect=true|false`, default false. It is not supported on `BACKUP_ALL` or `AUTO_BACKUP`.

Protected creation requires one complete source with unrestricted effective scope and no filter rules after inherited configuration and request overrides are resolved. Ordinary backups retain their existing scope behavior. Capture facts and Pin=true are committed together in one immutable history pack. Reused/no-change captures resolve through their actual result or reliable workspace baseline, require a deep Ready Exact assessment, and preserve the original version comment/name. They do not manufacture another archive.

After the commit, normal retention sees the Pin before cleanup. Success is reported only after a Ready Exact result, persisted Pin and filename can be confirmed. The final `command_completed` carries `result=created|reused`, `file`, `important=true`. Intermediate source-success signals are not protection acknowledgements. Recovery-required or unconfirmed states never receive this success receipt; callers must not retry automatically. A confirmed Pin is not a promise that the physical archive filename never changes.

MineRewind includes the `GET_IMPORTANCE` current-save selector. Use the companion plugin package with the companion desktop build; version numbers remain unchanged. Current filename ambiguity and compaction behavior are intentionally not modified.

## Manual verification (not performed automatically)

1. Use an isolated game instance/world, the companion desktop build and MineRewind plugin, and an updated MineBackup 3.4.0 JAR. Keep an independent world backup.
2. Create a full unfiltered protected backup with a recognizable comment. Correlate the final receipt by request ID; confirm `file`, `important=true`, `result=created`.
3. Query that filename. Mark true twice, query true, mark false, query false, then mark true again. An unknown filename must return an error, not false. Restart FolderRewind and query again.
4. For deterministic no-change testing, use a stopped/unmodified disposable source with smart mode. Record again with another comment: require `reused`, the old filename and unchanged version comment.
5. Set a low retention count, create further changed backups, and verify the pinned historical version still has a recoverable closure. It may use a replacement archive; re-list before restoring if its filename changed. Restore only in the disposable instance and compare files/world state.
6. Test local inherited filters and explicit region/whitelist rules: protected creation must reject; ordinary backup remains available through its normal API.
7. Test offline backend, old capability manifests, cancellation, and recovery-required faults. Never treat a timeout or ordinary source-success event as protected completion.
