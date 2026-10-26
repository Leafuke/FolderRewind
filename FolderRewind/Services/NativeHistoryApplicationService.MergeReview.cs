using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static partial class NativeHistoryApplicationService
{
    internal static async Task<MergeReviewSnapshot> PrepareMergeReviewAsync(BackupConfig config, MergeSession session,
        Action<MergeOperationStage> progress, CancellationToken token)
    {
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(config, token).ConfigureAwait(false);
        var restore = CreateRestoreService(config, runtime);
        await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
        var semantic = MergeEngine(runtime, session.Plan.SemanticProvider);
        using var providerLease = semantic is null ? null : await new PluginV3SemanticMerge().AcquireAsync(semantic.Binding, token).ConfigureAwait(false);
        runtime.MergeSessions.RequireCurrentContract(session, semantic?.Descriptor);
        var workspace = session.ProtectedWorkspace ?? session.Plan.ExpectedWorkspace;
        var liveWorkspace = (await runtime.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value;
        if (liveWorkspace is null || !HistoryRestoreTransactionJournalStore.WorkspaceEquals(liveWorkspace, workspace))
            throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
        var current = await new HistoryMergePlanner(runtime).BuildAsync(session.Plan.Theirs.BranchId, workspace,
            NativeHistoryConfigLease.Signature(config), session.Plan.Bindings, token, semantic?.Descriptor).ConfigureAwait(false);
        if (current.Ours.UpdateId != session.Plan.Ours.UpdateId || current.Theirs.UpdateId != session.Plan.Theirs.UpdateId
            || current.ConfigRevision != session.Plan.ConfigRevision)
            throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
        var archives = new SevenZipHistoryArchiveBackend(config);
        var builder = new HistoryMergeCommitBuilder(runtime, restore, archives, archives,
            (version, staging, ct) => CaptureMergeMetadataAsync(config, version, staging, ct), semantic);
        progress(MergeOperationStage.Building);
        var prepared = await builder.BuildAsync(session, token).ConfigureAwait(false);
        progress(MergeOperationStage.Validating);
        using var validation = await builder.ValidateAndLockAsync(prepared, token).ConfigureAwait(false);
        var branchChanges = ImmutableArray.CreateBuilder<MergeReviewChange>();
        var workingChanges = ImmutableArray.CreateBuilder<MergeReviewChange>();
        var targets = ImmutableArray.CreateBuilder<MergeReviewTarget>();
        foreach (var source in prepared.Sources)
        {
            var binding = session.Plan.Bindings.Single(b => b.SourceId == source.Version.SourceId);
            var result = await MergeTreeManifest.ReadAsync(source.StagingDirectory, _ => true, token).ConfigureAwait(false);
            var ours = session.Plan.Sources.Single(s => s.SourceId == source.Version.SourceId).Ours;
            var oursTree = runtime.MergeSessions.ReviewOurs(session, ours);
            var exists = Directory.Exists(binding.TargetDirectory);
            var live = exists ? await MergeTreeManifest.ReadAsync(binding.TargetDirectory,
                FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), token,
                FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding, isDirectory: true)).ConfigureAwait(false) : MergeTreeManifest.Empty;
            branchChanges.AddRange(MergeReviewSnapshot.Compare(oursTree, result));
            workingChanges.AddRange(MergeReviewSnapshot.Compare(live, result));
            targets.Add(new(binding.SourceId, binding.TargetDirectory, exists, live.Digest));
        }
        var scope = await new HistoryMergeApplyService(runtime, restore, builder).PlanCoordinationAsync(prepared, workspace, session.Plan.Bindings, token).ConfigureAwait(false);
        var review = new MergeReviewSnapshot(prepared, targets.ToImmutable(), branchChanges.ToImmutable(), workingChanges.ToImmutable(), scope.NeedsProtection);
        review.RequireIdentity(runtime.MergeSessions.Load(session.Id), prepared);
        await review.RequireLiveAsync(session.Plan.Bindings, token).ConfigureAwait(false);
        return review;
    }

}
