using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// 分离 Boundary Resolution 与 Capture Readiness 状态。
/// </summary>
internal sealed class PluginV3BackupSourceResolution
{
    public PluginV3BackupSourceResolution(
        BackupConfig effectiveConfig,
        ManagedFolder effectiveFolder,
        OperationResolution resolution,
        IReadOnlyList<PluginDiagnostic> diagnostics,
        EffectiveSourceBoundarySnapshot effectiveBoundary,
        bool isBoundaryBlocked = false,
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
        IsBoundaryBlocked = isBoundaryBlocked;
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
    public bool IsBoundaryBlocked { get; }
    public bool IsCaptureBlocked => IsBoundaryBlocked || Resolution.Readiness == OperationReadiness.Blocked;
    public bool IsBlocked => IsCaptureBlocked;
    public PluginId? PluginId { get; }
    public ConfigSnapshot? ConfigSnapshot { get; }
    public FolderSnapshot? FolderSnapshot { get; }
    public ConsistencyIntent Intent { get; }
}

/// <summary>
/// 负责在备份事务开始时为备份源解析有效策略与逻辑管理边界。
/// 确定“按当前配置与插件能力，该 Source 逻辑管理哪些文件”，
/// 但不获取 Consistency Lease 或执行任何数据快照。
/// 独立于一致性捕获就绪状态（Consistency Capability Probe）。
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

        var boundaryResolution = await HistorySourceBoundaryResolver.ResolveAsync(
            originalConfig,
            originalFolder,
            cancellationToken).ConfigureAwait(false);
        var config = boundaryResolution.EffectiveConfig;
        var folder = boundaryResolution.EffectiveFolder;
        var configSnapshot = boundaryResolution.ConfigSnapshot;
        var folderSnapshot = boundaryResolution.FolderSnapshot;
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

        var captureResolution = PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
            declaration,
            PluginOperationKind.Backup,
            isCore ? PluginRuntimeState.Active : runtimeState,
            providerScopeSelected,
            scopeProbe is not null,
            intent,
            isCore || consistencyProbe is not null,
            false));
        var diagnostics = captureResolution.Diagnostics.Concat(boundaryResolution.Diagnostics).ToList();

        var partial = folder.SourceScope.IsPartial || providerScopeSelected;
        if (captureResolution.Readiness == OperationReadiness.Degraded
            && (config.Archive.Mode != BackupMode.Full || partial))
        {
            diagnostics.Add(new PluginDiagnostic(
                "plugin.backup_raw_fallback_requires_complete_full",
                DiagnosticSeverity.Error,
                "Backup",
                owner.Value,
                new Dictionary<string, string>()));
            captureResolution = new OperationResolution(OperationReadiness.Blocked, diagnostics);
        }

        var activePluginId = boundaryResolution.ActivePluginId;

        if (boundaryResolution.IsBlocked)
        {
            return new PluginV3BackupSourceResolution(
                config,
                folder,
                new OperationResolution(OperationReadiness.Blocked, diagnostics),
                diagnostics,
                boundaryResolution.Boundary,
                isBoundaryBlocked: true,
                activePluginId,
                configSnapshot,
                folderSnapshot,
                intent);
        }

        if (NativeHistoryArtifactTransformPolicy.MustBlock(config.ArtifactTransformPolicy))
        {
            var diagnosticOwner = string.IsNullOrWhiteSpace(config.ArtifactTransformPolicy?.Transformer?.PluginId)
                ? owner.Value
                : config.ArtifactTransformPolicy.Transformer.PluginId;
            return Block(config, folder, NativeHistoryArtifactTransformPolicy.BlockedDiagnosticCode, diagnosticOwner, diagnostics, activePluginId, configSnapshot, folderSnapshot, intent);
        }

        var effectiveBoundary = boundaryResolution.Boundary;
        return new PluginV3BackupSourceResolution(
            config,
            folder,
            new OperationResolution(captureResolution.Readiness, diagnostics),
            diagnostics,
            effectiveBoundary,
            isBoundaryBlocked: false,
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

    private static PluginV3BackupSourceResolution Block(
        BackupConfig config,
        ManagedFolder folder,
        string code,
        string owner,
        List<PluginDiagnostic> diagnostics,
        PluginId? pluginId,
        ConfigSnapshot? configSnapshot,
        FolderSnapshot? folderSnapshot,
        ConsistencyIntent intent,
        string capability = "BackupScope",
        IReadOnlyDictionary<string, string>? arguments = null)
    {
        diagnostics.Add(new PluginDiagnostic(
            code,
            DiagnosticSeverity.Error,
            capability,
            owner,
            arguments ?? new Dictionary<string, string>()));
        var boundary = EffectiveSourceBoundaryFactory.Create(folder.Path, folder.SourceScope, config.Filters);
        return new PluginV3BackupSourceResolution(
            config,
            folder,
            new OperationResolution(OperationReadiness.Blocked, diagnostics),
            diagnostics,
            boundary,
            isBoundaryBlocked: true,
            pluginId,
            configSnapshot,
            folderSnapshot,
            intent);
    }

}
