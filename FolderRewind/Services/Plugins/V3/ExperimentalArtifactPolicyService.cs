using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using System;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>Product availability for the opt-in incremental slice, based on declared capabilities.</summary>
internal static class ExperimentalArtifactPolicyService
{
    internal const string UnavailableDiagnostic = "plugin.experimental_artifact_unavailable";
    internal static bool SupportsConfiguration(BackupConfig config)
        => NativeHistoryArtifactTransformPolicy.SupportsCapture(config.Archive.Mode switch
            { BackupMode.Full => CoreCaptureMode.Full, BackupMode.Smart => CoreCaptureMode.Smart, _ => CoreCaptureMode.Rolling },
            config.SourceFolders.Count, config.BackupScope?.IsPluginScopeEnabled == true, config.SourceFolders.Any(f => f.SourceScope.IsPartial));

    internal static ArtifactTransformerId? FindProvider(BackupConfig config)
    {
        var kind = new ConfigKindRef(new(config.Kind.OwnerId), config.Kind.KindId);
        var candidates = PluginV3RuntimeService.GetActivePlugins()
            .Select(id => PluginV3RuntimeService.Runtime.GetActiveManifest(id))
            .Where(m => m is not null)
            .SelectMany(m => m!.ArtifactTransformers.Where(d => d.SupportsIncrementalInput
                && d.CompatibleConfigKinds.Contains(kind)
                && d.CompatibleCoreModes.Contains(CoreCaptureMode.Full) && d.CompatibleCoreModes.Contains(CoreCaptureMode.Smart)
                && d.CompatibleCompleteness.Contains(ArtifactCompleteness.Complete) && d.CompatibleCompleteness.Contains(ArtifactCompleteness.Partial)
                && d.SupportedFailureBehaviors.Contains(ArtifactTransformFailureBehavior.KeepPrimaryWithWarnings)
                && d.SupportedFailureBehaviors.Contains(ArtifactTransformFailureBehavior.RequireTransform)
                && HasIncrementalParameter(d.ParameterSchema))).ToArray();
        if (candidates.Length != 1) return null;
        var candidate = candidates[0];
        using var transform = PluginV3RuntimeService.Runtime.TryAcquire<IBackupArtifactTransformerCapability>(candidate.TransformerId.PluginId,
            c => c.TransformerId == candidate.TransformerId, default);
        using var materializer = PluginV3RuntimeService.Runtime.TryAcquire<IRestoreMaterializerCapability>(candidate.TransformerId.PluginId,
            c => c is IArtifactFileViewProvider, default);
        return transform is not null && materializer is not null ? candidate.TransformerId : null;
    }

    private static bool HasIncrementalParameter(JsonElement schema)
        => schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("representation", out var representation) && representation.ValueKind == JsonValueKind.Object
            && representation.TryGetProperty("enum", out var choices)
            && choices.ValueKind == JsonValueKind.Array && choices.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "incremental-v1");

    internal static string? UnavailableReason(BackupConfig config)
    {
        if (config.ArtifactTransformPolicy?.ExperimentalEnabled == true
            && !NativeHistoryArtifactTransformPolicy.IsExperimentalEnabled(config.ArtifactTransformPolicy)) return "SemanticBackup_ProviderUnavailable";
        if (!SupportsConfiguration(config)) return "SemanticBackup_UnsupportedConfiguration";
        var provider = FindProvider(config);
        if (provider is null) return "SemanticBackup_ProviderUnavailable";
        if (config.ArtifactTransformPolicy is { } policy && (!NativeHistoryArtifactTransformPolicy.IsIncremental(policy)
            || policy.Transformer.PluginId != provider.Value.PluginId.Value || policy.Transformer.TransformerId != provider.Value.TransformerId))
            return "SemanticBackup_ProviderUnavailable";
        return null;
    }
    internal static string Describe(PluginDiagnostic diagnostic)
        => diagnostic.Code == UnavailableDiagnostic && diagnostic.Arguments.TryGetValue("resourceKey", out var key)
            && key is "SemanticBackup_UnsupportedConfiguration" or "SemanticBackup_ProviderUnavailable"
                ? I18n.GetString(key) : $"{diagnostic.Code} ({diagnostic.Owner})";
}
