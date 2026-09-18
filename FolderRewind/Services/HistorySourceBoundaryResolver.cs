using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record HistorySourceBoundaryResolution(
    BackupConfig EffectiveConfig,
    ManagedFolder EffectiveFolder,
    EffectiveSourceBoundarySnapshot Boundary,
    ConfigSnapshot ConfigSnapshot,
    FolderSnapshot FolderSnapshot,
    PluginId? ActivePluginId,
    IReadOnlyList<PluginDiagnostic> Diagnostics,
    bool IsBlocked);

/// <summary>Resolves the authoritative managed boundary shared by capture, restore, checkout and merge.</summary>
internal static class HistorySourceBoundaryResolver
{
    public static async ValueTask<HistorySourceBoundaryResolution> ResolveAsync(
        BackupConfig originalConfig,
        ManagedFolder originalFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalConfig);
        ArgumentNullException.ThrowIfNull(originalFolder);
        var (config, folder) = PluginV3ModelMapper.CloneForOperation(originalConfig, originalFolder);
        var configSnapshot = PluginV3ModelMapper.ToSnapshot(config);
        var folderId = Guid.Parse(folder.Id);
        var folderSnapshot = configSnapshot.Folders.Single(value => value.FolderId == folderId);
        var owner = configSnapshot.Kind.OwnerId;
        var isCore = string.Equals(owner.Value, "folderrewind.core", StringComparison.Ordinal);
        var pluginId = new PluginId(owner.Value);
        var runtime = PluginV3RuntimeService.Runtime;
        var runtimeState = isCore ? PluginRuntimeState.Active : runtime.GetSnapshot(pluginId).State;
        var activePluginId = isCore || runtimeState != PluginRuntimeState.Active ? null : (PluginId?)pluginId;
        var diagnostics = new List<PluginDiagnostic>();

        if (!isCore && runtimeState == PluginRuntimeState.Active)
        {
            using var filePolicyLease = runtime.TryAcquire<IFilePolicyCapability>(
                pluginId,
                capability => capability.Kind == configSnapshot.Kind,
                cancellationToken);
            if (filePolicyLease is not null)
            {
                try
                {
                    using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
                    var policy = await filePolicyLease.Capability.ResolveAsync(
                        new FilePolicyRequest(configSnapshot, folderSnapshot),
                        filePolicyLease.Context).ConfigureAwait(false);
                    MergeFilePolicy(config.Filters, policy);
                    diagnostics.AddRange(policy.Diagnostics);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Blocked(config, folder, configSnapshot, folderSnapshot, activePluginId,
                        diagnostics, "plugin.file_policy_failed", "FilePolicy", owner.Value,
                        new Dictionary<string, string> { ["error"] = ex.Message });
                }
            }
        }

        if (config.BackupScope?.IsPluginScopeEnabled == true)
        {
            using var scopeLease = isCore
                ? null
                : runtime.TryAcquire<IBackupScopeCapability>(
                    pluginId,
                    capability => capability.Kind == configSnapshot.Kind,
                    cancellationToken);
            if (scopeLease is null)
                return Blocked(config, folder, configSnapshot, folderSnapshot, activePluginId,
                    diagnostics, "plugin.backup_scope_mismatch", "BackupScope", owner.Value);

            var backupScope = config.BackupScope ?? new BackupScopeSettings();
            var descriptor = scopeLease.Capability.Scopes.SingleOrDefault(candidate =>
                string.Equals(candidate.Id.OwnerId.Value, backupScope.OwnerId, StringComparison.Ordinal)
                && string.Equals(candidate.Id.ScopeId, backupScope.ScopeId, StringComparison.Ordinal));
            if (descriptor is null
                || !TryBuildScopeParameters(
                    descriptor.FormSchema,
                    backupScope.Parameters ?? new Dictionary<string, string>(),
                    out var parameters))
                return Blocked(config, folder, configSnapshot, folderSnapshot, activePluginId,
                    diagnostics, "plugin.backup_scope_parameters_invalid", "BackupScope", owner.Value);
            try
            {
                using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
                var scope = await scopeLease.Capability.ResolveAsync(
                    new BackupScopeRequest(
                        configSnapshot,
                        folderSnapshot,
                        new BackupScopeId(new OwnerId(backupScope.OwnerId), backupScope.ScopeId),
                        parameters),
                    scopeLease.Context).ConfigureAwait(false);
                diagnostics.AddRange(scope.Diagnostics);
                if (scope.Readiness == OperationReadiness.Blocked)
                    return Blocked(config, folder, configSnapshot, folderSnapshot, activePluginId,
                        diagnostics, "plugin.backup_scope_blocked", "BackupScope", owner.Value);
                folder.SourceScope = new BackupSourceScope
                {
                    Mode = BackupSourceScopeMode.Include,
                    IncludePatterns = new ObservableCollection<string>(
                        BackupSourceScopePatternSet.NormalizeAndValidate(scope.IncludePatterns))
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Blocked(config, folder, configSnapshot, folderSnapshot, activePluginId,
                    diagnostics, "plugin.backup_scope_failed", "BackupScope", owner.Value,
                    new Dictionary<string, string> { ["error"] = ex.Message });
            }
        }

        return new(
            config,
            folder,
            EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters),
            configSnapshot,
            folderSnapshot,
            activePluginId,
            diagnostics,
            IsBlocked: false);
    }

    private static HistorySourceBoundaryResolution Blocked(
        BackupConfig config,
        ManagedFolder folder,
        ConfigSnapshot configSnapshot,
        FolderSnapshot folderSnapshot,
        PluginId? activePluginId,
        List<PluginDiagnostic> diagnostics,
        string code,
        string capability,
        string owner,
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        diagnostics.Add(new PluginDiagnostic(
            code,
            DiagnosticSeverity.Error,
            capability,
            owner,
            arguments ?? new Dictionary<string, string>()));
        return new(
            config,
            folder,
            EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters),
            configSnapshot,
            folderSnapshot,
            activePluginId,
            diagnostics,
            IsBlocked: true);
    }

    private static bool TryBuildScopeParameters(
        JsonElement schema,
        IEnumerable<KeyValuePair<string, string>> values,
        out IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var properties = schema.ValueKind == JsonValueKind.Object
                         && schema.TryGetProperty("properties", out var propertyElement)
                         && propertyElement.ValueKind == JsonValueKind.Object
            ? propertyElement
            : default;
        foreach (var pair in values)
        {
            var type = properties.ValueKind == JsonValueKind.Object
                       && properties.TryGetProperty(pair.Key, out var definition)
                       && definition.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : "string";
            switch (type)
            {
                case "boolean" when bool.TryParse(pair.Value, out var boolean):
                    result[pair.Key] = JsonSerializer.SerializeToElement(boolean);
                    break;
                case "integer" when long.TryParse(pair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                    result[pair.Key] = JsonSerializer.SerializeToElement(integer);
                    break;
                case "boolean" or "integer":
                    parameters = new Dictionary<string, JsonElement>();
                    return false;
                default:
                    result[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
                    break;
            }
        }
        parameters = result;
        return true;
    }

    private static void MergeFilePolicy(FilterSettings filters, FilePolicyResult policy)
    {
        filters.Blacklist ??= new ObservableCollection<string>();
        filters.BackupWhitelist ??= new ObservableCollection<string>();
        foreach (var exclusion in policy.RequiredExclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
            BackupFilterRulePolicy.AddDistinct(filters.Blacklist, exclusion);
        if (filters.BackupFilterMode != BackupFilterMode.Whitelist) return;
        foreach (var inclusion in policy.RequiredInclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
            BackupFilterRulePolicy.AddDistinct(filters.BackupWhitelist, inclusion);
    }
}
