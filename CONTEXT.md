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
The persistent identity of one discovery-provider-owned backup set, formed by `DiscoveryProviderId + DefinitionId + SetId`; external IDs may reconnect a uniquely renamed definition but display titles never establish identity.
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
A durable user-visible restore point rooted at one Backup Artifact and able to reach its required Artifact dependencies.
_Avoid_: Backup run, physical archive, artifact node

**Backup Run**:
A configuration-level grouping record for one backup operation, referring to source results and history items without owning their archives.
_Avoid_: History item, archive owner, alternative history mode

**Backup Artifact**:
An immutable, Host-managed payload that contributes to reconstructing one restorable state and may depend on other Backup Artifacts.
_Avoid_: Mutable archive, history item, plugin data file

**Artifact Format**:
The stable owner-qualified identity and version that determine how a Backup Artifact can be interpreted and materialized.
_Avoid_: File extension, backup mode, plugin version

**Artifact Envelope**:
The Host-owned storage protection around a logical Backup Artifact, including encryption and integrity metadata, without changing its Artifact Format.
_Avoid_: Artifact format, plugin encryption, file extension

**Artifact Dependency**:
An explicit directed requirement stating that one Backup Artifact needs another to materialize its state.
_Avoid_: Filename link, implicit chain order, neighboring history item

**Artifact Transaction**:
The Host-owned atomic change that validates and commits staged Backup Artifacts, dependency edges, and History root references as one recoverable graph revision.
_Avoid_: Post-backup hook, in-place archive rewrite, plugin commit

**Operation Comment**:
The comment supplied for one manual backup invocation; a configuration backup stores it on the run and newly created child history items, but never rewrites reused history items.
_Avoid_: Folder note, mutable archive comment

## Plugin language

**Plugin**:
An installed extension product that contributes domain behavior to FolderRewind while the host retains ownership of configuration, execution, persistence, conflict handling, and user interaction.
_Avoid_: Add-on script, privileged core module

**Plugin ID**:
The permanent reverse-domain identity of one plugin product, independent of its name, version, installation path, or runtime state.
_Avoid_: Provider ID, display name, assembly name

**Capability**:
A single kind of domain behavior that a plugin offers through the Plugin API; a capability is selected by explicit identity and contract rather than hook order or first-claim scanning.
_Avoid_: Hook, middleware, plugin feature flag

**Config Kind**:
The stable domain classification of a backup configuration, formed by `OwnerId + KindId`; it identifies who defines the configuration's semantics without transferring ownership of the configuration away from the user.
_Avoid_: Config type label, discovery provider, plugin name

**Discovery Provider ID**:
The stable identity of a component that proposes discovery candidates; it participates in discovery-set identity but does not identify a plugin, config kind, or state namespace.
_Avoid_: Plugin ID, state owner ID, game title

**Discovery Draft Commit**:
The host-owned atomic transaction that validates plugin discovery candidates, assigns host identities, and persists selected drafts only when the user's creation policy permits it.
_Avoid_: Plugin-created config, discovery side effect, automatic provider save

**Config Change Proposal**:
A non-persistent, revision-bound set of configuration changes suggested by a plugin for Host validation, user review, and atomic commit.
_Avoid_: Plugin config write, augmentation side effect, silent synchronization

**Config Reconciliation**:
The comparison of current user-owned configuration with plugin domain knowledge to produce Config Change Proposals without applying them.
_Avoid_: Config augmentation, automatic sync, plugin migration

**State Owner ID**:
The namespace owner of opaque provider state attached to a configuration or backup source; equal text may be used for a plugin's own state, but the role is distinct from Plugin ID.
_Avoid_: Plugin ID, discovery provider ID, dictionary prefix

**Provider State**:
An opaque, versioned JSON value owned and migrated only by its State Owner while the host owns storage, validation, and atomic commit.
_Avoid_: Plugin settings, extension properties, host origin

**Provider State Location**:
The stable host-owned address of provider state, formed by `ConfigId + optional FolderId`; a missing FolderId means configuration-level state and a present FolderId means backup-source state.
_Avoid_: File path, State Owner ID, plugin data-store path

**Enabled Intent**:
The user's durable choice that an installed plugin should be active when compatible; it is not proof that the plugin is installed, loaded, or healthy and Safe Mode never rewrites it.
_Avoid_: Installed state, runtime state, global plugin switch

**Runtime State**:
The host-observed lifecycle state of a plugin session: Inactive, Activating, Active, Draining, Deactivating, or Failed.
_Avoid_: Enabled intent, installation status, operation outcome

**Operation Resolution**:
The host-owned, side-effect-free decision that combines Config Kind policy, runtime availability, selected provider scope, and consistency intent into Ready, Degraded, or Blocked plus durable diagnostics.
_Avoid_: Plugin health check, operation result, fallback exception

**Runtime Session**:
One committed activation of a plugin instance together with its registered capabilities and active operation leases.
_Avoid_: Installed plugin, enabled intent, process lifetime

**Static Plugin Validation**:
The inspection of package paths, Manifest and settings facts, and assembly metadata without loading the candidate assembly or executing constructors, module initializers, or lifecycle code.
_Avoid_: Activation smoke test, trusted execution, plugin health check

**Requested Host Service**:
A Manifest-declared capability to call one part of FolderRewind's formal Host API; the Host gates that façade and discloses the request to the user, while the declaration does not sandbox ambient .NET or operating-system access.
_Avoid_: OS permission, security sandbox, optional documentation

**Consistency Lease**:
A bounded right to read one stable backup source through difference detection and archive capture, together with the obligation to release any coordination or snapshot resources.
_Avoid_: Backup hook, source path override, archive lifetime

**Artifact Transformer**:
A plugin capability that converts staged and compatible Backup Artifacts into a proposed Artifact Transaction without changing committed artifacts itself.
_Avoid_: Post-backup hook, backup engine, archive interceptor

**Backup Completion Observer**:
A participant notified after a backup's Artifact and History root are durably committed but before final diagnostics; it may perform declared integration work while remaining read-only toward Host backup state.
_Avoid_: Artifact finalizer, transactional hook, completion owner

**Restore Mode**:
The Host-owned policy for applying materialized content to a destination, currently Clean or Overwrite; Artifact completeness may constrain the effective choice.
_Avoid_: Artifact format, restore strategy, plugin takeover

**Restore Materializer**:
A plugin capability that reconstructs a selected Artifact graph into an isolated Host workspace without applying it to the user's destination.
_Avoid_: Restore mode, restore interceptor, target mutation

**Restore Coordinator**:
The domain owner that surrounds one host-controlled restore mutation with required external preparation and finalization without taking ownership of archive resolution or file restoration.
_Avoid_: Restore interceptor, restore engine, before/after hook

**Plugin Command**:
A stable plugin-owned action identified by `PluginId + CommandId`, independent of whether it is invoked from UI, hotkey, tray, KnotLink, or another host entry point.
The descriptor may carry an optional default gesture and global/local scope; the Host owns user overrides, registration, conflict handling, and dispatch. Commands that model an “active resource” may resolve it through the Host's read-only Config Kind query, but cannot mutate configuration as part of resolution.
_Avoid_: Hotkey provider, KnotLink command handler, UI callback
