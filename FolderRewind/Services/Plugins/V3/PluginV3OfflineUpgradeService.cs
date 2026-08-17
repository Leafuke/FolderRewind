using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Plugins.V3;

internal sealed record PluginV3OfflineUpgradeResult(
    bool Attempted,
    bool Completed,
    bool RecoveryRequired,
    string DiagnosticCode = "",
    string DiagnosticMessage = "");

internal static class PluginV3OfflineUpgradeService
{
    private const string MineRewindId = "com.folderrewind.minerewind";
    private const string BundledFileName = "MineRewind-1.9.0.frplugin";
    private const string BundledSha256 = "2790b49296c8b79bf6aae2a9643e254f47c4478d7a6fad389307a5d233c85ec2";
    private static readonly TimeSpan MigrationTimeout = TimeSpan.FromSeconds(30);
    private static readonly PluginId MineRewindPluginId = new(MineRewindId);
    private static readonly HashSet<string> V3Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        "versions",
        "install-state.v1.json"
    };

    private static PluginMigrationStateStore StateStore => new(Path.Combine(
        PluginV3PackageService.PluginsRoot,
        ".migration"));

    public static async ValueTask<PluginV3OfflineUpgradeResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        if (PluginRuntimeModeService.IsSafeMode)
            return new PluginV3OfflineUpgradeResult(false, false, false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MigrationTimeout);
        var operationCancellation = timeout.Token;
        PluginMigrationState? progress = null;
        try
        {
            var store = StateStore;
            var priorState = await store.ReadAsync(MineRewindPluginId, operationCancellation).ConfigureAwait(false);
            var pluginRoot = Path.Combine(PluginV3PackageService.PluginsRoot, MineRewindId);
            var hasFlatPayload = HasFlatPayload(pluginRoot);
            var isInstalled = await PluginV3PackageService.IsInstalledAsync(
                MineRewindPluginId,
                operationCancellation).ConfigureAwait(false);
            var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
            var hasLegacyData = settings.EnabledIntent.ContainsKey(MineRewindId)
                || settings.TypedSettings.ContainsKey(MineRewindId)
                || ConfigService.CurrentConfig.BackupConfigs.Any(config =>
                    config.ProviderStates.ContainsKey(MineRewindId)
                    || config.SourceFolders.Any(folder => folder.ProviderStates.ContainsKey(MineRewindId)));

            if (!hasFlatPayload
                && priorState?.Status is PluginMigrationStatus.Completed or PluginMigrationStatus.Suppressed)
                return new PluginV3OfflineUpgradeResult(false, priorState.Status == PluginMigrationStatus.Completed, false);

            if (!hasFlatPayload && isInstalled && priorState is null)
                return new PluginV3OfflineUpgradeResult(false, false, false);

            if (!hasFlatPayload && !isInstalled && !hasLegacyData)
                return new PluginV3OfflineUpgradeResult(false, false, false);

            if (!hasFlatPayload
                && isInstalled
                && priorState is { Phase: "QuarantiningLegacyPayload" })
            {
                var completedQuarantine = !string.IsNullOrWhiteSpace(priorState.QuarantinePath)
                    && File.Exists(Path.Combine(priorState.QuarantinePath, "quarantine-receipt.json"))
                        ? priorState.QuarantinePath
                        : string.IsNullOrWhiteSpace(priorState.QuarantinePath)
                            ? FindCompletedQuarantinePath()
                            : string.Empty;
                if (string.IsNullOrWhiteSpace(completedQuarantine))
                {
                    const string diagnosticCode = "plugin_quarantine_recovery_required";
                    const string diagnosticMessage = "Legacy files left the plugin root, but no complete quarantine receipt exists. Manual recovery is required before activation.";
                    await RecordRecoveryRequiredAsync(
                        priorState,
                        diagnosticCode,
                        diagnosticMessage).ConfigureAwait(false);
                    LogService.LogError(
                        $"Offline MineRewind migration requires recovery: {diagnosticCode}: {diagnosticMessage}",
                        "PluginV3Migration");
                    return new PluginV3OfflineUpgradeResult(
                        true,
                        false,
                        true,
                        diagnosticCode,
                        diagnosticMessage);
                }

                priorState = priorState with { QuarantinePath = completedQuarantine };
            }

            var priorIntent = priorState?.PreservedEnabledIntent
                ?? (settings.EnabledIntent.TryGetValue(MineRewindId, out var intended) && intended);
            var now = DateTimeOffset.UtcNow;
            progress = new PluginMigrationState(
                1,
                MineRewindPluginId,
                PluginMigrationStatus.InProgress,
                "DetectedLegacyState",
                priorState?.StartedAtUtc ?? now,
                now,
                priorIntent,
                priorState?.InstalledVersion ?? string.Empty,
                priorState?.QuarantinePath ?? string.Empty);
            await store.WriteAsync(progress, operationCancellation).ConfigureAwait(false);
            LogService.LogInfo(
                $"Offline MineRewind migration started. EnabledIntent={priorIntent}; state={store.GetStatePath(MineRewindPluginId)}",
                "PluginV3Migration");

            if (!isInstalled)
            {
                progress = await AdvanceAsync(store, progress, "InstallingBundledPackage", operationCancellation)
                    .ConfigureAwait(false);
                var packagePath = ResolveBundledPackagePath();
                if (!File.Exists(packagePath))
                    throw new FileNotFoundException("Bundled MineRewind migration package is missing.", packagePath);
                var install = await PluginV3PackageService.InstallAsync(
                    packagePath,
                    PluginInstallProvenance.BundledOfficial,
                    BundledSha256,
                    operationCancellation).ConfigureAwait(false);
                progress = progress with
                {
                    InstalledVersion = install.State.CurrentVersion,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await store.WriteAsync(progress, operationCancellation).ConfigureAwait(false);
            }

            if (HasFlatPayload(pluginRoot))
            {
                progress = await AdvanceAsync(store, progress, "QuarantiningLegacyPayload", operationCancellation)
                    .ConfigureAwait(false);
                var quarantine = await LegacyPluginQuarantineService.QuarantineFlatPayloadAsync(
                    MineRewindPluginId,
                    pluginRoot,
                    Path.Combine(
                        AppRuntimeInfo.WritableAppDataBaseDirectory,
                        "FolderRewind",
                        "legacy-quarantine",
                        "plugins"),
                    operationCancellation).ConfigureAwait(false);
                progress = progress with
                {
                    QuarantinePath = quarantine.QuarantinePath,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await store.WriteAsync(progress, operationCancellation).ConfigureAwait(false);
            }

            progress = await AdvanceAsync(store, progress, "RestoringEnabledIntent", operationCancellation)
                .ConfigureAwait(false);
            var transition = await PluginV3PackageService.SetEnabledAsync(
                MineRewindPluginId,
                priorIntent,
                operationCancellation).ConfigureAwait(false);
            if (!transition.Success)
            {
                throw new InvalidOperationException(
                    "Bundled MineRewind installed, but its preserved Enabled Intent could not be applied: "
                    + string.Join(",", transition.Diagnostics.Select(value => value.Code)));
            }

            progress = progress with
            {
                Status = PluginMigrationStatus.Completed,
                Phase = "Completed",
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DiagnosticCode = string.Empty,
                DiagnosticMessage = string.Empty
            };
            await store.WriteAsync(progress, operationCancellation).ConfigureAwait(false);
            LogService.LogInfo(
                $"Offline MineRewind migration completed. Version={progress.InstalledVersion}; quarantine={progress.QuarantinePath}",
                "PluginV3Migration");
            return new PluginV3OfflineUpgradeResult(true, true, false);
        }
        catch (Exception ex)
        {
            if (ex is LegacyPluginQuarantineRecoveryException quarantineError && progress is not null)
            {
                progress = progress with
                {
                    QuarantinePath = quarantineError.QuarantinePath,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }
            var timedOut = ex is OperationCanceledException
                && timeout.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested;
            var code = timedOut ? "plugin_migration_timeout" : "plugin_migration_failed";
            var message = timedOut
                ? $"Offline MineRewind migration exceeded {MigrationTimeout.TotalSeconds:0} seconds and was canceled."
                : ex.Message;
            await RecordRecoveryRequiredAsync(progress, code, message).ConfigureAwait(false);
            LogService.LogError(
                $"Offline MineRewind migration requires recovery: {code}: {message}",
                "PluginV3Migration",
                ex);
            return new PluginV3OfflineUpgradeResult(true, false, true, code, message);
        }
    }

    public static bool ShouldBlockLegacyExecution(string pluginId)
        => string.Equals(pluginId, MineRewindId, StringComparison.OrdinalIgnoreCase)
           && HasFlatPayload(Path.Combine(PluginV3PackageService.PluginsRoot, MineRewindId));

    public static async ValueTask SuppressAutomaticMigrationAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        if (pluginId != MineRewindPluginId) return;
        var store = StateStore;
        var prior = await store.ReadAsync(pluginId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
        var preservedIntent = settings.EnabledIntent.TryGetValue(pluginId.Value, out var intended) && intended;
        await store.WriteAsync(new PluginMigrationState(
            1,
            pluginId,
            PluginMigrationStatus.Suppressed,
            "UserRemovedInstalledCode",
            prior?.StartedAtUtc ?? now,
            now,
            PreservedEnabledIntent: preservedIntent,
            prior?.InstalledVersion ?? string.Empty,
            prior?.QuarantinePath ?? string.Empty), cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask<PluginMigrationState?> CaptureMigrationStateAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
        => StateStore.ReadAsync(pluginId, cancellationToken);

    internal static async ValueTask RestoreMigrationStateAsync(
        PluginId pluginId,
        PluginMigrationState? state,
        CancellationToken cancellationToken = default)
    {
        var store = StateStore;
        if (state is null) await store.DeleteAsync(pluginId, cancellationToken).ConfigureAwait(false);
        else await store.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<PluginMigrationState> AdvanceAsync(
        PluginMigrationStateStore store,
        PluginMigrationState state,
        string phase,
        CancellationToken cancellationToken)
    {
        var next = state with { Phase = phase, UpdatedAtUtc = DateTimeOffset.UtcNow };
        await store.WriteAsync(next, cancellationToken).ConfigureAwait(false);
        LogService.LogInfo($"Offline MineRewind migration phase: {phase}", "PluginV3Migration");
        return next;
    }

    private static async ValueTask RecordRecoveryRequiredAsync(
        PluginMigrationState? progress,
        string code,
        string message)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var failed = (progress ?? new PluginMigrationState(
                1,
                MineRewindPluginId,
                PluginMigrationStatus.InProgress,
                "DetectingLegacyState",
                now,
                now)) with
            {
                Status = PluginMigrationStatus.RecoveryRequired,
                UpdatedAtUtc = now,
                DiagnosticCode = code,
                DiagnosticMessage = message
            };
            await StateStore.WriteAsync(failed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception stateError)
        {
            LogService.LogError(
                $"Failed to persist the plugin migration recovery diagnostic: {stateError.Message}",
                "PluginV3Migration",
                stateError);
        }
    }

    private static bool HasFlatPayload(string pluginRoot)
        => Directory.Exists(pluginRoot)
           && Directory.EnumerateFileSystemEntries(pluginRoot)
               .Any(path => !V3Entries.Contains(Path.GetFileName(path)));

    private static string FindCompletedQuarantinePath()
    {
        var root = Path.Combine(
            AppRuntimeInfo.WritableAppDataBaseDirectory,
            "FolderRewind",
            "legacy-quarantine",
            "plugins",
            MineRewindId);
        if (!Directory.Exists(root)) return string.Empty;
        return Directory.EnumerateDirectories(root)
            .Where(path => File.Exists(Path.Combine(path, "quarantine-receipt.json")))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ResolveBundledPackagePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Plugins", BundledFileName),
            Path.Combine(AppContext.BaseDirectory, BundledFileName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
