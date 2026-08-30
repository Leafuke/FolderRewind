using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed record HistoryArchiveRecoveryResult(
    VersionId VersionId,
    RepresentationId RepresentationId,
    LocalReplicaId LocalReplicaId,
    PackId? PackId,
    bool Created);

public sealed class HistoryArchiveRecoveryService
{
    private readonly HistoryRuntime _runtime;
    private readonly HistoryPackCodec _codec = new();

    public HistoryArchiveRecoveryService(HistoryRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<HistoryArchiveRecoveryResult> RecoverAsync(
        SourceId explicitlySelectedSourceId,
        SourceDescriptorSnapshot descriptor,
        string archivePath,
        bool overlay,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(archivePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Recovery archive does not exist.", path);
        await using var lease = await _runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var load = await _runtime.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Local Replica Catalog requires recovery.");
        var existingLocals = load.Value?.Entries.Where(item =>
            item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && string.Equals(item.Locator.AbsolutePath, path, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (existingLocals.Length > 0)
        {
            var representations = (await _runtime.Query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(item => item.RepresentationId);
            foreach (var existingLocal in existingLocals)
            {
                if (!representations.TryGetValue(existingLocal.RepresentationId, out var existingRepresentation))
                    continue;
                var existingVersion = await _runtime.Query.GetVersionAsync(
                    existingRepresentation.VersionId,
                    cancellationToken).ConfigureAwait(false);
                if (existingVersion?.SourceId == explicitlySelectedSourceId)
                {
                    return new(
                        existingVersion.VersionId,
                        existingRepresentation.RepresentationId,
                        existingLocal.LocalReplicaId,
                        PackId: null,
                        Created: false);
                }
            }
        }

        var version = new SourceVersion(
            VersionId.New(), _runtime.ConfigId, explicitlySelectedSourceId, [], DateTimeOffset.UtcNow, null,
            overlay ? CaptureScope.PartialSource : CaptureScope.FullSource,
            CaptureOutcome.Recovered, [], descriptor, null,
            new HistoryProvenance(HistoryOrigin.Recovery, string.Empty, "explicit archive recovery"));
        var representation = new VersionRepresentation(
            RepresentationId.New(), version.VersionId, RepresentationKind.LegacyArchive,
            Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is { Length: > 0 } format ? format : "7z",
            [], overlay ? MaterializationFidelity.Partial : MaterializationFidelity.Exact, null, null,
            new[] { new KeyValuePair<string, string>("recoveredFileName", Path.GetFileName(path)) });
        var localId = LocalReplicaId.New();
        var revision = load.Value?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision;
        var catalog = new LocalReplicaCatalog(
            _runtime.ConfigId,
            checked(revision + 1),
            (load.Value?.Entries ?? []).Append(new LocalReplicaCatalogEntry(
                representation.RepresentationId, localId,
                LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow)));
        var pack = new HistoryCommitPack(
            PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow,
            [_codec.CreateObject(version), _codec.CreateObject(representation)]);
        var journal = HistoryTransactionJournal.Prepared(
            pack.TransactionId,
            pack.PackId,
            [HistoryLocalStateJournalRecovery.CreateCatalogIntent(catalog, revision)]);
        await _runtime.Repository.CommitAsync(pack, journal, cancellationToken).ConfigureAwait(false);
        var recovery = new HistoryLocalStateJournalRecovery(_runtime.WorkspaceStore, _runtime.LocalReplicaCatalogStore);
        await recovery.ApplyCommittedStateAsync(journal, cancellationToken).ConfigureAwait(false);
        _runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.LocalStateApplied });
        _runtime.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
        await _runtime.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        _runtime.ChangeFeed.Publish(_runtime.ConfigId, HistoryChangeKind.TransactionCommitted,
            [version.VersionId.ToString(), representation.RepresentationId.ToString()]);
        _runtime.ChangeFeed.Publish(_runtime.ConfigId, HistoryChangeKind.LocalStateChanged);
        return new(version.VersionId, representation.RepresentationId, localId, pack.PackId, Created: true);
    }
}
