# FolderRewind Backup Domain

FolderRewind proposes backup-worthy data, lets the user own the resulting configuration, and manages restorable source archives and operation groupings without silently changing protection boundaries.

## Discovery language

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

**Discovery Set Identity**:
The persistent identity of one provider-owned backup set, formed by `ProviderId + DefinitionId + SetId`; external IDs may reconnect a uniquely renamed definition but display titles never establish identity.
_Avoid_: Game title, merged game identity, configuration name

**Backup Resource Candidate**:
A proposed directory, file set, or unsupported registry resource that a user may choose to protect.
_Avoid_: Managed folder, backup target

**Backup Preset**:
A reusable backup policy that may reference discovery sources but does not represent an installed game or a persistent configuration.
_Avoid_: Template, game definition

**Backup Config Draft**:
A non-persistent proposal produced by combining selected resources with a backup preset.
_Avoid_: Backup config, template instance

**Reviewed Discovery Baseline**:
The complete normalized upstream source snapshot that the user last finished reviewing, including roots, source scopes, and discovery resource IDs; it is one side of three-way review and is not a copy of the current configuration.
_Avoid_: Current config, accepted changes, runtime selection

## Backup language

**Backup Config**:
The user-owned persistent definition of what this computer backs up and which policies it applies; discovery may propose changes but never remains its runtime owner.
_Avoid_: Backup preset, game definition

**Backup Source**:
A configured root directory together with its source scope.
_Avoid_: Backup resource candidate, game installation

**Source Scope**:
The user-editable hard boundary for one backup source, expressed as `All` or a validated set of relative `Include` globs; configuration filters may only reduce this boundary.
_Avoid_: Discovery selection, global whitelist, resource IDs

**Configuration Filter**:
A whitelist or blacklist applied across a configuration after each source scope, and therefore unable to expand a source's hard boundary.
_Avoid_: Source scope, discovery rule

## History language

**History Item**:
A durable record of one source archive that may be referenced by zero or more backup runs.
_Avoid_: Backup run, snapshot group

**Backup Run**:
A configuration-level grouping record for one backup operation, referring to source results and history items without owning their archives.
_Avoid_: History item, archive owner, alternative history mode

**Operation Comment**:
The comment supplied for one manual backup invocation; a configuration backup stores it on the run and newly created child history items, but never rewrites reused history items.
_Avoid_: Folder note, mutable archive comment
