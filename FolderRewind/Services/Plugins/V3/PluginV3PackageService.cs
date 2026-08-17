using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Loading;
using FolderRewind.Plugin.Runtime.Packaging;
using FolderRewind.Plugin.Runtime.Settings;

namespace FolderRewind.Services.Plugins.V3;

public sealed record PluginV3SettingsEditorData(
    PluginSettingsSchema Schema,
    PluginSettingsSnapshot Settings);

public static class PluginV3PackageService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<PluginId, LoadedPluginAssembly> Loaded = new();
    private static readonly JsonSerializerOptions InstallStateJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static PluginPackageInstaller? _installer;
    private static bool _initialized;

    public static string PluginsRoot => Services.Plugins.PluginService.PluginRootDirectory;
    private static string DataRoot => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "plugin-data");
    private static string TemporaryRoot => Path.Combine(AppRuntimeInfo.WritableAppDataBaseDirectory, "FolderRewind", "plugin-temp");
    private static PluginPackageInstaller Installer => _installer ??= new PluginPackageInstaller(PluginsRoot);

    public static string FormatInstallOutcome(PluginInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var name = Resolve(result.Manifest.Contract.Name);
        var version = result.State.CurrentVersion;
        if (!result.Updated)
            return I18n.Format("Plugins_InstallOutcomeNew", name, version);
        var enabled = EnabledIntent(result.State.PluginId);
        return I18n.Format(
            enabled ? "Plugins_InstallOutcomeUpdatedEnabled" : "Plugins_InstallOutcomeUpdatedDisabled",
            name,
            version);
    }

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
                    var transition = await ActivateInstalledWithoutLockAsync(
                        state,
                        replace: false,
                        cancellationToken).ConfigureAwait(false);
                    if (!transition.Success)
                    {
                        LogService.LogError(
                            $"Plugin '{pluginId}' could not honor Enabled Intent during startup: "
                            + string.Join(",", transition.Diagnostics.Select(value => value.Code)),
                            "PluginV3");
                    }
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

            if (prior is null)
            {
                ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent[
                    package.Manifest.Contract.PluginId.Value] = PluginInstallIntentPolicy.ResolveAfterInstall(
                        isUpdate: false,
                        existingEnabledIntent: EnabledIntent(package.Manifest.Contract.PluginId));
                var save = ConfigService.SaveWithResult();
                if (!save.Success)
                    throw new IOException("The plugin could not be installed as Disabled because Enabled Intent could not be persisted: "
                                          + save.ErrorMessage);
            }

            var result = await Installer.InstallAsync(
                packagePath,
                provenance,
                expectedSha256,
                await BuildInstallValidationFactsAsync(package.Manifest, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            if (prior is null)
            {
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
            var currentIntent = EnabledIntent(pluginId);
            var currentSnapshot = PluginV3RuntimeService.Runtime.GetSnapshot(pluginId);
            var decision = PluginRuntimeIntentPolicy.Decide(enabled, currentIntent, currentSnapshot.State);

            if (decision.PersistIntent)
            {
                ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent[pluginId.Value] = enabled;
                ConfigService.Save();
            }

            if (decision.RuntimeAction == PluginRuntimeIntentAction.None)
                return PluginRuntimeTransitionResult.Completed(currentSnapshot.State);

            if (decision.RuntimeAction == PluginRuntimeIntentAction.Deactivate)
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
        CancellationToken cancellationToken,
        PluginSettingsSnapshot? settingsOverride = null)
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
            var candidate = BuildCandidate(manifest.Contract, loaded, settingsOverride);
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
        LoadedPluginAssembly loaded,
        PluginSettingsSnapshot? settingsOverride = null)
    {
        var settings = settingsOverride
            ?? ReadSettings(manifest.PluginId, loaded.Request.RootDirectory, manifest.SettingsSchema);
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

    public static async ValueTask<PluginV3SettingsEditorData?> GetSettingsEditorDataAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (state is null) return null;
            var root = GetCurrentVersionRoot(state);
            var manifest = ReadManifest(root);
            var schema = PluginSettingsSchema.Parse(
                File.ReadAllBytes(Path.Combine(root, manifest.Contract.SettingsSchema)));
            var validation = schema.Validate(ReadSettings(
                pluginId,
                root,
                manifest.Contract.SettingsSchema));
            return new PluginV3SettingsEditorData(schema, validation.NormalizedSettings);
        }
        finally { Gate.Release(); }
    }

    public static async ValueTask<PluginSettingsApplyResult> ApplySettingsAsync(
        PluginId pluginId,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await Installer.ReadStateAsync(pluginId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The plugin is not installed.");
            var root = GetCurrentVersionRoot(state);
            var manifest = ReadManifest(root);
            var schema = PluginSettingsSchema.Parse(
                File.ReadAllBytes(Path.Combine(root, manifest.Contract.SettingsSchema)));
            var validation = schema.Validate(new PluginSettingsSnapshot(
                pluginId,
                values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)));
            if (!validation.IsValid) return new PluginSettingsApplyResult(validation, null);

            var snapshot = PluginV3RuntimeService.Runtime.GetSnapshot(pluginId);
            if (!PluginRuntimeModeService.IsSafeMode
                && (snapshot.State == PluginRuntimeState.Active || EnabledIntent(pluginId)))
            {
                var transition = await ActivateInstalledWithoutLockAsync(
                    state,
                    replace: snapshot.State == PluginRuntimeState.Active,
                    cancellationToken: cancellationToken,
                    settingsOverride: validation.NormalizedSettings).ConfigureAwait(false);
                return new PluginSettingsApplyResult(validation, transition);
            }

            // 禁用插件只保存已验证的静态设置；绝不能为了打开设置页而执行插件代码。
            PersistInactiveSettings(pluginId, validation.NormalizedSettings);
            return new PluginSettingsApplyResult(
                validation,
                PluginRuntimeTransitionResult.Completed(snapshot.State));
        }
        finally { Gate.Release(); }
    }

    public static IReadOnlyList<InstalledPluginInfo> GetInstalledPluginInfos(
        CancellationToken cancellationToken = default)
    {
        var result = new List<InstalledPluginInfo>();
        foreach (var directory in Directory.EnumerateDirectories(PluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginId pluginId;
            try { pluginId = new PluginId(Path.GetFileName(directory)); }
            catch { continue; }
            var state = ReadInstallState(directory);
            if (state is null) continue;
            try
            {
                var manifest = ReadManifest(Path.Combine(directory, "versions", state.CurrentVersion));
                result.Add(new InstalledPluginInfo
                {
                    Id = pluginId.Value,
                    Name = Resolve(manifest.Contract.Name),
                    Version = state.CurrentVersion,
                    Author = manifest.Author,
                    Description = Resolve(manifest.Contract.Description),
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

    public static IReadOnlyList<(PluginId PluginId, ConfigKindDeclaration Kind)> GetInstalledConfigKinds(
        CancellationToken cancellationToken = default)
    {
        var result = new List<(PluginId, ConfigKindDeclaration)>();
        var occupied = new HashSet<ConfigKindRef>();
        if (!Directory.Exists(PluginsRoot)) return result;
        foreach (var directory in Directory.EnumerateDirectories(PluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadInstallState(directory);
            if (state is null) continue;
            try
            {
                var manifest = ReadManifest(GetCurrentVersionRoot(state));
                PluginManifestContractValidator.ValidateStatic(manifest.Contract, state.PluginId);
                foreach (var kind in manifest.Contract.ConfigKinds)
                {
                    if (!occupied.Add(kind.Kind))
                        throw new InvalidDataException($"Installed plugin inventory contains duplicate Config Kind '{kind.Kind}'.");
                    result.Add((state.PluginId, kind));
                }
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"Installed plugin Config Kind metadata could not be read from '{directory}': {ex.Message}",
                    "PluginV3");
            }
        }
        return result
            .OrderBy(pair => pair.Item1.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.Item2.Kind.KindId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void PersistInactiveSettings(PluginId pluginId, PluginSettingsSnapshot settings)
    {
        var typedSettings = ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings;
        var hadPrevious = typedSettings.TryGetValue(pluginId.Value, out var previous);
        typedSettings[pluginId.Value] = settings.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.Ordinal);
        var save = ConfigService.SaveWithResult();
        if (save.Success) return;

        if (hadPrevious && previous is not null) typedSettings[pluginId.Value] = previous;
        else typedSettings.Remove(pluginId.Value);
        throw new IOException(save.ErrorMessage ?? "Plugin settings could not be saved.");
    }

    private static string GetCurrentVersionRoot(PluginInstallState state)
        => Path.Combine(PluginsRoot, state.PluginId.Value, "versions", state.CurrentVersion);

    private static string Resolve(LocalizedText text)
        => I18n.PickBest(text.Translations, text.Default) ?? text.Default;

    public static async ValueTask<IReadOnlyList<InstalledPluginInfo>> GetInstalledPluginInfosAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return GetInstalledPluginInfos(cancellationToken);
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
            await PluginV3OfflineUpgradeService.SuppressAutomaticMigrationAsync(pluginId, cancellationToken)
                .ConfigureAwait(false);
            await Installer.RemoveInstalledCodeAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!deleteData) return preview;

            var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
            settings.TypedSettings.Remove(pluginId.Value);
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

    private static async ValueTask<PluginPackageInstallValidationFacts> BuildInstallValidationFactsAsync(
        ParsedPluginPackageManifest candidate,
        CancellationToken cancellationToken)
    {
        var owned = await LoadOwnedArtifactsAsync(candidate.Contract.PluginId, cancellationToken).ConfigureAwait(false);
        var installed = new List<PluginManifestContract>();
        foreach (var directory in Directory.EnumerateDirectories(PluginsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = ReadInstallState(directory);
            if (state is null || state.PluginId == candidate.Contract.PluginId) continue;
            try
            {
                var manifest = ReadManifest(GetCurrentVersionRoot(state));
                PluginManifestContractValidator.ValidateStatic(manifest.Contract, state.PluginId);
                installed.Add(manifest.Contract);
            }
            catch (Exception ex)
            {
                LogService.LogWarning(
                    $"Invalid installed plugin Manifest was excluded from Config Kind inventory: {ex.Message}",
                    "PluginV3");
            }
        }

        var persisted = ConfigService.CurrentConfig.GlobalSettings.Plugins.TypedSettings
            .TryGetValue(candidate.Contract.PluginId.Value, out var values)
            ? values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return new PluginPackageInstallValidationFacts(
            new PluginSettingsSnapshot(candidate.Contract.PluginId, persisted),
            installed,
            owned.Select(artifact => new ReachableOwnedArtifactRequirement(
                artifact.ArtifactId,
                artifact.Format,
                artifact.FormatVersion,
                artifact.Completeness,
                artifact.RestoreStrategyId)).ToArray());
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

    private static PluginInstallState? ReadInstallState(string pluginDirectory)
    {
        var path = Path.Combine(pluginDirectory, "install-state.v1.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<PluginInstallState>(File.ReadAllBytes(path), InstallStateJson)
            : null;
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
