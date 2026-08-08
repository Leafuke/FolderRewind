# FolderRewind Backup Domain

FolderRewind discovers backup-worthy data, applies reusable backup policies, and manages restorable snapshots without silently deciding what a user should protect.

## Language

**Game Definition**:
Catalog knowledge that identifies a game and describes where its backup-worthy data may exist. It does not assert that the game is installed on this computer.
_Avoid_: Game template, installed game

**Game Installation**:
Evidence that a game is installed through a particular store or at a particular location on this computer.
_Avoid_: Game definition, backup source

**Discovery Provider**:
A component that reports game, installation, backup-set, and resource candidates without creating persistent backup configurations.
Default machine discovery requires explicit installation evidence and must not recursively enumerate candidate resources for counts or sizes.
_Avoid_: Template resolver, config factory

**Discovered Game Candidate**:
A proposed game identity assembled from one or more providers and one or more installations.
_Avoid_: Backup config, detected template

**Backup Set Candidate**:
A proposed configuration boundary containing backup-resource candidates that should be managed together.
_Avoid_: Game candidate, source folder

**Backup Resource Candidate**:
A proposed directory, file set, or unsupported registry resource that a user may choose to protect.
_Avoid_: Managed folder, backup target

**Backup Preset**:
A reusable backup policy that may reference discovery sources but does not represent an installed game or a persistent configuration.
_Avoid_: Template, game definition

**Backup Config Draft**:
A non-persistent proposal produced by combining selected resources with a backup preset.
_Avoid_: Backup config, template instance

**Backup Config**:
The persistent definition of what this computer backs up and which policies it applies.
_Avoid_: Backup preset, game definition

**Backup Source**:
A configured root directory together with the maximum file selection that backups may include.
_Avoid_: Backup resource candidate, game installation

**Backup Run**:
A logical configuration-level snapshot that refers to the per-source archives representing the same backup operation.
_Avoid_: History item, archive

**History Item**:
A durable record of one source archive that may be referenced by zero or more backup runs.
_Avoid_: Backup run, snapshot group
