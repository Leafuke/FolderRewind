using FolderRewind.History.Domain;
using FolderRewind.History.Index;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Representation;

public sealed record LocalRepresentationCandidate(
    LocalReplicaCatalogEntry Entry,
    string? ResolvedPath,
    ReplicaAvailabilityObservation Availability,
    string Diagnostic);

public interface IRepresentationEnvironment
{
    IReadOnlyList<LocalRepresentationCandidate> GetLocalCandidates(RepresentationId representationId);
    IReadOnlyList<StorageReplica> GetActiveSharedReplicas(RepresentationId representationId);
    HistoryReplicaObservation? GetSharedReplicaObservation(ReplicaId replicaId);
}

public sealed class RepresentationEnvironment : IRepresentationEnvironment
{
    private readonly ImmutableArray<LocalReplicaCatalogEntry> _localEntries;
    private readonly ImmutableArray<StorageReplica> _sharedReplicas;
    private readonly ImmutableHashSet<ReplicaId> _activeReplicaIds;
    private readonly ImmutableDictionary<ReplicaId, HistoryReplicaObservation> _observations;
    private readonly ImmutableDictionary<string, string> _stableRoots;

    public RepresentationEnvironment(
        IEnumerable<LocalReplicaCatalogEntry>? localEntries,
        IEnumerable<StorageReplica>? sharedReplicas,
        IEnumerable<ReplicaId>? activeReplicaIds,
        IEnumerable<KeyValuePair<ReplicaId, HistoryReplicaObservation>>? observations = null,
        IEnumerable<KeyValuePair<string, string>>? stableRoots = null)
    {
        _localEntries = localEntries is null ? [] : [.. localEntries];
        _sharedReplicas = sharedReplicas is null ? [] : [.. sharedReplicas];
        _activeReplicaIds = activeReplicaIds?.ToImmutableHashSet() ?? [];
        _observations = observations?.ToImmutableDictionary() ?? ImmutableDictionary<ReplicaId, HistoryReplicaObservation>.Empty;
        _stableRoots = stableRoots?.ToImmutableDictionary(StringComparer.Ordinal)
            ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    }

    public IReadOnlyList<LocalRepresentationCandidate> GetLocalCandidates(RepresentationId representationId)
        => _localEntries
            .Where(entry => entry.RepresentationId == representationId)
            .Select(entry => Resolve(entry))
            .ToImmutableArray();

    public IReadOnlyList<StorageReplica> GetActiveSharedReplicas(RepresentationId representationId)
        => _sharedReplicas
            .Where(replica => replica.RepresentationId == representationId
                              && _activeReplicaIds.Contains(replica.ReplicaId))
            .ToImmutableArray();

    public HistoryReplicaObservation? GetSharedReplicaObservation(ReplicaId replicaId)
        => _observations.GetValueOrDefault(replicaId);

    private LocalRepresentationCandidate Resolve(LocalReplicaCatalogEntry entry)
    {
        try
        {
            var path = entry.Locator.Resolve(_stableRoots);
            var available = File.Exists(path) || Directory.Exists(path);
            return new(
                entry,
                path,
                available ? ReplicaAvailabilityObservation.Available : ReplicaAvailabilityObservation.Missing,
                available ? string.Empty : "Registered local payload is missing.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return new(entry, null, ReplicaAvailabilityObservation.Inaccessible, ex.Message);
        }
    }
}

