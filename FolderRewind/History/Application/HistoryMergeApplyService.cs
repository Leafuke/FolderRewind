using FolderRewind.History.Domain;
using FolderRewind.Services;
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
    Func<CancellationToken, Task<(string Revision, IReadOnlyList<HistoryRestoreSourceBinding> Bindings)>>? reload = null,
    MergeProviderDescriptor? provider = null)
{
    public sealed record CoordinationPlan(IReadOnlyDictionary<SourceId, string> Writes, bool NeedsProtection);

    public async Task<CoordinationPlan> PlanCoordinationAsync(PreparedMerge prepared, HistoryWorkspace workspace,
        IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token = default)
    {
        ValidateMapping(prepared, bindings);
        var writes = new Dictionary<SourceId, string>();
        var needsProtection = false;
        foreach (var source in prepared.Sources)
        {
            var binding = bindings.Single(b => b.SourceId == source.Version.SourceId);
            var exists = Directory.Exists(binding.TargetDirectory);
            var live = exists ? await MergeTreeManifest.ReadAsync(binding.TargetDirectory,
                FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), token).ConfigureAwait(false) : MergeTreeManifest.Empty;
            if (exists && live.Digest == source.TreeDigest) continue;
            writes.Add(binding.SourceId, live.Digest);
            if (live.Files.Count != 0 && !await IsCleanAsync(workspace, [binding], token).ConfigureAwait(false))
                needsProtection = true;
        }
        return new(writes, needsProtection);
    }

    public async Task<HistoryRestoreResult> ApplyAsync(MergeSession expected, CancellationToken token = default,
        PreparedMerge? ready = null, CoordinationPlan? coordinated = null)
    {
        PreparedMerge? prepared = null;
        HistoryRestoreResult? mutationResult = null;
        var session = expected;
        try
        {
            await restore.RecoverIncompleteAsync(token).ConfigureAwait(false);
            var stored = history.MergeSessions.Load(session.Id);
            var selectedProvider = provider ?? MergeProviderDescriptor.Generic;
            if (session.Plan.ProviderVersion != selectedProvider.Identity || session.Plan.PolicyVersion != selectedProvider.PolicyIdentity)
            {
                if (stored.State is MergeSessionState.Ready or MergeSessionState.Resolving or MergeSessionState.Preparing)
                    history.MergeSessions.Update(stored, MergeSessionState.Stale);
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
            }
            if (stored.Revision != session.Revision || stored.State != MergeSessionState.Ready)
                throw new InvalidOperationException("Merge Session is not ready at the expected revision.");
            var workspace = session.ProtectedWorkspace ?? session.Plan.ExpectedWorkspace;
            await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
            prepared = ready ?? await builder.BuildAsync(session, token).ConfigureAwait(false);
            var persisted = history.MergeSessions.LoadPrepared(session)?.Restore(session);
            if (persisted is null || persisted.PackId != prepared.PackId || persisted.TransactionId != prepared.TransactionId)
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
            coordinated ??= await PlanCoordinationAsync(prepared, workspace, session.Plan.Bindings, token).ConfigureAwait(false);
            await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(history.ConfigId, token).ConfigureAwait(false);
            await restore.RecoverInsideConfigurationAsync(operation, token).ConfigureAwait(false);
            await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
            var scope = await PlanCoordinationAsync(prepared, workspace, session.Plan.Bindings, token).ConfigureAwait(false);
            if (scope.Writes.Keys.Except(coordinated.Writes.Keys).Any() || scope.NeedsProtection && !coordinated.NeedsProtection)
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.CoordinationScopeChanged));
            if (scope.NeedsProtection)
            {
                if (protector is null) throw new InvalidOperationException("An Exact SafetySnapshot is required before Merge.");
                var protectedWorkspace = protector is IHistoryWorkingStateProtectorInsideOperation nested
                    ? await nested.ProtectInsideOperationAsync(workspace, operation, token).ConfigureAwait(false)
                    : await protector.ProtectAsync(workspace, token).ConfigureAwait(false);
                if (protectedWorkspace.ActiveBranchId != workspace.ActiveBranchId
                    || protectedWorkspace.ActiveBranchUpdateId != workspace.ActiveBranchUpdateId)
                    throw new InvalidOperationException("Protection must not advance a Branch.");
                session = history.MergeSessions.RecordProtection(session, protectedWorkspace);
                workspace = protectedWorkspace;
            }
            await using var guard = await restore.EnterFinalGuardAsync(token, operation).ConfigureAwait(false);
            await using var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
            await restore.RequireExpectedWorkspaceAsync(workspace, token).ConfigureAwait(false);
            var current = reload is null ? (session.Plan.ConfigRevision, (IReadOnlyList<HistoryRestoreSourceBinding>)session.Plan.Bindings)
                : await reload(token).ConfigureAwait(false);
            if (current.Item1 != session.Plan.ConfigRevision || !BindingsEqual(current.Item2, session.Plan.Bindings))
            {
                session = history.MergeSessions.Update(session, MergeSessionState.Stale);
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
            }
            var plan = await new HistoryMergePlanner(history).BuildAsync(session.Plan.Theirs.BranchId, workspace,
                current.Item1, current.Item2, token, selectedProvider).ConfigureAwait(false);
            if (plan.Ours.UpdateId != session.Plan.Ours.UpdateId || plan.Theirs.UpdateId != session.Plan.Theirs.UpdateId
                || plan.Mode != session.Plan.Mode || plan.BaseCheckpointId != session.Plan.BaseCheckpointId)
            {
                session = history.MergeSessions.Update(session, MergeSessionState.Stale);
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.Stale));
            }
            var finalScope = await PlanCoordinationAsync(prepared, workspace, current.Item2, token).ConfigureAwait(false);
            if (finalScope.NeedsProtection || finalScope.Writes.Count != scope.Writes.Count
                || finalScope.Writes.Any(pair => !scope.Writes.TryGetValue(pair.Key, out var digest) || digest != pair.Value))
                throw new HistoryMergeBlockedException(new(MergeDiagnosticCode.CoordinationScopeChanged));
            ValidateMapping(prepared, current.Item2);
            using var payloadLocks = await builder.ValidateAndLockAsync(prepared, token).ConfigureAwait(false);
            foreach (var source in prepared.Sources)
                if ((await MergeTreeManifest.ReadAsync(source.StagingDirectory, _ => true, token).ConfigureAwait(false)).Digest != source.TreeDigest)
                    throw new InvalidDataException("Staged Merge result changed before Apply.");
            var codec = new HistoryPackCodec();
            var pack = new HistoryCommitPack(prepared.PackId, prepared.TransactionId, prepared.Update.CreatedAtUtc,
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
            session = history.MergeSessions.Update(session, MergeSessionState.Applying, pack.TransactionId, pack.PackId);
            var result = mutationResult = await restore.ExecuteMutationAsync(prepared.Sources.Where(s => scope.Writes.ContainsKey(s.Version.SourceId)).Select(s =>
                new HistoryRestoreService.PreparedRestoreSource(current.Item2.Single(b => b.SourceId == s.Version.SourceId),
                    s.Version, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, s.StagingDirectory, scope.Writes[s.Version.SourceId], s.TreeDigest)).ToArray(),
                workspace, desired, token, pack, desiredCatalog, catalog.CatalogRevision).ConfigureAwait(false);
            if (result.TargetCommitted && result.Status != HistoryRestoreStatus.CommittedRecoveryRequired)
            {
                history.MergeSessions.Update(session, MergeSessionState.Committed);
                if (!await history.CleanupMergeArtifactsAsync().ConfigureAwait(false))
                    return result with { Status = HistoryRestoreStatus.CommittedWithPostActionWarning,
                        Diagnostic = history.MaintenanceDiagnostic ?? "Merge cleanup is deferred.", MergeDiagnostic = new(MergeDiagnosticCode.PostActionWarning) };
            }
            else if (result.Status == HistoryRestoreStatus.MutationFailedRolledBack)
                history.MergeSessions.Update(session, MergeSessionState.Ready);
            return result;
        }
        catch (Exception ex)
        {
            if (mutationResult?.TargetCommitted == true)
                return mutationResult with { Status = HistoryRestoreStatus.CommittedRecoveryRequired,
                    Diagnostic = $"Merge committed; Session completion requires recovery: {ex.Message}", MergeDiagnostic = new(MergeDiagnosticCode.RecoveryRequired) };
            if (mutationResult is not null) return mutationResult with { Diagnostic = mutationResult.Diagnostic + " " + ex.Message };
            // 不依赖可能已经不可读的 Session DB 判断 durable 事实。
            if (session.State == MergeSessionState.Applying)
            {
                if (prepared is not null)
                {
                    try
                    {
                        var path = history.Repository.Paths.GetPackPath(prepared.PackId);
                        if (File.Exists(path))
                        {
                            var pack = new HistoryPackCodec().Decode(File.ReadAllBytes(path)).Pack;
                            if (pack.TransactionId == prepared.TransactionId)
                                return new(HistoryRestoreStatus.CommittedRecoveryRequired, ex.Message, false, []);
                        }
                    }
                    catch { /* 无法证明提交状态，保留 intent 并阻断后续 mutation。 */ }
                }
                return new(HistoryRestoreStatus.MutationFailedRecoveryRequired, ex.Message, false, []);
            }
            // 已发布准备结果由 Session 拥有；阻断/取消后仍可显式重试。
            var diagnostic = (ex as HistoryMergeBlockedException)?.Diagnostic
                ?? new HistoryMergeDiagnostic(MergeDiagnosticCode.PreparationFailed, Detail: ex.Message);
            return new(diagnostic.Code == MergeDiagnosticCode.PreparationRequired ? HistoryRestoreStatus.PreparationRequired
                : HistoryRestoreStatus.BlockedBeforeMutation, ex.Message, false, [], MergeDiagnostic: diagnostic);
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
