using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public enum MergeChangeKind { Added, Modified, Deleted, Structure }
public sealed record MergeReviewChange(string Path, MergeChangeKind Kind);
public sealed record MergeReviewTarget(SourceId SourceId, string Directory, bool Existed, string Digest);
public sealed record MergeReviewSnapshot(PreparedMerge Prepared, ImmutableArray<MergeReviewTarget> Targets,
    ImmutableArray<MergeReviewChange> BranchChanges, ImmutableArray<MergeReviewChange> WorkingChanges, bool NeedsProtection)
{
    public Guid SessionId => Prepared.Session.Id;
    public long ResolutionRevision => Prepared.Session.Revision;
    public Guid PlanRevision => Prepared.Session.Plan.Revision;

    public void RequireIdentity(MergeSession session, PreparedMerge candidate)
    {
        if (SessionId != session.Id || ResolutionRevision != session.Revision || PlanRevision != session.Plan.Revision
            || Prepared.PackId != candidate.PackId || Prepared.TransactionId != candidate.TransactionId
            || Prepared.Session.Plan.Ours.UpdateId != session.Plan.Ours.UpdateId || Prepared.Session.Plan.Theirs.UpdateId != session.Plan.Theirs.UpdateId
            || Prepared.Session.Plan.ConfigRevision != session.Plan.ConfigRevision)
            throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
    }
    public async Task RequireLiveAsync(IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token)
    {
        if (Targets.Length != bindings.Count) throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
        foreach (var target in Targets)
        {
            var binding = bindings.SingleOrDefault(b => b.SourceId == target.SourceId);
            if (binding is null || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(binding.TargetDirectory), Path.GetFullPath(target.Directory))
                || System.IO.Directory.Exists(binding.TargetDirectory) != target.Existed)
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.CoordinationScopeChanged));
            var tree = target.Existed ? await MergeTreeManifest.ReadAsync(binding.TargetDirectory,
                FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), token).ConfigureAwait(false) : MergeTreeManifest.Empty;
            if (tree.Digest != target.Digest) throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.CoordinationScopeChanged));
        }
    }
    public static ImmutableArray<MergeReviewChange> Compare(MergeTreeManifest before, MergeTreeManifest after)
    {
        var paths = before.Files.Keys.Concat(after.Files.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var structural = GenericFileMergeProvider.StructuralGroups(paths).SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        return paths.Order(StringComparer.Ordinal).Where(path =>
        {
            var b = before.Files.GetValueOrDefault(path); var a = after.Files.GetValueOrDefault(path);
            return b?.Digest != a?.Digest || b?.Length != a?.Length;
        }).Select(path => new MergeReviewChange(path, structural.Contains(path) ? MergeChangeKind.Structure
            : !before.Files.ContainsKey(path) ? MergeChangeKind.Added : !after.Files.ContainsKey(path) ? MergeChangeKind.Deleted : MergeChangeKind.Modified)).ToImmutableArray();
    }
}
