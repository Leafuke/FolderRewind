using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>A candidate password only decodes a private session; authoritative registration is a later recovery step.</summary>
internal sealed class NativeMigrationConsumerSession : IAsyncDisposable
{
    private OwnedPluginTemporaryStorage? _owner;
    private string? _workspace;
    internal RepresentationRuntime Consumers { get; private init; } = null!;
    internal NativeArtifactStore? Store { get; private init; }
    internal IReadOnlySet<RepresentationId> NativeRepresentations { get; private init; } = new HashSet<RepresentationId>();
    internal IReadOnlySet<RepresentationId> CredentialProtected { get; private init; } = new HashSet<RepresentationId>();

    internal static async Task<HistoryTransferProjectMigration?> IncomingAsync(BackupConfig snapshot, CancellationToken token)
    {
        var state = DataMigrationStateStore.Load(ConfigService.ConfigDirectory, new(snapshot.Id));
        if (state is not { BundlePath.Length: > 0 }) return null;
        var preview = await new HistoryMigrationBundleService().PreviewAsync(state.BundlePath, token).ConfigureAwait(false);
        if (preview.History.FileSha256 != state.SourceFileSha256) throw new IOException("Migration source changed.");
        return preview.History.Projects.Single(p => p.ConfigId.Matches(snapshot.Id)).Migration;
    }

    internal static async Task<bool> RequiresCredentialAsync(BackupConfig snapshot, HistoryRuntime history, CancellationToken token)
    {
        if (snapshot.IsEncrypted) return true;
        var native = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).Where(NativePluginArtifactBackend.IsSupported).ToArray();
        if (native.Length == 0) return false;
        var incoming = await IncomingAsync(snapshot, token).ConfigureAwait(false);
        foreach (var payload in incoming?.NativePayloads.IsDefault == false ? incoming.NativePayloads : [])
        {
            var path = HistoryMigrationBundleService.InstalledNativePath(snapshot.DestinationPath, payload.EntryName);
            var descriptor = ArtifactMetadataFile.ReadJson<NativeArtifactObjectClosure>(path, 16 * 1024 * 1024) ?? throw new InvalidDataException();
            if (descriptor.Objects.Any(o => o.Envelope.Encrypted)) return true;
        }
        var root = NativeArtifactStore.ForConfig(snapshot.DestinationPath, snapshot.Id);
        if (!Directory.Exists(root)) return false;
        var store = Plugins.V3.NativeArtifactHostService.Store(snapshot, BackupPasswordPreparationService.CreateBackend(snapshot));
        using var held = await store.EnterAsync(token).ConfigureAwait(false);
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var nodes = ledger.Artifacts.ToDictionary(a => a.ArtifactId);
        var inspected = new HashSet<ArtifactId>();
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        foreach (var representation in native.Where(r => catalog?.Entries.Any(e => e.RepresentationId == r.RepresentationId) == true))
            foreach (var id in NativeArtifactClosureLimits.Validate(ledger, new ArtifactId(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]))).Reachable)
            {
                if (!inspected.Add(id)) continue;
                var path = Path.Combine(Path.GetDirectoryName(store.PayloadPath(nodes[id]))!, "envelope.json");
                HistoryReplicaRecoveryService.RequireRegularPath(root, path);
                if (ArtifactMetadataFile.ReadJson<NativeEnvelope>(path, 4096)?.Encrypted == true) return true;
            }
        return false;
    }

    internal static async Task<string> EvidenceAsync(BackupConfig snapshot, HistoryRuntime history, CancellationToken token)
    {
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (!graph.Any(r => NativePluginArtifactBackend.IsSupported(r) && catalog?.Entries.Any(e => e.RepresentationId == r.RepresentationId) == true)) return "";
        var root = NativeArtifactStore.ForConfig(snapshot.DestinationPath, snapshot.Id);
        if (!Directory.Exists(root)) throw new IOException("Native recovery storage is missing.");
        return await NativeArtifactMaterializationEvidence.CaptureAsync(history.ConfigId,
            Plugins.V3.NativeArtifactHostService.Store(snapshot, BackupPasswordPreparationService.CreateBackend(snapshot)), graph,
            await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false), catalog?.Entries ?? [], token).ConfigureAwait(false);
    }

    internal static async Task<(HistoryReplicaRecoveryPreview Preview, int Registered)> RegisterAsync(DataMigrationRecoveryContext context,
        IArchiveRepresentationBackend archive, CancellationToken token)
    {
        var history = context.Runtime; var snapshot = context.Snapshot;
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        if (!graph.Any(NativePluginArtifactBackend.IsSupported)) return (context.Replicas, 0);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(history.ConfigId, token).ConfigureAwait(false);
        await using var mutation = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var catalog = await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
        if (catalog.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible
            || (catalog.Value?.CatalogRevision ?? -1) != context.Replicas.CatalogRevision)
            throw new DeviceLocalStateConflictException("Replica catalog changed after Native migration preview.");
        await using var session = await OpenAsync(snapshot, history, archive, token).ConfigureAwait(false);
        var environment = new RepresentationEnvironment(catalog.Value?.Entries ?? [], [], []);
        foreach (var representation in graph.Where(r => session.NativeRepresentations.Contains(r.RepresentationId)))
            if ((await session.Consumers.AssessVersionAsync(representation.VersionId, [representation], environment, AssessmentDepth.Deep,
                MaterializationFidelity.Exact, token).ConfigureAwait(false)).Readiness != HistoryReadiness.Ready)
                throw new InvalidDataException("Native migration requires complete reconstructed source evidence before registration.");
        if (session.NativeRepresentations.Count == 0) return (context.Replicas, 0);
        var decoder = new ReadOnlyArtifactEnvelopeCodec((IBoundedArtifactEnvelopeCodec)archive);
        var store = Plugins.V3.NativeArtifactHostService.Store(snapshot, decoder);
        var incoming = await IncomingAsync(snapshot, token).ConfigureAwait(false);
        if (incoming is not null) await ImportIncomingAsync(snapshot, incoming, store, token).ConfigureAwait(false);
        var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
        var entries = (catalog.Value?.Entries ?? []).ToList(); var registered = 0;
        foreach (var representation in graph.Where(r => session.NativeRepresentations.Contains(r.RepresentationId)))
        {
            var node = ledger.Artifacts.Single(a => a.ArtifactId.Value == Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]));
            var path = store.PayloadPath(node);
            if (entries.Any(e => e.RepresentationId == representation.RepresentationId && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, path))) continue;
            entries.RemoveAll(e => e.RepresentationId == representation.RepresentationId);
            entries.Add(new(representation.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow)); registered++;
        }
        if (registered == 0) return (context.Replicas, 0);
        var revision = catalog.Value?.CatalogRevision ?? -1;
        token.ThrowIfCancellationRequested();
        history.MigrationState.MarkImported(false);
        history.MigrationState.Prepare([HistoryLocalStateJournalRecovery.CreateCatalogIntent(new(history.ConfigId, revision + 1, entries), revision)]);
        await history.MigrationState.RecoverAsync(new(history.WorkspaceStore, history.LocalReplicaCatalogStore), CancellationToken.None).ConfigureAwait(false);
        await history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.LocalStateChanged);
        return (context.Replicas with { CatalogRevision = revision + 1 }, registered);
    }

    internal static async Task<NativeMigrationConsumerSession> OpenAsync(BackupConfig snapshot, HistoryRuntime history,
        IArchiveRepresentationBackend archive, CancellationToken token)
    {
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var native = graph.Where(NativePluginArtifactBackend.IsSupported).ToArray();
        if (native.Length == 0) return new() { Consumers = HistoryRepresentationRuntimeFactory.Create(snapshot, history, archive) };
        var incoming = await IncomingAsync(snapshot, token).ConfigureAwait(false);
        var payloads = incoming is null || incoming.NativePayloads.IsDefault ? [] : incoming.NativePayloads;
        var decoder = new ReadOnlyArtifactEnvelopeCodec(archive as IBoundedArtifactEnvelopeCodec
            ?? throw new InvalidOperationException("Native migration requires an explicit bounded decoder."));
        var localRoot = NativeArtifactStore.ForConfig(snapshot.DestinationPath, snapshot.Id);
        var available = new HashSet<RepresentationId>(); var protectedRoots = new HashSet<RepresentationId>();
        OwnedPluginTemporaryStorage? owner = null; string? workspace = null;
        try
        {
            NativeArtifactStore? store = null;
            if (payloads.Length > 0)
            {
                owner = await OwnedPluginTemporaryStorage.CreateAsync(Path.Combine(history.Repository.Paths.LocalStateRoot, "native-migration-verification"), token).ConfigureAwait(false);
                workspace = await owner.CreateDirectoryAsync(token).ConfigureAwait(false);
                store = new NativeArtifactStore(Path.Combine(workspace, "store"), decoder);
                await ImportIncomingAsync(snapshot, incoming!, store, token).ConfigureAwait(false);
                foreach (var payload in payloads) available.Add(payload.RepresentationId);
            }
            else if (Directory.Exists(localRoot))
            {
                store = new NativeArtifactStore(localRoot, decoder);
                var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value;
                foreach (var entry in catalog?.Entries ?? []) if (native.Any(r => r.RepresentationId == entry.RepresentationId)) available.Add(entry.RepresentationId);
            }
            if (store is not null)
            {
                var ledger = await store.Ledger.LoadAsync(token).ConfigureAwait(false);
                var rows = ledger.Artifacts.ToDictionary(a => a.ArtifactId);
                foreach (var representation in native.Where(r => available.Contains(r.RepresentationId)))
                {
                    var root = new ArtifactId(Guid.Parse(representation.RepresentationSpecificMetadata["artifactRootId"]));
                    if (NativeArtifactClosureLimits.Validate(ledger, root).Reachable.Any(id =>
                        ArtifactMetadataFile.ReadJson<NativeEnvelope>(Path.Combine(Path.GetDirectoryName(store.PayloadPath(rows[id]))!, "envelope.json"), 4096)?.Encrypted == true))
                        protectedRoots.Add(representation.RepresentationId);
                }
            }
            var selectedStore = store;
            return new()
            {
                _owner = owner, _workspace = workspace, Store = store, NativeRepresentations = available, CredentialProtected = protectedRoots,
                Consumers = HistoryRepresentationRuntimeFactory.Create(new(snapshot, history, archive,
                    () => selectedStore ?? throw new IOException("Native migration closure is unavailable."),
                    (representation, _) => System.Threading.Tasks.Task.FromResult(available.Contains(representation.RepresentationId))))
            };
        }
        catch
        {
            if (workspace is not null) HostArtifactWorkspace.DeleteOwnedDirectory(workspace);
            if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task ImportIncomingAsync(BackupConfig snapshot, HistoryTransferProjectMigration incoming, NativeArtifactStore store, CancellationToken token)
    {
        if (incoming.NativePayloads.IsDefaultOrEmpty) return;
        var objects = HistoryMigrationBundleService.InstalledNativeObjects(incoming, snapshot.DestinationPath);
        foreach (var payload in incoming.NativePayloads)
        {
            if (payload.PayloadType != HistoryMigrationBundleService.NativePayloadType || !new HistoryConfigId(snapshot.Id).Matches(payload.ArtifactConfigId))
                throw new InvalidDataException("Foreign Native migration payload.");
            var path = HistoryMigrationBundleService.InstalledNativePath(snapshot.DestinationPath, payload.EntryName);
            HistoryReplicaRecoveryService.RequireRegularPath(snapshot.DestinationPath, path);
            var facts = await HistoryReplicaRecoveryService.ReadFileFactsAsync(path, token).ConfigureAwait(false);
            if (facts.Size != payload.Size || facts.Hash != payload.StorageSha256) throw new InvalidDataException("Native migration descriptor changed after installation.");
            await store.ImportObjectsAsync(path, new(payload.ArtifactRootId), payload.RootStorageSha256, payload.ArtifactConfigId, payload.SourceId, objects, token).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> VerifyCredentialAsync(BackupConfig snapshot, HistoryRuntime history,
        IArchiveRepresentationBackend archive, IReadOnlyList<VersionRepresentation> graph, IRepresentationEnvironment environment, CancellationToken token)
    {
        try
        {
            await using var session = await OpenAsync(snapshot, history, archive, token).ConfigureAwait(false);
            if (session.CredentialProtected.Count > 0)
            {
                foreach (var representation in graph.Where(r => session.CredentialProtected.Contains(r.RepresentationId)))
                    if ((await session.Consumers.AssessVersionAsync(representation.VersionId, [representation], environment,
                        AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false)).Readiness == HistoryReadiness.Ready) return true;
                return false;
            }
            foreach (var version in await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false))
                if ((await session.Consumers.AssessVersionAsync(version.VersionId, graph, environment,
                    AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false)).Readiness == HistoryReadiness.Ready) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_workspace is not null) HostArtifactWorkspace.DeleteOwnedDirectory(_workspace); }
        finally { if (_owner is not null) await _owner.DisposeAsync().ConfigureAwait(false); }
        _workspace = null; _owner = null;
    }
}
