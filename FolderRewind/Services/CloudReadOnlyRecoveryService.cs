using FolderRewind.History.Application;
using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class CloudReadOnlyRecoveryService
{
    private static (RepresentationRuntime Representations, NativeArtifactStore Store) CreateRepresentations(HistoryRuntime runtime, string? password)
    {
        var executable = SevenZipExecutableLocator.Resolve(ConfigService.CurrentConfig.GlobalSettings.SevenZipPath);
        var backend = new SevenZipArchiveProcessBackend(() => executable,
            () => password, !string.IsNullOrEmpty(password), BackupService.InternalRestoreMarkerDirectoryName);
        var snapshot = new BackupConfig { Id = runtime.ConfigId.Value, Name = runtime.ConfigId.Value, IsEncrypted = !string.IsNullOrEmpty(password) };
        var root = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "downloaded-native-v2");
        HostArtifactWorkspace.CreatePrivateDirectory(root);
        var store = new NativeArtifactStore(root, backend);
        return (HistoryRepresentationRuntimeFactory.Create(new(snapshot, runtime, backend, () => store)), store);
    }

    internal static async Task<HistoryRecoveryPreview> PreviewAsync(HistoryRuntime runtime, VersionId versionId,
        CancellationToken token = default)
    {
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var graph = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        var assessment = await CreateRepresentations(runtime, null).Representations.AssessVersionAsync(versionId, graph.Values.ToArray(),
            await NativeHistoryApplicationService.BuildEnvironmentAsync(runtime, token).ConfigureAwait(false), AssessmentDepth.Fast,
            HistoryRecoveryPreview.FidelityFor(version), token).ConfigureAwait(false);
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var local = (catalog?.Entries ?? []).Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && File.Exists(e.Locator.AbsolutePath)).Select(e => e.RepresentationId).ToHashSet();
        var sizes = new System.Collections.Generic.Dictionary<RepresentationId, long>();
        if (assessment.Selected is not null)
            foreach (var representation in ExactReplicaPreparation.Closure(graph[assessment.Selected.RepresentationId], graph))
                foreach (var replica in await GetActiveCloudReplicasAsync(runtime, representation.RepresentationId, token).ConfigureAwait(false))
                    if (!sizes.ContainsKey(representation.RepresentationId))
                    {
                        if (representation.Kind != RepresentationKind.PluginArtifact) sizes.Add(representation.RepresentationId, replica.ExpectedSize!.Value);
                        else if (representation.RepresentationSpecificMetadata.TryGetValue("nativeClosureStorageBytes", out var declared)
                            && long.TryParse(declared, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes)
                            && bytes is >= 0 and <= 16L * 1024 * 1024 * 1024)
                            sizes.Add(representation.RepresentationId, checked(bytes + replica.ExpectedSize!.Value));
                    }
        return HistoryRecoveryPreview.Create(version, graph, assessment, local.Contains,
            id => sizes.TryGetValue(id, out var bytes) ? bytes : null);
    }

    private static async Task<System.Collections.Generic.IReadOnlyList<StorageReplica>> GetActiveCloudReplicasAsync(
        HistoryRuntime runtime, RepresentationId id, CancellationToken token)
    {
        var result = new System.Collections.Generic.List<StorageReplica>();
        foreach (var replica in (await runtime.Query.GetStorageReplicasAsync(id, token).ConfigureAwait(false))
            .Where(r => r.ProviderKind == ReplicaProviderKind.Cloud && r.ExpectedSize is >= 0 && r.ExpectedStorageSha256 is not null)
            .OrderBy(r => r.ReplicaId.ToString(), StringComparer.Ordinal))
        {
            var updates = await runtime.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, token).ConfigureAwait(false);
            if (HistoryCloudReplicaValidity.IsActive(updates)) result.Add(replica);
        }
        return result;
    }

    public static async Task RestoreToNewLocationAsync(HistoryRuntime runtime, RcloneExecutionContext connection,
        VersionId versionId, string destination, string? password, CancellationToken token = default,
        RepresentationId? confirmedRepresentation = null, Action<RepresentationId, bool>? progress = null)
    {
        var version = await runtime.Query.GetVersionAsync(versionId, token).ConfigureAwait(false) ?? throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var consumer = CreateRepresentations(runtime, password);
        var representations = consumer.Representations;
        var preview = await PreviewAsync(runtime, versionId, token).ConfigureAwait(false);
        if (!preview.CanPrepare || confirmedRepresentation is { } expected && preview.Assessment.Selected?.RepresentationId != expected)
            throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var fidelity = preview.RequiredFidelity;
        var graph = (await runtime.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        var transport = new RcloneNativeHistoryTransport(connection);
        var replicas = new HistoryReplicaSyncService(runtime, transport, _ => throw new InvalidOperationException("Read-only recovery cannot sync metadata to the remote."), new NoRetirement());
        var catalog = (await runtime.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        var local = (catalog?.Entries ?? []).Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && File.Exists(e.Locator.AbsolutePath)).Select(e => e.RepresentationId).ToHashSet();
        var payloadRoot = Path.Combine(runtime.Repository.Paths.LocalStateRoot, "downloaded-payloads");
        Directory.CreateDirectory(payloadRoot);
        await ExactReplicaPreparation.PrepareAsync([(version.SourceId, versionId)], graph,
            async (id, depth, ct) => await representations.AssessVersionAsync(id, graph.Values.ToArray(), await NativeHistoryApplicationService.BuildEnvironmentAsync(runtime, ct).ConfigureAwait(false), depth, fidelity, ct).ConfigureAwait(false),
            id => local.Contains(id), async (source, representation, ct) =>
            {
                progress?.Invoke(representation.RepresentationId, false);
                string? diagnostic = null;
                foreach (var replica in await GetActiveCloudReplicasAsync(runtime, representation.RepresentationId, ct).ConfigureAwait(false))
                {
                    var manifest = new HistoryReplicaManifest(representation.RepresentationId, replica.ReplicaId,
                        HistoryCloudReplicaValidity.ArtifactRoot(representation), replica.ObjectKey, replica.ExpectedSize!.Value, replica.ExpectedStorageSha256!);
                    var downloaded = representation.Kind == RepresentationKind.PluginArtifact
                        ? await new NativeArtifactReplicaService(consumer.Store, replicas).DownloadAsync(representation, manifest, runtime.ConfigId.Value, source.Value, ct).ConfigureAwait(false)
                        : await replicas.DownloadAsync(manifest, Path.Combine(payloadRoot, representation.RepresentationId + ".payload"), ct).ConfigureAwait(false);
                    if (downloaded.Status == HistoryReplicaOperationStatus.Succeeded) { local.Add(representation.RepresentationId); progress?.Invoke(representation.RepresentationId, true); return true; }
                    diagnostic = downloaded.Diagnostic;
                }
                throw new InvalidDataException(I18n.Format("CloudRecovery_PayloadFailed", representation.RepresentationId,
                    CloudCommandSecurity.Redact(diagnostic ?? I18n.GetString("CloudRecovery_NoActiveReplica"))));
            }, token).ConfigureAwait(false);
        var protectedRoots = ConfigService.CurrentConfig.BackupConfigs.SelectMany(c => c.SourceFolders.Select(s => s.Path).Append(c.DestinationPath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Append(ConfigService.ConfigDirectory).Append(runtime.Repository.Paths.RepositoryRoot).ToArray();
        await new HistoryVersionExportService(representations).ExportAsync(versionId, graph.Values.ToArray(),
            await NativeHistoryApplicationService.BuildEnvironmentAsync(runtime, token).ConfigureAwait(false), destination, protectedRoots, token, fidelity).ConfigureAwait(false);
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool releaseVersion, CancellationToken token)
            => throw new InvalidOperationException("Read-only recovery cannot retire replicas.");
    }
}
