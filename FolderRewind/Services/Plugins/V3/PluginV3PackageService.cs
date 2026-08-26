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

public sealed record PluginV3InstallOperationResult(
    OperationOutcome Outcome,
    PluginInstallResult? InstalledPackage,
    bool RolledBack,
    bool WasInstalledBefore,
    bool EnabledAfterOperation,
    PluginRuntimeTransitionResult? RuntimeTransition,
    PluginRuntimeSnapshot RuntimeAfterOperation,
    IReadOnlyList<PluginDiagnostic> Diagnostics)
{
    public bool Success
        => Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings;

    public bool RequiresRestart
        => RuntimeTransition?.RequiresRestart == true || RuntimeAfterOperation.RequiresRestart;

    public bool IsNewInstall
        => Success && InstalledPackage is not null && !WasInstalledBefore;

    public bool CanEnableNow
        => IsNewInstall && !EnabledAfterOperation && !RequiresRestart;
}

public static class PluginV3PackageService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<PluginId, LoadedPluginAssembly> Loaded = new();
    private static readonly ConcurrentQueue<LoadedPluginAssembly> RestartRetainedAssemblies = new();
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

    public static string FormatInstallOutcome(PluginV3InstallOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var name = result.InstalledPackage is null
            ? result.RuntimeAfterOperation.PluginId.Value
            : Resolve(result.InstalledPackage.Manifest.Contract.Name);
        var version = result.InstalledPackage?.State.CurrentVersion ?? string.Empty;
        if (!result.Success)
        {
            var diagnostic = result.Diagnostics.FirstOrDefault()?.Arguments is { } arguments
                && arguments.TryGetValue("message", out var message)
                    ? message
                    : I18n.Format("Plugins_InstallOutcomeFailed", name);
            return diagnostic;
        }
        if (result.RequiresRestart)
        {
            return I18n.Format("Plugins_InstallOutcomeRequiresRestart", name, version);
        }
        if (result.IsNewInstall)
            return I18n.Format("Plugins_InstallOutcomeNew", name, version);
        return I18n.Format(
            result.EnabledAfterOperation ? "Plugins_InstallOutcomeUpdatedEnabled" : "Plugins_InstallOutcomeUpdatedDisabled",
            name,
            version);
    }

    public static string FormatRuntimeDiagnostics(IReadOnlyList<PluginDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var requiresRestart = diagnostics.Any(value =>
            value.Code.Contains("restart_required", StringComparison.Ordinal)
            || value.Code.Contains("physical_unload", StringComparison.Ordinal)
            || value.Code.Contains("deactivation", StringComparison.Ordinal)
            || value.Code.Contains("candidate_cleanup", StringComparison.Ordinal));
        return I18n.GetString(
            requiresRestart ? "Plugins_RuntimeRequiresRestart" : "Plugins_RuntimeOperationFailed");
    }

    public static async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            // Destructive uninstall recovery must run before migration, Manifest projection, or plugin loading.
            await PluginV3UninstallService.RecoverAsync(cancellationToken).ConfigureAwait(false);
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

    public static async ValueTask<PluginV3InstallOperationResult> InstallAsync(
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
                PersistEnabledIntent(
                    package.Manifest.Contract.PluginId,
                    PluginInstallIntentPolicy.ResolveAfterInstall(
                        isUpdate: false,
                        existingEnabledIntent: EnabledIntent(package.Manifest.Contract.PluginId)));
            }

            var runtimeBeforeInstall = PluginV3RuntimeService.Runtime.GetSnapshot(package.Manifest.Contract.PluginId);
            var result = await Installer.InstallAsync(
                packagePath,
                provenance,
                expectedSha256,
                await BuildInstallValidationFactsAsync(package.Manifest, cancellationToken).ConfigureAwait(false),
                cancellationToken,
                retainExistingVersions: runtimeBeforeInstall.RequiresRestart).ConfigureAwait(false);

            PluginRuntimeTransitionResult? transition = null;
            if (runtimeBeforeInstall.RequiresRestart)
            {
                // 重启门禁期间只落盘新版本，不加载程序集，也不触碰当前进程中的保留实例。
                transition = CreateRestartDeferredTransition(
                    runtimeBeforeInstall,
                    result.State.PluginId,
                    "The update was installed and will take effect after FolderRewind restarts.");
            }
            else if (prior is not null && PluginV3RuntimeService.IsActive(result.State.PluginId))
            {
                transition = await ActivateInstalledWithoutLockAsync(
                    result.State, replace: true, cancellationToken).ConfigureAwait(false);
                if (!transition.Success)
                {
                    var diagnostics = transition.Diagnostics;
                    var rollbackCompleted = false;
                    try
                    {
                        await Installer.RollbackToPreviousKnownGoodAsync(result.State.PluginId, CancellationToken.None)
                            .ConfigureAwait(false);
                        rollbackCompleted = true;
                    }
                    catch (Exception rollbackFailure)
                    {
                        diagnostics = diagnostics
                            .Append(new PluginDiagnostic(
                                "plugin.install_rollback_failed",
                                DiagnosticSeverity.Error,
                                "PluginInstall",
                                result.State.PluginId.Value,
                                new Dictionary<string, string>
                                {
                                    ["message"] = rollbackFailure.Message
                                }))
                            .ToArray();
                    }
                    var afterRollback = PluginV3RuntimeService.Runtime.GetSnapshot(result.State.PluginId);
                    return new PluginV3InstallOperationResult(
                        transition.Outcome == OperationOutcome.Canceled
                            ? OperationOutcome.Canceled
                            : OperationOutcome.Failed,
                        InstalledPackage: null,
                        RolledBack: rollbackCompleted,
                        WasInstalledBefore: prior is not null,
                        EnabledAfterOperation: EnabledIntent(result.State.PluginId),
                        RuntimeTransition: transition,
                        RuntimeAfterOperation: afterRollback,
                        Diagnostics: diagnostics);
                }
            }

            var runtimeAfterOperation = PluginV3RuntimeService.Runtime.GetSnapshot(result.State.PluginId);
            var outcome = transition?.Outcome ?? OperationOutcome.Success;
            if (runtimeAfterOperation.RequiresRestart)
            {
                outcome = OperationOutcome.SuccessWithWarnings;
            }
            return new PluginV3InstallOperationResult(
                outcome,
                result,
                RolledBack: false,
                WasInstalledBefore: prior is not null,
                EnabledIntent(result.State.PluginId),
                transition,
                runtimeAfterOperation,
                transition?.Diagnostics ?? Array.Empty<PluginDiagnostic>());
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
            var decision = PluginRuntimeIntentPolicy.Decide(
                enabled,
                currentIntent,
                currentSnapshot.State,
                currentSnapshot.RequiresRestart);

            if (decision.PersistIntent)
            {
                PersistEnabledIntent(pluginId, enabled);
            }

            if (decision.ActivationDeferred)
            {
                return new PluginRuntimeTransitionResult(
                    true,
                    OperationOutcome.SuccessWithWarnings,
                    currentSnapshot.State,
                    [new PluginDiagnostic(
                        "runtime.restart_required",
                        DiagnosticSeverity.Warning,
                        "Runtime",
                        pluginId.Value,
                        new Dictionary<string, string>
                        {
                            ["message"] = "The enable intent was saved and will take effect after FolderRewind restarts."
                        })],
                    RequiresRestart: true);
            }

            if (decision.RuntimeAction == PluginRuntimeIntentAction.None)
            {
                return currentSnapshot.RequiresRestart
                    ? new PluginRuntimeTransitionResult(
                        true,
                        OperationOutcome.SuccessWithWarnings,
                        currentSnapshot.State,
                        [new PluginDiagnostic(
                            "runtime.restart_required",
                            DiagnosticSeverity.Warning,
                            "Runtime",
                            pluginId.Value,
                            new Dictionary<string, string>
                            {
                                ["message"] = "The requested state will be finalized after FolderRewind restarts."
                            })],
                        RequiresRestart: true)
                    : PluginRuntimeTransitionResult.Completed(currentSnapshot.State);
            }

            if (decision.RuntimeAction == PluginRuntimeIntentAction.Deactivate)
            {
                var result = await PluginV3RuntimeService.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
                if (result.Success && Loaded.TryRemove(pluginId, out var loaded))
                    result = await ReleaseLoadedAssemblyAsync(
                        pluginId,
                        loaded,
                        result.RequiresRestart,
                        CancellationToken.None,
                        result).ConfigureAwait(false);
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
        var beforeLoad = PluginV3RuntimeService.Runtime.GetSnapshot(state.PluginId);
        if (PluginV3RuntimeService.Runtime.IsSafeMode)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                beforeLoad.State,
                new PluginDiagnostic(
                    "runtime.safe_mode",
                    DiagnosticSeverity.Error,
                    "Runtime",
                    state.PluginId.Value,
                    new Dictionary<string, string>
                    {
                        ["message"] = "Safe Mode blocks all plugin loading."
                    }));
        }
        if (beforeLoad.RequiresRestart)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                beforeLoad.State,
                new PluginDiagnostic(
                    "runtime.restart_required",
                    DiagnosticSeverity.Error,
                    "Runtime",
                    state.PluginId.Value,
                    new Dictionary<string, string>
                    {
                        ["message"] = "Host restart is required before this plugin can be loaded again."
                    })) with
            {
                RequiresRestart = true
            };
        }
        if (replace && beforeLoad.State != PluginRuntimeState.Active)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                beforeLoad.State,
                new PluginDiagnostic(
                    "runtime.not_active",
                    DiagnosticSeverity.Error,
                    "Runtime",
                    state.PluginId.Value,
                    new Dictionary<string, string>
                    {
                        ["message"] = "There is no active session to replace."
                    }));
        }
        if (!replace && beforeLoad.State == PluginRuntimeState.Active)
        {
            return PluginRuntimeTransitionResult.Rejected(
                OperationOutcome.Blocked,
                beforeLoad.State,
                new PluginDiagnostic(
                    "runtime.already_active",
                    DiagnosticSeverity.Error,
                    "Runtime",
                    state.PluginId.Value,
                    new Dictionary<string, string>
                    {
                        ["message"] = "The plugin is already active."
                    }));
        }

        LoadedPluginAssembly? loaded = null;
        try
        {
            var root = Path.Combine(PluginsRoot, state.PluginId.Value, "versions", state.CurrentVersion);
            var manifest = ReadManifest(root);
            loaded = PluginAssemblyLoader.Load(new PluginLoadRequest(
                state.PluginId,
                root,
                manifest.Contract.EntryAssembly,
                manifest.Contract.EntryType,
                manifest.Contract.RequiredApi));
            var candidate = BuildCandidate(manifest.Contract, loaded, settingsOverride);
            var result = replace
                ? await PluginV3RuntimeService.ReplaceAsync(candidate, cancellationToken).ConfigureAwait(false)
                : await PluginV3RuntimeService.ActivateAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                // Runtime 的并发门禁在候选工厂运行前返回；此时新加载上下文仍可独立卸载。
                var retainCandidate = result.RequiresRestart && result.Outcome != OperationOutcome.Blocked;
                return await ReleaseLoadedAssemblyAsync(
                    state.PluginId,
                    loaded,
                    retainCandidate,
                    CancellationToken.None,
                    result).ConfigureAwait(false);
            }
            if (Loaded.TryGetValue(state.PluginId, out var prior))
            {
                result = await ReleaseLoadedAssemblyAsync(
                    state.PluginId,
                    prior,
                    result.RequiresRestart,
                    CancellationToken.None,
                    result).ConfigureAwait(false);
            }
            Loaded[state.PluginId] = loaded;
            return result;
        }
        catch (Exception ex)
        {
            var snapshot = PluginV3RuntimeService.Runtime.GetSnapshot(state.PluginId);
            var failed = new PluginRuntimeTransitionResult(
                false,
                ex is OperationCanceledException ? OperationOutcome.Canceled : OperationOutcome.Failed,
                snapshot.State,
                [new PluginDiagnostic(
                    "runtime.plugin_load_failed",
                    DiagnosticSeverity.Error,
                    "Runtime",
                    state.PluginId.Value,
                    new Dictionary<string, string> { ["message"] = ex.Message })],
                snapshot.RequiresRestart);
            return loaded is null
                ? failed
                : await ReleaseLoadedAssemblyAsync(
                    state.PluginId,
                    loaded,
                    false,
                    CancellationToken.None,
                    failed).ConfigureAwait(false);
        }
    }

    private static PluginRuntimeTransitionResult CreateRestartDeferredTransition(
        PluginRuntimeSnapshot snapshot,
        PluginId pluginId,
        string message)
        => new(
            true,
            OperationOutcome.SuccessWithWarnings,
            snapshot.State,
            [new PluginDiagnostic(
                "runtime.restart_required",
                DiagnosticSeverity.Warning,
                "Runtime",
                pluginId.Value,
                new Dictionary<string, string> { ["message"] = message })],
            RequiresRestart: true);

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

    private static async ValueTask<PluginRuntimeTransitionResult> ReleaseLoadedAssemblyAsync(
        PluginId pluginId,
        LoadedPluginAssembly loaded,
        bool requiresRestart,
        CancellationToken cancellationToken,
        PluginRuntimeTransitionResult transition)
    {
        if (requiresRestart)
        {
            RestartRetainedAssemblies.Enqueue(loaded);
            return transition;
        }

        try
        {
            if (await loaded.UnloadAsync(cancellationToken).ConfigureAwait(false))
            {
                return transition;
            }
        }
        catch (Exception ex)
        {
            PluginV3RuntimeService.Runtime.MarkPhysicalUnloadFailed(
                pluginId,
                $"Physical plugin unload failed: {ex.Message}");
            return AddPhysicalUnloadWarning(transition, pluginId, ex.Message);
        }

        const string message = "Physical plugin unload could not be verified; restart FolderRewind before loading it again.";
        PluginV3RuntimeService.Runtime.MarkPhysicalUnloadFailed(pluginId, message);
        return AddPhysicalUnloadWarning(transition, pluginId, message);
    }

    private static PluginRuntimeTransitionResult AddPhysicalUnloadWarning(
        PluginRuntimeTransitionResult transition,
        PluginId pluginId,
        string message)
    {
        var diagnostics = transition.Diagnostics
            .Append(new PluginDiagnostic(
                "runtime.physical_unload_failed",
                DiagnosticSeverity.Warning,
                "Runtime",
                pluginId.Value,
                new Dictionary<string, string> { ["message"] = message }))
            .ToArray();
        return transition with
        {
            Outcome = transition.Success ? OperationOutcome.SuccessWithWarnings : transition.Outcome,
            Diagnostics = diagnostics,
            RequiresRestart = true
        };
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

    private static void PersistEnabledIntent(PluginId pluginId, bool enabled)
    {
        var intents = ConfigService.CurrentConfig.GlobalSettings.Plugins.EnabledIntent;
        var hadPrevious = intents.TryGetValue(pluginId.Value, out var previous);
        intents[pluginId.Value] = enabled;
        var save = ConfigService.SaveWithResult();
        if (save.Success) return;

        if (hadPrevious) intents[pluginId.Value] = previous;
        else intents.Remove(pluginId.Value);
        throw new IOException(
            "The plugin Enabled Intent could not be persisted: " + save.ErrorMessage);
    }

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
            var runtimeSnapshot = PluginV3RuntimeService.Runtime.GetSnapshot(pluginId);
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
                    LoadError = runtimeSnapshot.LastError,
                    RequiresRestart = runtimeSnapshot.RequiresRestart
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
                    LoadError = ex.Message,
                    RequiresRestart = runtimeSnapshot.RequiresRestart
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

    public static async ValueTask<PluginUninstallResult> UninstallAsync(
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
            // 用户已确认卸载；先清除启用意图，避免启动竞态重新激活插件。
            PersistEnabledIntent(pluginId, enabled: false);
            var transition = await PluginV3RuntimeService.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
            if (!transition.Success)
            {
                return new PluginUninstallResult(
                    preview,
                    transition.Outcome,
                    string.Empty,
                    FormatDiagnostics(transition.Diagnostics));
            }
            if (Loaded.TryRemove(pluginId, out var loaded))
            {
                transition = await ReleaseLoadedAssemblyAsync(
                    pluginId,
                    loaded,
                    transition.RequiresRestart,
                    CancellationToken.None,
                    transition).ConfigureAwait(false);
            }
            if (transition.RequiresRestart)
            {
                return new PluginUninstallResult(
                    preview,
                    OperationOutcome.SuccessWithWarnings,
                    string.Empty,
                    FormatDiagnostics(transition.Diagnostics));
            }
            if (!deleteData)
            {
                await PluginV3OfflineUpgradeService.SuppressAutomaticMigrationAsync(pluginId, cancellationToken)
                    .ConfigureAwait(false);
                await Installer.RemoveInstalledCodeAsync(pluginId, cancellationToken).ConfigureAwait(false);
                return new PluginUninstallResult(preview, OperationOutcome.Success, string.Empty, string.Empty);
            }

            var transaction = await PluginV3UninstallService.ExecuteAsync(preview, cancellationToken)
                .ConfigureAwait(false);
            if (transaction.Outcome == OperationOutcome.SuccessWithWarnings)
            {
                LogService.LogWarning(
                    $"Plugin uninstall committed with cleanup pending at '{transaction.RecoveryPath}': {transaction.Diagnostic}",
                    "PluginV3Uninstall");
            }
            return new PluginUninstallResult(
                preview,
                transaction.Outcome,
                transaction.RecoveryPath,
                transaction.Diagnostic);
        }
        finally { Gate.Release(); }
    }

    private static string FormatDiagnostics(IReadOnlyList<PluginDiagnostic> diagnostics)
        => FormatRuntimeDiagnostics(diagnostics);

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

public sealed record PluginUninstallResult(
    PluginUninstallPreview Preview,
    OperationOutcome Outcome,
    string RecoveryPath,
    string Diagnostic)
{
    public PluginId PluginId => Preview.PluginId;
    public string CodePath => Preview.CodePath;
    public int SettingsCount => Preview.SettingsCount;
    public int ProviderStateLocationCount => Preview.ProviderStateLocationCount;
    public string DataPath => Preview.DataPath;
    public IReadOnlyList<string> AffectedHistoryItemIds => Preview.AffectedHistoryItemIds;
    public string RequiredConfirmation => Preview.RequiredConfirmation;
}
