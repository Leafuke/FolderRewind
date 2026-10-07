using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services.Plugins.V3;

internal static class NativeArtifactHostService
{
    internal static async ValueTask<IAsyncDisposable> AcquireCompatibilityGuardAsync(PluginManifestContract manifest, CancellationToken token)
    {
        var gates = new System.Collections.Generic.List<IDisposable>();
        try
        {
            var stores = ConfigService.CurrentConfig.BackupConfigs.Where(c => !string.IsNullOrWhiteSpace(c.DestinationPath))
                .Select(c => (Config: c, Root: NativeArtifactStore.ForConfig(c.DestinationPath, c.Id)))
                .Where(p => Directory.Exists(p.Root)).DistinctBy(p => p.Root, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Root, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var item in stores)
            {
                var store = Store(item.Config); gates.Add(await store.EnterAsync(token));
                _ = store.Pending(); // fail closed for corrupt intents during update
                foreach (var node in (await store.Ledger.LoadAsync(token)).Artifacts.Where(a => a.Format.OwnerId.Value == manifest.PluginId.Value))
                {
                    if (!manifest.ArtifactFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)
                        || !manifest.RestoreStrategies.Any(s => s.RestoreStrategyId == node.RestoreStrategyId
                            && s.SupportedCompleteness.Contains(node.Completeness)
                            && s.SupportedFormats.Any(f => f.Format == node.Format && f.MinimumVersion <= node.FormatVersion && f.MaximumVersion >= node.FormatVersion)))
                        throw new InvalidDataException("Candidate cannot read protected native Artifact format.");
                }
            }
            return new CompatibilityGuard(gates);
        }
        catch { foreach (var gate in gates.AsEnumerable().Reverse()) gate.Dispose(); throw; }
    }
    private sealed class CompatibilityGuard(System.Collections.Generic.List<IDisposable> gates) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { foreach (var gate in gates.AsEnumerable().Reverse()) gate.Dispose(); return ValueTask.CompletedTask; }
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task>> Recoveries = new();
    internal static async Task RecoverOnceAsync(BackupConfig config, HistoryRuntime history, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationPath)) return;
        var root = NativeArtifactStore.ForConfig(config.DestinationPath, config.Id);
        if (!Directory.Exists(root)) return;
        try { await Recoveries.GetOrAdd(root, _ => new Lazy<Task>(() => NativeArtifactHistoryRecovery.RecoverAsync(history, Store(config), token))).Value; }
        catch { Recoveries.TryRemove(root, out _); throw; }
    }
    internal static NativeArtifactStore Store(BackupConfig config)
        => new(NativeArtifactStore.ForConfig(config.DestinationPath, config.Id), new SevenZipHistoryArchiveBackend(config)
#if NATIVE_ARTIFACT_ACCEPTANCE
            , Development.NativeArtifactAcceptance.Observe
#endif
            );

    internal static async Task<SourceCaptureResult> TransformAsync(BackupConfig config, PluginV3BackupSourceResolution resolution,
        SourceCaptureResult capture, CancellationToken token)
    {
        if (config.ArtifactTransformPolicy is not { } settings || capture.Outcome != SourceCaptureOutcome.Captured) return capture;
        if (!NativeHistoryArtifactTransformPolicy.IsEnabled(config.Id)) throw new InvalidOperationException("Native transform acceptance is disabled.");
        if (config.Archive.Mode != BackupMode.Full)
        {
            if (settings.FailureBehavior == PersistedArtifactTransformFailureBehavior.RequireTransform)
                throw new InvalidOperationException("P2 Artifact transformation requires Full mode; Smart and Rolling are not converted.");
            return capture.WithVersionMetadata(capture.VersionMetadataCandidates,
                [new HistoryDiagnostic("plugin.artifact_primary_kept", HistoryDiagnosticSeverity.Warning, "P2 requires Full mode.")]);
        }
        var policy = new ArtifactTransformPolicy(new(new(settings.Transformer.PluginId), settings.Transformer.TransformerId),
            settings.Parameters, (ArtifactTransformFailureBehavior)settings.FailureBehavior);
        return await new NativeArtifactCaptureService(PluginV3RuntimeService.Runtime, Store(config),
            new SevenZipHistoryArchiveBackend(config), config.IsEncrypted).TransformAsync(capture,
            resolution.ConfigSnapshot ?? throw new InvalidOperationException("Config snapshot required."),
            resolution.FolderSnapshot ?? throw new InvalidOperationException("Source snapshot required."), policy, token);
    }

    internal static PluginArtifactRepresentationHandler Handler(BackupConfig config, HistoryRuntime history)
        => new(new NativePluginArtifactBackend(PluginV3RuntimeService.Runtime, () => Store(config), async (root, token) =>
        {
            var representations = await history.Query.GetAllRepresentationsAsync(token);
            var representation = representations.Single(r => r.Kind == RepresentationKind.PluginArtifact
                && r.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var id) && Guid.TryParse(id, out var parsed) && parsed == root);
            var version = (await history.Query.GetAllVersionsAsync(token)).Single(v => v.VersionId == representation.VersionId);
            var snapshot = PluginV3ModelMapper.ToSnapshot(config);
            // Source may have been detached from current config; historical identity remains usable.
            var folder = snapshot.Folders.SingleOrDefault(f => f.FolderId == version.SourceId.Value)
                ?? new FolderSnapshot(version.SourceId.Value, string.Empty, "Historical source", new System.Collections.Generic.Dictionary<StateOwnerId, ProviderStateSnapshot>());
            return new NativeArtifactBinding(representation, snapshot, folder);
        }));
}
