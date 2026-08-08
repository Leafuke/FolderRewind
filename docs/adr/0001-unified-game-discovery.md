# Separate game discovery from backup presets

FolderRewind will treat game definitions, backup presets, and persistent backup configurations as separate concepts connected through a provider-based discovery layer. Ludusavi data, legacy path rules, and specialized plugins all produce candidates; only the user's final confirmation creates or updates configurations. This avoids misrepresenting a large third-party location database as curated FolderRewind presets while preserving legacy V1 templates through adapters.

## Considered Options

- Converting every Ludusavi entry into an official template was rejected because most entries contain location knowledge rather than a curated backup policy, and the legacy directory-oriented resolver cannot represent files, globs, and registry data faithfully.
- Replacing the template system outright was rejected because existing presets contain valuable archive, automation, filtering, cloud, and plugin policy.

## Consequences

- Discovery providers cannot persist `BackupConfig` instances directly.
- Full-machine discovery resolves only definitions backed by launcher, plugin, or user-supplied installation evidence; probing all uninstalled definitions is an explicit non-default workflow if introduced later.
- Discovery reports path expressions and cheap fixed-root existence only. Recursive file enumeration, counts, and sizes are deferred until user selection or backup execution.
- A game candidate may contain multiple backup-set candidates, allowing specialized providers such as MineRewind to propose one configuration per instance.
- Files and globs require source-level selection semantics in the backup engine; registry resources remain visible but unsupported until a dedicated backup representation exists.
