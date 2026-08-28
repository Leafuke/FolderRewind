using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public sealed class HistoryReplicaSyncService
{
    private readonly HistoryRuntime _history;
    private readonly IHistoryReplicaTransport _transport;
    private readonly Func<CancellationToken, Task<HistoryMetadataSyncResult>> _syncMetadata;
    private readonly IHistoryReplicaRetirementGuard _retirementGuard;
    private readonly HistoryPackCodec _codec = new();

    public HistoryReplicaSyncService(
        HistoryRuntime history,
        IHistoryReplicaTransport transport,
        Func<CancellationToken, Task<HistoryMetadataSyncResult>> syncMetadata,
        IHistoryReplicaRetirementGuard retirementGuard)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _syncMetadata = syncMetadata ?? throw new ArgumentNullException(nameof(syncMetadata));
        _retirementGuard = retirementGuard ?? throw new ArgumentNullException(nameof(retirementGuard));
    }

    public async Task<HistoryReplicaOperationResult> UploadAsync(
        RepresentationId representationId,
        string localPath,
        Guid? artifactRootId = null,
        CancellationToken cancellationToken = default)
    {
        var replicaId = ReplicaId.New();
        try
        {
            if (await _history.Query.GetRepresentationAsync(representationId, cancellationToken).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Representation does not exist.");
            await _transport.UploadAsync(replicaId, localPath, cancellationToken).ConfigureAwait(false);
            var verification = await _transport.VerifyRemoteAsync(replicaId, cancellationToken).ConfigureAwait(false);
            if (!verification.Success) throw new InvalidDataException(verification.Diagnostic);
            var objectKey = HistoryRepositoryPaths.CreateReplicaObjectKey(replicaId);
            var manifest = new HistoryReplicaManifest(
                representationId, replicaId, artifactRootId, objectKey,
                verification.Size, verification.StorageSha256);
            await _transport.CommitManifestOnceAsync(manifest, cancellationToken).ConfigureAwait(false);

            var replica = new StorageReplica(
                replicaId, representationId, ReplicaProviderKind.Cloud, objectKey,
                verification.Size, verification.StorageSha256, HistoryProvenance.Native("cloud-upload"));
            var active = new ReplicaLifecycleUpdate(
                ReplicaLifecycleUpdateId.New(), replicaId, [], ReplicaLifecycleState.Active,
                DateTimeOffset.UtcNow, "Physical payload and manifest verified.");
            await CommitFactsAsync([replica, active], cancellationToken).ConfigureAwait(false);
            var sync = await _syncMetadata(cancellationToken).ConfigureAwait(false);
            if (!sync.Succeeded) throw new InvalidOperationException("Replica facts are durable locally but metadata sync failed: " + sync.Diagnostic);
            return new(HistoryReplicaOperationStatus.Succeeded, replicaId, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new(HistoryReplicaOperationStatus.Failed, replicaId, ex.Message);
        }
    }

    public async Task<HistoryReplicaOperationResult> DownloadAsync(
        HistoryReplicaManifest manifest,
        string finalLocalPath,
        CancellationToken cancellationToken = default)
    {
        var staging = finalLocalPath + ".download-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await _transport.DownloadAsync(manifest, staging, cancellationToken).ConfigureAwait(false);
            var verification = VerifyLocal(staging, manifest);
            if (!verification.Success) throw new InvalidDataException(verification.Diagnostic);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalLocalPath))!);
            if (File.Exists(finalLocalPath))
            {
                if (!VerifyLocal(finalLocalPath, manifest).Success)
                    throw new IOException("Existing local realization differs; download will not overwrite it.");
                File.Delete(staging);
            }
            else
            {
                File.Move(staging, finalLocalPath, false);
            }
            await RegisterLocalAsync(manifest.RepresentationId, finalLocalPath, cancellationToken).ConfigureAwait(false);
            await _history.Index.UpsertObservationAsync(new HistoryReplicaObservation(
                manifest.ReplicaId.ToString(), ReplicaAvailabilityObservation.Available,
                ReplicaIntegrityObservation.Verified, DateTimeOffset.UtcNow, "Downloaded and verified."), cancellationToken)
                .ConfigureAwait(false);
            return new(HistoryReplicaOperationStatus.Succeeded, manifest.ReplicaId, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(HistoryReplicaOperationStatus.Failed, manifest.ReplicaId, ex.Message); }
        finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { } }
    }

    public async Task<HistoryReplicaOperationResult> RetireAsync(
        StorageReplica replica,
        HistoryReplicaManifest manifest,
        bool releaseVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _retirementGuard.EnsureRetirementSafeAsync(replica, releaseVersion, cancellationToken).ConfigureAwait(false);
            if (releaseVersion)
            {
                var representation = await _history.Query.GetRepresentationAsync(replica.RepresentationId, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidOperationException("Representation is missing.");
                await _history.MaterializationPolicies.SetAsync(
                    representation.VersionId, MaterializationPolicyState.Released,
                    "User requested shared materialization release.", cancellationToken).ConfigureAwait(false);
            }
            var updates = await _history.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, cancellationToken)
                .ConfigureAwait(false);
            var parentIds = Tips(updates).Select(item => item.UpdateId).ToArray();
            if (Tips(updates).Any(item => item.State == ReplicaLifecycleState.Retired))
                return new(HistoryReplicaOperationStatus.Succeeded, replica.ReplicaId, string.Empty);
            var retired = new ReplicaLifecycleUpdate(
                ReplicaLifecycleUpdateId.New(), replica.ReplicaId, parentIds,
                ReplicaLifecycleState.Retired, DateTimeOffset.UtcNow, "User requested Cloud replica retirement.");
            await CommitFactsAsync([retired], cancellationToken).ConfigureAwait(false);
            var sync = await _syncMetadata(cancellationToken).ConfigureAwait(false);
            if (!sync.Succeeded) throw new InvalidOperationException("Retirement metadata sync failed: " + sync.Diagnostic);
            try
            {
                await _transport.DeletePhysicalAsync(manifest, cancellationToken).ConfigureAwait(false);
                return new(HistoryReplicaOperationStatus.Succeeded, replica.ReplicaId, string.Empty);
            }
            catch (Exception ex)
            {
                return new(HistoryReplicaOperationStatus.MetadataCommittedPhysicalOrphan, replica.ReplicaId, ex.Message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(HistoryReplicaOperationStatus.Failed, replica.ReplicaId, ex.Message); }
    }

    private async Task CommitFactsAsync(IEnumerable<object> facts, CancellationToken cancellationToken)
    {
        await using var lease = await _history.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var objects = facts.Select(fact => _codec.CreateObject(fact)).ToArray();
        var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, objects);
        await _history.Repository.CommitAsync(pack, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _history.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        _history.ChangeFeed.Publish(_history.ConfigId, HistoryChangeKind.TransactionCommitted, objects.Select(item => item.Id));
    }

    private async Task RegisterLocalAsync(
        RepresentationId representationId,
        string path,
        CancellationToken cancellationToken)
    {
        var load = await _history.LocalReplicaCatalogStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (load.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            throw new DeviceLocalStateConflictException("Local Replica Catalog requires recovery.");
        var current = load.Value;
        if (current?.Entries.Any(item => item.RepresentationId == representationId
            && item.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
            && StringComparer.OrdinalIgnoreCase.Equals(item.Locator.AbsolutePath, Path.GetFullPath(path))) == true) return;
        var revision = current?.CatalogRevision ?? LocalReplicaCatalogStore.MissingRevision;
        var updated = new LocalReplicaCatalog(
            _history.ConfigId, checked(revision + 1),
            (current?.Entries ?? []).Append(new LocalReplicaCatalogEntry(
                representationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow)));
        await _history.LocalReplicaCatalogStore.SaveAsync(updated, revision, cancellationToken).ConfigureAwait(false);
    }

    private static HistoryReplicaVerification VerifyLocal(string path, HistoryReplicaManifest manifest)
    {
        if (!File.Exists(path)) return new(false, 0, string.Empty, "Downloaded payload is missing.");
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return info.Length == manifest.Size && StringComparer.OrdinalIgnoreCase.Equals(hash, manifest.StorageSha256)
            ? new(true, info.Length, hash, string.Empty)
            : new(false, info.Length, hash, "Downloaded payload size or hash differs from manifest.");
    }

    private static ImmutableArray<ReplicaLifecycleUpdate> Tips(IEnumerable<ReplicaLifecycleUpdate> updates)
    {
        var all = updates.ToImmutableArray();
        var parents = all.SelectMany(item => item.ParentUpdateIds).ToHashSet();
        return all.Where(item => !parents.Contains(item.UpdateId)).ToImmutableArray();
    }
}
