using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>
/// 封装单个备份源的有效策略解析结果，包含解析后的克隆配置与文件夹、
/// 诊断集合以及权威的有效备份边界（Effective Source Boundary）。
/// </summary>
internal sealed class PluginV3BackupSourceResolution
{
    public PluginV3BackupSourceResolution(
        BackupConfig effectiveConfig,
        ManagedFolder effectiveFolder,
        OperationResolution resolution,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        EffectiveSourceBoundarySnapshot effectiveBoundary,
        PluginId? pluginId = null,
        ConfigSnapshot? configSnapshot = null,
        FolderSnapshot? folderSnapshot = null,
        ConsistencyIntent intent = ConsistencyIntent.Prefer)
    {
        EffectiveConfig = effectiveConfig ?? throw new ArgumentNullException(nameof(effectiveConfig));
        EffectiveFolder = effectiveFolder ?? throw new ArgumentNullException(nameof(effectiveFolder));
        Resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        EffectiveBoundary = effectiveBoundary ?? throw new ArgumentNullException(nameof(effectiveBoundary));
        PluginId = pluginId;
        ConfigSnapshot = configSnapshot;
        FolderSnapshot = folderSnapshot;
        Intent = intent;
    }

    public BackupConfig EffectiveConfig { get; }
    public ManagedFolder EffectiveFolder { get; }
    public OperationResolution Resolution { get; }
    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
    public EffectiveSourceBoundarySnapshot EffectiveBoundary { get; }
    public PluginId? PluginId { get; }
    public ConfigSnapshot? ConfigSnapshot { get; }
    public FolderSnapshot? FolderSnapshot { get; }
    public ConsistencyIntent Intent { get; }
    public bool IsBlocked => Resolution.Readiness == OperationReadiness.Blocked;
}

/// <summary>
/// 负责在备份事务开始时为备份源解析有效策略与逻辑管理边界。
/// 确定“按当前配置与插件能力，该 Source 逻辑管理哪些文件”，
/// 但不获取 Consistency Lease 或执行任何数据快照。
/// </summary>
internal static class PluginV3BackupSourceResolver
{
    public static async ValueTask<PluginV3BackupSourceResolution> ResolveAsync(
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
        var declaration = isCore
            ? new ConfigKindDeclaration(
                configSnapshot.Kind,
                new LocalizedText("Folder", new Dictionary<string, string>()),
                new LocalizedText("Folder", new Dictionary<string, string>()),
                string.Empty,
                BackupFallbackPolicy.Block,
                RestoreCoordinationPolicy.None)
            : FindInstalledKind(configSnapshot.Kind)
              ?? new ConfigKindDeclaration(
                  configSnapshot.Kind,
                  new LocalizedText(configSnapshot.Kind.KindId, new Dictionary<string, string>()),
                  new LocalizedText(string.Empty, new Dictionary<string, string>()),
                  string.Empty,
                  BackupFallbackPolicy.Block,
                  RestoreCoordinationPolicy.Required);

        using var scopeProbe = isCore
            ? null
            : runtime.TryAcquire<IBackupScopeCapability>(
                pluginId,
                capability => capability.Kind == configSnapshot.Kind,
                cancellationToken);
        using var consistencyProbe = isCore
            ? null
            : runtime.TryAcquire<IBackupConsistencyCapability>(
                pluginId,
                capability => capability.Kind == configSnapshot.Kind,
                cancellationToken);
        var providerScopeSelected = config.BackupScope?.IsPluginScopeEnabled == true;
        var intent = config.ConsistencyIntent == PersistedConsistencyIntent.Require
            ? ConsistencyIntent.Require
            : ConsistencyIntent.Prefer;
        var resolution = PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
            declaration,
            PluginOperationKind.Backup,
            isCore ? PluginRuntimeState.Active : runtimeState,
            providerScopeSelected,
            scopeProbe is not null,
            intent,
            isCore || consistencyProbe is not null,
            false));
        var diagnostics = resolution.Diagnostics.ToList();

        var partial = folder.SourceScope.IsPartial || providerScopeSelected;
        if (resolution.Readiness == OperationReadiness.Degraded
            && (config.Archive.Mode != BackupMode.Full || partial))
        {
            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_raw_fallback_requires_complete_full",
                DiagnosticSeverity.Error,
                "Backup",
                owner.Value,
                new Dictionary<string, string>()));
            resolution = new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }

        var activePluginId = isCore || runtimeState != PluginRuntimeState.Active ? null : (PluginId?)pluginId;

        if (NativeHistoryArtifactTransformPolicy.MustBlock(config.ArtifactTransformPolicy))
        {
            var diagnosticOwner = string.IsNullOrWhiteSpace(config.ArtifactTransformPolicy?.Transformer?.PluginId)
                ? owner.Value
                : config.ArtifactTransformPolicy.Transformer.PluginId;
            return Block(config, folder, NativeHistoryArtifactTransformPolicy.BlockedDiagnosticCode, diagnosticOwner, diagnostics, activePluginId, configSnapshot, folderSnapshot, intent);
        }

        if (resolution.Readiness == OperationReadiness.Blocked || isCore || runtimeState != PluginRuntimeState.Active)
        {
            var rawBoundary = EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters);
            return new PluginV3BackupSourceResolution(
                config,
                folder,
                resolution,
                diagnostics,
                rawBoundary,
                activePluginId,
                configSnapshot,
                folderSnapshot,
                intent);
        }

        using (var filePolicyLease = runtime.TryAcquire<IFilePolicyCapability>(
            pluginId,
            capability => capability.Kind == configSnapshot.Kind,
            cancellationToken))
        {
            if (filePolicyLease is not null)
            {
                var policy = await filePolicyLease.Capability.ResolveAsync(
                    new FilePolicyRequest(configSnapshot, folderSnapshot),
                    filePolicyLease.Context).ConfigureAwait(false);
                MergeFilePolicy(config.Filters, policy);
                diagnostics.AddRange(policy.Diagnostics);
            }
        }

        if (providerScopeSelected)
        {
            if (scopeProbe is null)
            {
                return Block(config, folder, "plugin.backup_scope_mismatch", owner.Value, diagnostics, activePluginId, configSnapshot, folderSnapshot, intent);
            }
            var backupScope = config.BackupScope ?? new BackupScopeSettings();
            var descriptor = scopeProbe.Capability.Scopes.SingleOrDefault(candidate =>
                string.Equals(candidate.Id.OwnerId.Value, backupScope.OwnerId, StringComparison.Ordinal)
                && string.Equals(candidate.Id.ScopeId, backupScope.ScopeId, StringComparison.Ordinal));
            if (descriptor is null
                || !TryBuildScopeParameters(
                    descriptor.FormSchema,
                    backupScope.Parameters ?? new Dictionary<string, string>(),
                    out var parameters))
            {
                return Block(config, folder, "plugin.backup_scope_parameters_invalid", owner.Value, diagnostics, activePluginId, configSnapshot, folderSnapshot, intent);
            }
            var scope = await scopeProbe.Capability.ResolveAsync(
                new BackupScopeRequest(
                    configSnapshot,
                    folderSnapshot,
                    new BackupScopeId(new OwnerId(backupScope.OwnerId), backupScope.ScopeId),
                    parameters),
                scopeProbe.Context).ConfigureAwait(false);
            diagnostics.AddRange(scope.Diagnostics);
            if (scope.Readiness == OperationReadiness.Blocked)
            {
                return Block(config, folder, "plugin.backup_scope_blocked", owner.Value, diagnostics, activePluginId, configSnapshot, folderSnapshot, intent);
            }
            var patterns = BackupSourceScopePatternSet.NormalizeAndValidate(scope.IncludePatterns);
            folder.SourceScope = new BackupSourceScope
            {
                Mode = BackupSourceScopeMode.Include,
                IncludePatterns = new ObservableCollection<string>(patterns)
            };
        }

        var effectiveBoundary = EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters);
        return new PluginV3BackupSourceResolution(
            config,
            folder,
            new OperationResolution(resolution.Readiness, diagnostics),
            diagnostics,
            effectiveBoundary,
            activePluginId,
            configSnapshot,
            folderSnapshot,
            intent);
    }

    private static ConfigKindDeclaration? FindInstalledKind(ConfigKindRef kind)
        => PluginV3RuntimeService.FindKind(kind)
           ?? PluginV3PackageService.GetInstalledConfigKinds()
               .Where(pair => pair.PluginId.Value == kind.OwnerId.Value)
               .Select(pair => pair.Kind)
               .SingleOrDefault(candidate => candidate.Kind == kind);

    private static bool TryBuildScopeParameters(
        JsonElement schema,
        IReadOnlyDictionary<string, string> values,
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

    private static PluginV3BackupSourceResolution Block(
        BackupConfig config,
        ManagedFolder folder,
        string code,
        string owner,
        List<PluginDiagnostic> diagnostics,
        PluginId? pluginId,
        ConfigSnapshot? configSnapshot,
        FolderSnapshot? folderSnapshot,
        ConsistencyIntent intent)
    {
        diagnostics.Add(new PluginDiagnostic(
            code,
            DiagnosticSeverity.Error,
            "BackupScope",
            owner,
            new Dictionary<string, string>()));
        var boundary = EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters);
        return new PluginV3BackupSourceResolution(
            config,
            folder,
            new OperationResolution(OperationReadiness.Blocked, diagnostics),
            diagnostics,
            boundary,
            pluginId,
            configSnapshot,
            folderSnapshot,
            intent);
    }

    private static void MergeFilePolicy(FilterSettings filters, FilePolicyResult policy)
    {
        filters.Blacklist ??= new ObservableCollection<string>();
        filters.BackupWhitelist ??= new ObservableCollection<string>();
        foreach (var exclusion in policy.RequiredExclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
        {
            BackupFilterRulePolicy.AddDistinct(filters.Blacklist, exclusion);
        }
        if (filters.BackupFilterMode == BackupFilterMode.Whitelist)
        {
            foreach (var inclusion in policy.RequiredInclusions.Where(BackupSourceScopePatternSet.IsSafeRelativePattern))
            {
                BackupFilterRulePolicy.AddDistinct(filters.BackupWhitelist, inclusion);
            }
        }
    }
}
