using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Services.Plugins.V3;

public static class PluginV3PackageService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<PluginId, LoadedPluginAssembly> Loaded = new();
    private static PluginPackageInstaller? _installer;
    private static bool _initialized;

    public static string PluginsRoot => Services.Plugins.PluginService.PluginRootDirectory;
    private static string DataRoot => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "plugin-data");
    private static string TemporaryRoot => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "plugin-temp");
    private static PluginPackageInstaller Installer => _installer ??= new PluginPackageInstaller(PluginsRoot);

    public static async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Installer.RecoverAsync(cancellationToken).ConfigureAwait(false);
            if (!PluginRuntimeModeService.IsSafeMode)
            {
                foreach (var directory in Directory.EnumerateDirectories(PluginsRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PluginId pluginId;
                    try { pluginId = new PluginId(Path.GetFileName(directory)); }
                    catch { continue; }
                    var state = await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
                    if (state is null || !EnabledIntent(pluginId)) continue;
                    await ActivateInstalledWithoutLockAsync(state, replace: false, cancellationToken).ConfigureAwait(false);
                }
            }
            _initialized = true;
        }
        finally { Gate.Release(); }
    }

    public static async ValueTask<PluginInstallResult> InstallAsync(
        string packagePath,
        PluginInstallProvenance provenance,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var package = await PluginPackageValidator.ValidateAsync(
                packagePath, expectedSha256, cancellationToken: cancellationToken).ConfigureAwait(false);
            var prior = await Installer.ReadStateAsync(package.Manifest.Contract.PluginId, cancellationToken)
                .ConfigureAwait(false);
            if (prior?.Provenance == PluginInstallProvenance.Manual
                && provenance == PluginInstallProvenance.OfficialCatalog)
                throw new InvalidOperationException("Official background updates cannot replace a manually installed build.");

            var result = await Installer.InstallAsync(
                packagePath,
                provenance,
                expectedSha256,
                ValidateOwnedArtifactsAsync,
                ValidateCandidateAsync,
                cancellationToken).ConfigureAwait(false);
            if (prior is null)
            {
                ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent.TryAdd(
                    result.State.PluginId.Value, false);
                ConfigService.Save();
                return result;
            }

            if (PluginV3RuntimeService.IsActive(result.State.PluginId))
            {
                var transition = await ActivateInstalledWithoutLockAsync(
                    result.State, replace: true, cancellationToken).ConfigureAwait(false);
                if (!transition.Success)
                {
                    await Installer.RollbackToPreviousKnownGoodAsync(result.State.PluginId, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "Plugin update was rolled back because runtime replacement failed: "
                        + string.Join(",", transition.Diagnostics.Select(value => value.Code)));
                }
            }
            return result;
        }
        finally { Gate.Release(); }
    }

    public static async ValueTask<PluginRuntimeTransitionResult> SetEnabledAsync(
        PluginId pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent[pluginId.Value] = enabled;
            ConfigService.Save();
            if (!enabled)
            {
                var result = await PluginV3RuntimeService.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
                if (result.Success && Loaded.TryRemove(pluginId, out var loaded)) loaded.Dispose();
                return result;
            }
            var state = await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (state is null)
                return PluginRuntimeTransitionResult.Rejected(
                    OperationOutcome.Blocked,
                    PluginRuntimeState.Inactive,
                    new PluginDiagnostic(
                        "runtime.not_installed",
                        DiagnosticSeverity.Error,
                        "Runtime",
                        pluginId.Value,
                        new Dictionary<string, string>()));
            return await ActivateInstalledWithoutLockAsync(state, replace: false, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static async ValueTask<PluginRuntimeTransitionResult> ActivateInstalledWithoutLockAsync(
        PluginInstallState state,
        bool replace,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(PluginsRoot, state.PluginId.Value, "versions", state.CurrentVersion);
        var manifest = ReadManifest(root);
        var loaded = PluginAssemblyLoader.Load(new PluginLoadRequest(
            state.PluginId,
            root,
            manifest.Contract.EntryAssembly,
            manifest.Contract.EntryType,
            manifest.Contract.RequiredApi));
        try
        {
            var candidate = BuildCandidate(manifest.Contract, loaded);
            var result = replace
                ? await PluginV3RuntimeService.ReplaceAsync(candidate, cancellationToken).ConfigureAwait(false)
                : await PluginV3RuntimeService.ActivateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                loaded.Dispose();
                return result;
            }
            if (Loaded.TryGetValue(state.PluginId, out var prior)) prior.Dispose();
            Loaded[state.PluginId] = loaded;
            return result;
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
    }

    private static PluginActivationCandidate BuildCandidate(
        PluginManifestContract manifest,
        LoadedPluginAssembly loaded)
    {
        var settings = ReadSettings(manifest.PluginId, loaded.Request.RootDirectory, manifest.SettingsSchema);
        var configs = ConfigService.CurrentConfig.BackupConfigs.Select(PluginV3ModelMapper.ToSnapshot).ToArray();
        return new PluginActivationCandidate(
            manifest.PluginId,
            () => loaded.Instance,
            settings,
            configs,
            new PluginV3HostServices(manifest.PluginId, DataRoot, TemporaryRoot),
            new PluginV3ActivationStore(),
            manifest);
    }

    private static async ValueTask ValidateCandidateAsync(
        string root,
        ParsedPluginPackageManifest parsed,
        CancellationToken cancellationToken)
    {
        using var loaded = PluginAssemblyLoader.Load(new PluginLoadRequest(
            parsed.Contract.PluginId,
            root,
            parsed.Contract.EntryAssembly,
            parsed.Contract.EntryType,
            parsed.Contract.RequiredApi));
        var manager = new PluginRuntimeManager();
        var candidate = BuildCandidate(parsed.Contract, loaded) with { Store = new ValidationActivationStore() };
        var result = await manager.ActivateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(
            "Plugin candidate activation validation failed: " + string.Join(",", result.Diagnostics.Select(value => value.Code)));
        await manager.DeactivateAsync(parsed.Contract.PluginId, cancellationToken).ConfigureAwait(false);
    }

    private static ParsedPluginPackageManifest ReadManifest(string root)
        => PluginPackageManifestReader.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));

    private static PluginSettingsSnapshot ReadSettings(PluginId pluginId, string root, string schemaPath)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, schemaPath))))
        {
            if (document.RootElement.TryGetProperty("settings", out var settings)
                && settings.ValueKind == JsonValueKind.Array)
            {
                foreach (var setting in settings.EnumerateArray())
                {
                    if (!setting.TryGetProperty("key", out var keyElement)
                        || !setting.TryGetProperty("default", out var defaultElement)) continue;
                    var key = keyElement.GetString();
                    if (!string.IsNullOrWhiteSpace(key)) values[key] = defaultElement.Clone();
                }
            }
        }
        if (ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings.TryGetValue(pluginId.Value, out var persisted))
        {
            foreach (var (key, value) in persisted) values[key] = value.Clone();
        }
        return new PluginSettingsSnapshot(pluginId, values);
    }

    private static bool EnabledIntent(PluginId pluginId)
        => ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent.TryGetValue(pluginId.Value, out var enabled)
           && enabled;

    public static async ValueTask<bool> IsInstalledAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
        => await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false) is not null;

    public static async ValueTask<IReadOnlyList<InstalledPluginInfo>> GetInstalledPluginInfosAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<InstalledPluginInfo>();
        foreach (var directory in Directory.EnumerateDirectories(PluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginId pluginId;
            try { pluginId = new PluginId(Path.GetFileName(directory)); }
            catch { continue; }
            var state = await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (state is null) continue;
            try
            {
                var manifest = ReadManifest(Path.Combine(directory, "versions", state.CurrentVersion));
                result.Add(new InstalledPluginInfo
                {
                    Id = pluginId.Value,
                    Name = manifest.Contract.Name.Default,
                    Version = state.CurrentVersion,
                    Author = state.Provenance.ToString(),
                    Description = manifest.Contract.Description.Default,
                    InstallPath = directory,
                    IsEnabled = EnabledIntent(pluginId),
                    LoadError = PluginV3RuntimeService.Runtime.GetSnapshot(pluginId).LastError
                });
            }
            catch (Exception ex)
            {
                result.Add(new InstalledPluginInfo
                {
                    Id = pluginId.Value,
                    Name = pluginId.Value,
                    Version = state.CurrentVersion,
                    InstallPath = directory,
                    IsEnabled = EnabledIntent(pluginId),
                    LoadError = ex.Message
                });
            }
        }
        return result;
    }

    public static async ValueTask<PluginUninstallPreview> PreviewUninstallAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
        var providerStateLocations = ConfigService.CurrentConfig.BackupConfigs.Sum(config =>
            (config.ProviderStates.ContainsKey(pluginId.Value) ? 1 : 0)
            + config.SourceFolders.Count(folder => folder.ProviderStates.ContainsKey(pluginId.Value)));
        var histories = await FindOwnedHistoryAsync(pluginId, cancellationToken).ConfigureAwait(false);
        return new PluginUninstallPreview(
            pluginId,
            Path.Combine(PluginsRoot, pluginId.Value),
            settings.TypedSettings.TryGetValue(pluginId.Value, out var typed) ? typed.Count : 0,
            providerStateLocations,
            Path.Combine(DataRoot, pluginId.Value),
            histories,
            $"DELETE {pluginId.Value} DATA");
    }

    public static async ValueTask<PluginUninstallPreview> UninstallAsync(
        PluginId pluginId,
        bool deleteData,
        string? confirmation,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewUninstallAsync(pluginId, cancellationToken).ConfigureAwait(false);
        if (deleteData && !StringComparer.Ordinal.Equals(confirmation, preview.RequiredConfirmation))
            throw new InvalidOperationException("Dangerous plugin data deletion requires the exact confirmation text.");

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transition = await PluginV3RuntimeService.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!transition.Success)
                throw new InvalidOperationException("Plugin is still draining; uninstall must be applied after restart.");
            if (Loaded.TryRemove(pluginId, out var loaded)) loaded.Dispose();
            await Installer.RemoveInstalledCodeAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!deleteData) return preview;

            var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
            settings.TypedSettings.Remove(pluginId.Value);
            settings.PluginSettings.Remove(pluginId.Value);
            foreach (var config in ConfigService.CurrentConfig.BackupConfigs)
            {
                config.ProviderStates.Remove(pluginId.Value);
                foreach (var folder in config.SourceFolders) folder.ProviderStates.Remove(pluginId.Value);
            }
            if (Directory.Exists(preview.DataPath)) Directory.Delete(preview.DataPath, recursive: true);
            ConfigService.Save();
            return preview;
        }
        finally { Gate.Release(); }
    }

    private static async ValueTask ValidateOwnedArtifactsAsync(
        ParsedPluginPackageManifest candidate,
        CancellationToken cancellationToken)
    {
        var owned = await LoadOwnedArtifactsAsync(candidate.Contract.PluginId, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in owned)
        {
            var format = candidate.Contract.ArtifactFormats.SingleOrDefault(value => value.Format == artifact.Format);
            if (format is null || artifact.FormatVersion < format.MinimumVersion || artifact.FormatVersion > format.MaximumVersion)
                throw new InvalidOperationException($"Candidate cannot read reachable Artifact format {artifact.Format} v{artifact.FormatVersion}.");
            var strategy = candidate.Contract.RestoreStrategies.SingleOrDefault(value =>
                value.RestoreStrategyId == artifact.RestoreStrategyId);
            if (strategy is null
                || !strategy.SupportedCompleteness.Contains(artifact.Completeness)
                || !strategy.SupportedFormats.Any(value =>
                    value.Format == artifact.Format
                    && artifact.FormatVersion >= value.MinimumVersion
                    && artifact.FormatVersion <= value.MaximumVersion))
                throw new InvalidOperationException($"Candidate cannot materialize reachable Artifact {artifact.ArtifactId}.");
        }
    }

    private static async ValueTask<IReadOnlyList<ArtifactLedgerEntry>> LoadOwnedArtifactsAsync(
        PluginId pluginId,
        CancellationToken cancellationToken)
    {
        var result = new List<ArtifactLedgerEntry>();
        foreach (var destination in ConfigService.CurrentConfig.BackupConfigs
                     .Select(value => value.DestinationPath)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var store = new FileArtifactLedgerStore(destination);
            var ledger = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var reachable = ArtifactLedgerValidator.ComputeReachable(ledger);
            result.AddRange(ledger.Artifacts.Where(value =>
                reachable.Contains(value.ArtifactId)
                && StringComparer.Ordinal.Equals(value.Format.OwnerId.Value, pluginId.Value)));
        }
        return result;
    }

    private static async ValueTask<IReadOnlyList<string>> FindOwnedHistoryAsync(
        PluginId pluginId,
        CancellationToken cancellationToken)
    {
        var owned = await LoadOwnedArtifactsAsync(pluginId, cancellationToken).ConfigureAwait(false);
        return owned.Select(value => value.HistoryItemId).Distinct(StringComparer.Ordinal).Order().ToArray();
    }

    private sealed class ValidationActivationStore : IPluginActivationStore
    {
        public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}

public sealed record PluginUninstallPreview(
    PluginId PluginId,
    string CodePath,
    int SettingsCount,
    int ProviderStateLocationCount,
    string DataPath,
    IReadOnlyList<string> AffectedHistoryItemIds,
    string RequiredConfirmation);
