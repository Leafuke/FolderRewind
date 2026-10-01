using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Cloud;

public enum HistoryCloudItemState { Uploaded, Reused, Failed }
public sealed record HistoryCloudItemResult(RepresentationId RepresentationId, HistoryCloudItemState State, string Diagnostic);
public sealed record HistoryCloudBackupResult(IReadOnlyList<HistoryCloudItemResult> Items, IReadOnlyList<RepresentationId> Missing,
    HistoryMetadataSyncResult? Metadata, bool Canceled, DateTimeOffset ObservedAtUtc)
{
    public bool Complete => !Canceled && Items.Count != 0 && Missing.Count == 0 && Items.All(i => i.State != HistoryCloudItemState.Failed) && Metadata?.Succeeded == true;
}

public sealed class HistoryCloudBackupService(HistoryRuntime history, IHistoryReplicaTransport transport,
    Func<CancellationToken, Task<HistoryMetadataSyncResult>> syncMetadata)
{
    public async Task<HistoryCloudBackupResult> UploadClosureAsync(IReadOnlyList<RepresentationId> selected,
        Func<RepresentationId, CancellationToken, Task<string?>> localPath, CancellationToken token = default)
    {
        var graph = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
        var visited = new HashSet<RepresentationId>(); var visiting = new HashSet<RepresentationId>();
        var closure = new List<RepresentationId>(); var missing = new List<RepresentationId>();
        void Visit(RepresentationId id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id) || visiting.Count + visited.Count > 1000) throw new InvalidDataException("Representation closure is cyclic or exceeds its bound.");
            if (!graph.TryGetValue(id, out var representation)) missing.Add(id);
            else { foreach (var dependency in representation.DependencyRepresentationIds) Visit(dependency); closure.Add(id); }
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var id in selected) Visit(id);
        var results = new List<HistoryCloudItemResult>();
        if (missing.Count != 0) return new(results, missing, null, false, DateTimeOffset.UtcNow);
        var service = new HistoryReplicaSyncService(history, transport, syncMetadata, new NoRetirement());
        try
        {
            foreach (var id in closure)
            {
                token.ThrowIfCancellationRequested();
                bool reused = false;
                foreach (var replica in (await history.Query.GetStorageReplicasAsync(id, token).ConfigureAwait(false)).Where(r => r.ProviderKind == ReplicaProviderKind.Cloud))
                {
                    var updates = await history.Query.GetReplicaLifecycleUpdatesAsync(replica.ReplicaId, token).ConfigureAwait(false);
                    var parents = updates.SelectMany(u => u.ParentUpdateIds).ToHashSet();
                    if (!updates.Any(u => !parents.Contains(u.UpdateId) && u.State == ReplicaLifecycleState.Active)) continue;
                    var verified = await transport.VerifyRemoteAsync(replica.ReplicaId, token).ConfigureAwait(false);
                    if (verified.Success && verified.Size == replica.ExpectedSize && StringComparer.OrdinalIgnoreCase.Equals(verified.StorageSha256, replica.ExpectedStorageSha256))
                    { reused = true; break; }
                }
                if (reused) { results.Add(new(id, HistoryCloudItemState.Reused, "")); continue; }
                var path = await localPath(id, token).ConfigureAwait(false);
                if (path is null || !File.Exists(path)) { missing.Add(id); results.Add(new(id, HistoryCloudItemState.Failed, "Local payload is missing.")); break; }
                var uploaded = await service.UploadAsync(id, path, cancellationToken: token).ConfigureAwait(false);
                results.Add(new(id, uploaded.Status == HistoryReplicaOperationStatus.Succeeded ? HistoryCloudItemState.Uploaded : HistoryCloudItemState.Failed, uploaded.Diagnostic));
                if (uploaded.Status != HistoryReplicaOperationStatus.Succeeded) { missing.Add(id); break; }
            }
            missing.AddRange(closure.Where(id => !results.Any(r => r.RepresentationId == id)));
            var metadata = missing.Count == 0 ? await syncMetadata(token).ConfigureAwait(false) : null;
            return new(results, missing.Distinct().ToArray(), metadata, false, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(results, closure.Where(id => !results.Any(r => r.RepresentationId == id && r.State != HistoryCloudItemState.Failed)).ToArray(), null, true, DateTimeOffset.UtcNow); }
    }
    private sealed class NoRetirement : IHistoryReplicaRetirementGuard
    {
        public Task EnsureRetirementSafeAsync(StorageReplica replica, bool releaseVersion, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Upload cannot retire replicas.");
    }
}
