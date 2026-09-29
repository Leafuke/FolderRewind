using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryMergeApplyService(HistoryRuntime history, HistoryRestoreService restore,
    HistoryMergeCommitBuilder builder, IHistoryWorkingStateProtector? protector = null,
    Func<CancellationToken, Task<(string Revision, IReadOnlyList<HistoryRestoreSourceBinding> Bindings)>>? reload = null)
{
    public async Task<HistoryRestoreResult> ApplyAsync(MergeSession expected, CancellationToken token = default)
    {
        PreparedMerge? prepared = null;
        var session = expected;
        try
        {
            await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
            var stored = history.MergeSessions.Load(session.Id);
            if (stored.Revision != session.Revision || stored.State != MergeSessionState.Ready)
                throw new InvalidOperationException("Merge Session is not ready at the expected revision.");
            var workspace = session.ProtectedWorkspace ?? session.Plan.ExpectedWorkspace;
            await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
            prepared = await builder.BuildAsync(session, token).ConfigureAwait(false);
            ValidateMapping(prepared, session.Plan.Bindings);
            if (!await IsCleanAsync(workspace, session.Plan.Bindings, token).ConfigureAwait(false))
            {
                if (protector is null) throw new InvalidOperationException("An Exact SafetySnapshot is required before Merge.");
                var protectedWorkspace = await protector.ProtectAsync(workspace, token).ConfigureAwait(false);
                if (protectedWorkspace.ActiveBranchId != workspace.ActiveBranchId
                    || protectedWorkspace.ActiveBranchUpdateId != workspace.ActiveBranchUpdateId)
                    throw new InvalidOperationException("Protection must not advance a Branch.");
                session = history.MergeSessions.RecordProtection(session, protectedWorkspace);
                workspace = protectedWorkspace;
            }
            await using var guard = await restore.EnterFinalGuardAsync(token).ConfigureAwait(false);
            await using var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
            await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
            var current = reload is null ? (session.Plan.ConfigRevision, (IReadOnlyList<HistoryRestoreSourceBinding>)session.Plan.Bindings)
                : await reload(token).ConfigureAwait(false);
            if (current.Item1 != session.Plan.ConfigRevision || !BindingsEqual(current.Item2, session.Plan.Bindings))
            {
                session = history.MergeSessions.Update(session, MergeSessionState.Stale);
                throw new InvalidOperationException("Configuration changed; recompute the Merge plan.");
            }
            var plan = await new HistoryMergePlanner(history).BuildAsync(session.Plan.Theirs.BranchId, workspace,
                current.Item1, current.Item2, token).ConfigureAwait(false);
            if (plan.Ours.UpdateId != session.Plan.Ours.UpdateId || plan.Theirs.UpdateId != session.Plan.Theirs.UpdateId
                || plan.Mode != session.Plan.Mode || plan.BaseCheckpointId != session.Plan.BaseCheckpointId)
            {
                session = history.MergeSessions.Update(session, MergeSessionState.Stale);
                throw new InvalidOperationException("Merge tips changed; recompute the plan.");
            }
            if (!await IsCleanAsync(workspace, current.Item2, token).ConfigureAwait(false))
                throw new InvalidOperationException("Working files changed after protection; Merge was blocked.");
            ValidateMapping(prepared, current.Item2);
            foreach (var source in prepared.Sources)
                if ((await MergeTreeManifest.ReadAsync(source.StagingDirectory, _ => true, token).ConfigureAwait(false)).Digest != source.TreeDigest)
                    throw new InvalidDataException("Staged Merge result changed before Apply.");
            var codec = new HistoryPackCodec();
            var pack = new HistoryCommitPack(prepared.PackId, prepared.TransactionId, DateTimeOffset.UtcNow,
                prepared.Facts.Select(f => codec.CreateObject(f)));
            var packs = await history.Repository.ReadAllPacksAsync(token).ConfigureAwait(false);
            new HistoryRepositoryValidator(codec).Validate(history.ConfigId,
                packs.Append(codec.Decode(codec.Encode(pack))).ToArray());
            var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value
                ?? throw new InvalidOperationException("Local replica catalog is unavailable.");
            var desiredCatalog = new LocalReplicaCatalog(history.ConfigId, checked(catalog.CatalogRevision + 1),
                catalog.Entries.Concat(prepared.NewReplicas));
            var restored = prepared.Sources.Select(s => s.Version.SourceId).ToHashSet();
            var desired = new HistoryWorkspace(history.ConfigId, checked(workspace.StateRevision + 1),
                prepared.Update.BranchId, prepared.Update.UpdateId,
                workspace.SourceBaselines.Where(b => !restored.Contains(b.SourceId)).Concat(prepared.Sources.Select(s =>
                    new WorkspaceSourceBaseline(s.Version.SourceId, s.Version.VersionId, WorkspaceBaselineRelation.Exact))),
                prepared.Checkpoint.CheckpointId);
            var originalDigests = new Dictionary<SourceId, string>();
            foreach (var source in prepared.Sources)
            {
                var binding = current.Item2.Single(b => b.SourceId == source.Version.SourceId);
                var baseline = workspace.SourceBaselines.Single(b => b.SourceId == source.Version.SourceId);
                var version = await history.Query.GetVersionAsync(baseline.BaseVersionId!.Value, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Protected baseline Version is missing.");
                var original = await restore.PrepareSourceAsync(version, binding, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, token).ConfigureAwait(false);
                try { originalDigests.Add(binding.SourceId, (await MergeTreeManifest.ReadAsync(original.StagingDirectory,
                    FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), token).ConfigureAwait(false)).Digest); }
                finally { HistoryRestoreTransactionJournalStore.CleanupStaging([original.StagingDirectory]); }
            }
            session = history.MergeSessions.Update(session, MergeSessionState.Applying, pack.TransactionId, pack.PackId);
            var result = await restore.ExecuteMutationAsync(prepared.Sources.Select(s =>
                new HistoryRestoreService.PreparedRestoreSource(current.Item2.Single(b => b.SourceId == s.Version.SourceId),
                    s.Version, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, s.StagingDirectory, originalDigests[s.Version.SourceId], s.TreeDigest)).ToArray(),
                workspace, desired, token, pack, desiredCatalog, catalog.CatalogRevision).ConfigureAwait(false);
            if (result.TargetCommitted && result.Status != HistoryRestoreStatus.CommittedRecoveryRequired)
                history.MergeSessions.Update(session, MergeSessionState.Committed);
            else if (result.Status == HistoryRestoreStatus.MutationFailedRolledBack)
                history.MergeSessions.Update(session, MergeSessionState.Ready);
            return result;
        }
        catch (Exception ex)
        {
            // Applying must retain its identity and roots until journal recovery resolves durability.
            if (history.MergeSessions.Load(session.Id).State == MergeSessionState.Applying)
                return new(HistoryRestoreStatus.MutationFailedRecoveryRequired, ex.Message, false, []);
            if (prepared is not null) HistoryRestoreTransactionJournalStore.CleanupStaging(prepared.Sources.Select(s => s.StagingDirectory));
            return new(HistoryRestoreStatus.BlockedBeforeMutation, ex.Message, false, []);
        }
    }

    private async Task<bool> IsCleanAsync(HistoryWorkspace workspace, IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token)
    {
        var probe = new HistoryExactWorkingStateProbe(history, restore);
        foreach (var binding in bindings)
        {
            var baseline = workspace.SourceBaselines.SingleOrDefault(b => b.SourceId == binding.SourceId);
            if (baseline is null || !await probe.IsExactAsync(binding, baseline, token).ConfigureAwait(false)) return false;
        }
        return true;
    }
    internal static bool BindingsEqual(IReadOnlyList<HistoryRestoreSourceBinding> left, IReadOnlyList<HistoryRestoreSourceBinding> right)
        => left.Count == right.Count && left.All(a => right.Any(b => a.SourceId == b.SourceId
            && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(a.TargetDirectory), Path.GetFullPath(b.TargetDirectory))
            && a.Boundary.Fingerprint == b.Boundary.Fingerprint));

    private static void ValidateMapping(PreparedMerge prepared, IReadOnlyList<HistoryRestoreSourceBinding> bindings)
    {
        if (bindings.Select(b => b.SourceId).Distinct().Count() != bindings.Count)
            throw new InvalidOperationException("Source mappings are not unique.");
        var paths = bindings.Select(b => Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.TargetDirectory))).ToArray();
        for (int i = 0; i < paths.Length; i++)
            for (int j = i + 1; j < paths.Length; j++)
                if (paths[i].Equals(paths[j], StringComparison.OrdinalIgnoreCase)
                    || paths[i].StartsWith(paths[j] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || paths[j].StartsWith(paths[i] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Source target directories overlap.");
        foreach (var source in prepared.Sources)
        {
            var binding = bindings.SingleOrDefault(b => b.SourceId == source.Version.SourceId);
            if (binding is null || binding.Boundary.Fingerprint != source.Version.EffectiveSourceBoundaryFingerprint)
                throw new InvalidOperationException("Every result Source requires a current mapping with the same boundary.");
        }
    }
}
