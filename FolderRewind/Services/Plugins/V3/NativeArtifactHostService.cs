using System;
using System.IO;
using System.Collections.Immutable;
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
    internal static string SourcePathFingerprint(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant())));
    internal static bool IsIncrementalPolicy(BackupConfig config)
        => NativeHistoryArtifactTransformPolicy.IsIncremental(config.ArtifactTransformPolicy);

    internal static async Task<SourceCaptureBaseline?> RebuildIncrementalBaselineAsync(BackupConfig config,
        Guid sourceId, CancellationToken token)
    {
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
        var workspace = (await history.WorkspaceStore.LoadAsync(token)).Value;
        var state = workspace?.SourceBaselines.SingleOrDefault(s => s.SourceId.Value == sourceId);
        if (state?.Relation != History.LocalState.WorkspaceBaselineRelation.Exact || state.RequiresFullCapture || state.BaseVersionId is not { } version) return null;
        var representations = (await history.Query.GetAllRepresentationsAsync(token)).Where(r => r.VersionId == version
            && r.Kind == RepresentationKind.PluginArtifact && r.RepresentationSpecificMetadata.GetValueOrDefault("incrementalEvidence") == "2")
            .OrderBy(r => r.RepresentationId.ToString(), StringComparer.Ordinal);
        var store = Store(config);
        foreach (var representation in representations)
        {
            try
            {
                var root = new ArtifactId(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]));
                await using var read = await store.OpenLazyAsync(root, token);
                var evidence = await IncrementalCaptureEvidence.ReadAsync(read, root, token);
                if (evidence.SourceId != sourceId || evidence.VersionId != version.ToString()
                    || evidence.RepresentationId != representation.RepresentationId.ToString()
                    || evidence.Fingerprint() != representation.StateFingerprint) throw new InvalidDataException("Capture evidence mismatch.");
                var current = await history.CaptureBaselines.LoadAsync(new(sourceId), token);
                return new(new(sourceId), current?.Revision ?? -1, version, representation.RepresentationId, RepresentationKind.PluginArtifact,
                    store.PayloadPath(read.Nodes.Single(n => n.ArtifactId == root)), evidence.CapturesSinceCheckpoint,
                    evidence.FileStates.ToImmutableSortedDictionary(StringComparer.Ordinal), evidence.BoundaryFingerprint)
                { ArtifactRootId = root.Value, EvidenceIdentity = representation.RepresentationSpecificMetadata["captureEvidenceSha256"],
                    RequiresContentValidation = true, ContentRealizationId = state.ContentRealizationId, Directories = [.. evidence.Directories] };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { /* Try another representation of this exact Version only. */ }
        }
        return null;
    }

    internal static async Task<bool> IsIncrementalBaselineAvailableAsync(BackupConfig config, SourceCaptureBaseline baseline, CancellationToken token)
    {
        if (baseline.ArtifactRootId is not { } root) return false;
        try
        {
            var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token);
            var workspace = (await history.WorkspaceStore.LoadAsync(token)).Value;
            if (workspace?.GetSourceState(baseline.SourceId).Relation != History.LocalState.WorkspaceBaselineRelation.Exact) return false;
            await using var lease = await Store(config).OpenLazyAsync(new(root), token);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { return false; }
    }

    internal static async Task<SourceCaptureBaseline> RevalidateStatisticsAsync(BackupConfig config, string source,
        SourceCaptureBaseline baseline, System.Collections.Generic.IReadOnlyDictionary<string, SourceCaptureFileState> current, CancellationToken token)
    {
        await using var read = await Store(config).OpenLazyAsync(new(baseline.ArtifactRootId!.Value), token);
        var evidence = await IncrementalCaptureEvidence.ReadAsync(read, new(baseline.ArtifactRootId.Value), token);
        var states = baseline.FileStates.ToBuilder();
        foreach (var file in evidence.Files)
        {
            if (!current.TryGetValue(file.RelativePath, out var stat)) continue;
            var path = ArtifactPathRules.ResolveUnderRoot(source, file.RelativePath);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token));
            states[file.RelativePath] = input.Length == file.Length && StringComparer.OrdinalIgnoreCase.Equals(hash, file.Sha256)
                ? stat : new SourceCaptureFileState(-1, DateTime.MinValue);
        }
        return baseline with { FileStates = states.ToImmutable(), RequiresContentValidation = false };
    }
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
    internal static Task RecoverInsideOperationAsync(BackupConfig config, HistoryRuntime history,
        NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken token)
    {
        operation.Require(history.ConfigId);
        if (string.IsNullOrWhiteSpace(config.DestinationPath)
            || !Directory.Exists(NativeArtifactStore.ForConfig(config.DestinationPath, config.Id))) return Task.CompletedTask;
        return NativeArtifactHistoryRecovery.RecoverAsync(history, Store(config), token);
    }
    internal static NativeArtifactStore Store(BackupConfig config, IArtifactEnvelopeCodec? codec = null)
        => new(NativeArtifactStore.ForConfig(config.DestinationPath, config.Id), codec ?? new SevenZipHistoryArchiveBackend(config)
#if NATIVE_ARTIFACT_ACCEPTANCE
            , Development.NativeArtifactAcceptance.Observe
#endif
            );

    internal static Task<SourceCaptureResult> CaptureDirectAsync(BackupConfig config, PluginV3BackupSourceResolution resolution,
        FileChangeCapturePlan plan, SourceCaptureBaselineCandidate statistics, EffectiveSourceBoundarySnapshot boundary,
        string stableSource, CancellationToken token)
    {
        if (NativeHistoryArtifactTransformPolicy.MustBlock(config.ArtifactTransformPolicy, config.Id))
            throw new InvalidOperationException("Artifact transformation has not been explicitly enabled.");
        var settings = config.ArtifactTransformPolicy ?? throw new InvalidOperationException("Transform policy required.");
        var policy = new ArtifactTransformPolicy(new(new(settings.Transformer.PluginId), settings.Transformer.TransformerId),
            settings.Parameters, (ArtifactTransformFailureBehavior)settings.FailureBehavior);
        return new NativeArtifactIncrementalCaptureService(PluginV3RuntimeService.Runtime, Store(config),
            new SevenZipHistoryArchiveBackend(config), config.IsEncrypted).TransformAsync(null, plan,
                resolution.ConfigSnapshot!, resolution.FolderSnapshot!, policy, token, stableSource, boundary, statistics);
    }

    internal static async Task<SourceCaptureResult> TransformAsync(BackupConfig config, PluginV3BackupSourceResolution resolution,
        SourceCaptureResult capture, CancellationToken token, SourceCaptureBaseline? baseline = null, bool stableSource = false)
    {
        if (config.ArtifactTransformPolicy is not { } settings || capture.Outcome != SourceCaptureOutcome.Captured) return capture;
        if (NativeHistoryArtifactTransformPolicy.MustBlock(config.ArtifactTransformPolicy, config.Id))
            throw new InvalidOperationException("Artifact transformation has not been explicitly enabled.");
        if (IsIncrementalPolicy(config))
        {
            if (!stableSource) throw new InvalidOperationException("Incremental Artifact capture requires a stable source view.");
            var incrementalPolicy = new ArtifactTransformPolicy(new(new(settings.Transformer.PluginId), settings.Transformer.TransformerId),
                settings.Parameters, (ArtifactTransformFailureBehavior)settings.FailureBehavior);
            return await new NativeArtifactIncrementalCaptureService(PluginV3RuntimeService.Runtime, Store(config),
                new SevenZipHistoryArchiveBackend(config), config.IsEncrypted).TransformAsync(capture,
                FileChangeCapturePlan.Create(capture, baseline),
                resolution.ConfigSnapshot!, resolution.FolderSnapshot!, incrementalPolicy, token);
        }
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

    internal static PluginArtifactRepresentationHandler Handler(BackupConfig config, HistoryRuntime history, IArtifactEnvelopeCodec? codec = null)
        => new(new NativePluginArtifactBackend(PluginV3RuntimeService.Runtime, () => Store(config, codec), async (root, token) =>
        {
            var representations = await history.Query.GetAllRepresentationsAsync(token);
            var representation = representations.Single(r => r.Kind == RepresentationKind.PluginArtifact
                && r.RepresentationSpecificMetadata.TryGetValue("artifactRootId", out var id) && Guid.TryParse(id, out var parsed) && parsed == root);
            var version = (await history.Query.GetAllVersionsAsync(token)).Single(v => v.VersionId == representation.VersionId);
            var snapshot = PluginV3ModelMapper.ToSnapshot(config);
            // Source may have been detached from current config; historical identity remains usable.
            var folder = snapshot.Folders.SingleOrDefault(f => f.FolderId == version.SourceId.Value)
                ?? new FolderSnapshot(version.SourceId.Value, string.Empty, "Historical source", new System.Collections.Generic.Dictionary<StateOwnerId, ProviderStateSnapshot>());
            var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token)).Value;
            return new NativeArtifactBinding(representation, snapshot, folder)
            { LocalRegistered = catalog?.Entries.Any(e => e.RepresentationId == representation.RepresentationId) == true };
        }));
}
