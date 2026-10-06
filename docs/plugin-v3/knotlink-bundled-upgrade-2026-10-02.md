# KnotLink current-world upgrade incident — 2026-10-02

## Cause and evidence

The valid request `cmd=LIST_BACKUPS;current_save=true` failed with `knotlink.current_save_unavailable` after the Host changed current-world commands to use `IKnotLinkTargetResolver`. MineRewind 1.9.2 exposes `IKnotLinkIntegrationCapability` but does not implement this new resolver. The Host skipped it before querying any world or history.

Replacing the bundled asset with 1.9.3 did not update an existing official v3 installation. `PluginV3OfflineUpgradeService` implements v2 migration: it returns early for installed v3 packages or completed/suppressed migration state and installs the bundled package only when no v3 package is installed. The release therefore omitted the upgrade path required by the new command handler. The previous verification used the new plugin and fixtures; it did not validate upgrading an existing official installation.

Read-only inspection of the supplied LocalState directory and release payloads established:

- Plugin initialization completed before the failing request; this was not merely a startup race.
- The archived 1.9.2 DLL lacks the resolver; the 1.9.3 DLL implements it.
- After the installed plugin changed to 1.9.3 at 13:41:20 local time, the error changed to `minerewind.command_active_world_not_found`. This confirms the new resolver was then reached. That later error means no configured world was detected as active and is distinct from the interface failure.
- At inspection time both configured worlds existed, but their `session.lock` files were not held. This observation does not establish the game's state at the earlier request times. The lock detection policy itself was unchanged by the parameter-unification work.
- The installed 1.9.3 DLL matched the bundled package byte for byte. No live settings, plugin installation or Minecraft files were modified during diagnosis or verification.

## Repair

`PluginV3PackageService.InitializeAsync` now performs a separate bundled-package update under its installation gate, after transaction recovery and before loading assemblies. It does not use the ordinary `InstallAsync` entry point, which initializes and would activate the old plugin first. `PluginBundledPackageUpdater` updates only an existing OfficialCatalog/BundledOfficial package with lower semantic-version precedence, validates the bundled SHA-256 and PluginId, uses persisted settings/artifact validation facts, and keeps the previous known-good payload. It does not change settings or Enabled Intent, overwrite manual/newer builds, or reinstall an absent plugin. Safe mode bypasses the update. Failure logs `plugin.bundled_update_failed` and the required MineRewind version.

Target resolution waits for Host plugin initialization. If an active integration lacks the resolver, it now reports `knotlink.current_save_plugin_upgrade_required` with plugin identity/version and upgrade guidance. An unavailable/disabled plugin receives an explicit installation/activation diagnostic. No legacy execution fallback silently discards the new operation options.

## Verification

The user requested removal of the related regression tests and no new regression tests. Related player-preservation, selector, parameter, whitelist and preparation regression cases were removed from the Host and MineRewind repositories. Unrelated existing checks and package/API identity baselines remain.

- The WinUI Host Debug/x64 build succeeded with zero warnings and zero errors.
- A temporary console outside tracked sources exercised the production updater with the actual archived 1.9.2 and bundled 1.9.3 packages in isolated directories. Both official provenance types updated to 1.9.3 and retained 1.9.2 as previous known-good. Manual installations remained 1.9.2; absent installations stayed absent; repeated updates did nothing; a newer version was not downgraded. Settings snapshots remained unchanged.
- Bundled package SHA-256 remained `9cf45bc4be235ec2c95beb4b4dcbf7d14184d66b2cd6c8e6d55206ae5fc35de5`.

The repair takes effect in the newly built Host. Real Minecraft loading acceptance for the player-preservation feature remains pending; package-upgrade verification does not certify game loading.
