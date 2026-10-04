using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

/// <summary>The caller owns the configuration operation lease for the complete run.</summary>
public sealed class HistoryCleanupCoordinator(HistoryRuntime history, RepresentationRuntime engine,
    IHistoryChainRewriteArchiveBackend archive, HistoryRetentionPlanner planner, string backupRoot)
{
    public async Task<HistoryCleanupReport?> ExecuteAsync(int keepCount, int maximumDepth,
        HistoryRetentionBenefitPolicy policy, bool automatic, string compressionSignature,
        IProgress<HistoryChainRewriteProgress>? progress = null, CancellationToken token = default)
    {
        if (keepCount <= 0) return null;
        var store = new HistoryCleanupReportStore(history.Repository.Paths.LocalStateRoot);
        using var activeRun = store.BeginRun();
        var cache = store.ReadCache();
        var report = new HistoryCleanupReport { KeepCount = keepCount, Policy = policy, Trigger = automatic ? "Automatic" : "Manual" };
        bool evaluated = false;
        void Persist()
        {
            try { store.Save(report); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!report.Issues.Any(i => i.Code == "ReportWriteFailed")) report.Issues.Add(new("ReportWriteFailed", ex.Message));
            }
        }
        try
        {
            await history.EnsureIndexCurrentAsync(token).ConfigureAwait(false);
            var versions = (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).ToDictionary(v => v.VersionId);
            var graph = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
            var catalog = await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false);
            var workspace = await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false);
            if (catalog.Status != DeviceLocalStateStatus.Valid || workspace.Status != DeviceLocalStateStatus.Valid)
                throw new InvalidDataException("Local catalog or workspace requires recovery.");
            var groups = GroupSources(versions, graph, catalog.Value!);
            foreach (var group in groups)
            {
                string groupId = string.Join(",", group.Select(s => s.ToString()).Order(StringComparer.Ordinal));
                var rows = group.OrderBy(s => s.ToString(), StringComparer.Ordinal).Select(source => new HistoryCleanupSourceReport
                {
                    SourceId = source.ToString(), GroupId = groupId,
                    Name = versions.Values.Where(v => v.SourceId == source).OrderByDescending(v => v.CreatedAtUtc).First().SourceDescriptorSnapshot.DisplayName
                }).ToArray();
                report.Sources.AddRange(rows);
            }
            if (!automatic) { evaluated = true; Persist(); }
            int completedSources = 0;
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                string groupId = string.Join(",", group.Select(s => s.ToString()).Order(StringComparer.Ordinal));
                var rows = report.Sources.Where(r => r.GroupId == groupId).ToArray();
                bool stopAfterGroup = false;
                string? fingerprint = null;
                try
                {
                    fingerprint = await FingerprintAsync(group, keepCount, maximumDepth, compressionSignature, token).ConfigureAwait(false);
                    if (automatic && fingerprint is not null && cache.GetValueOrDefault(groupId) == fingerprint)
                    {
                        foreach (var row in rows) { row.Status = "NoSpaceBenefit"; row.Issues.Add(new("NoSpaceBenefitCached")); }
                        continue;
                    }
                    evaluated = true;
                    Persist();
                    progress?.Report(new("planning", completedSources, report.Sources.Count));
                    HistoryRetentionPlan roots;
                    await using (var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false))
                        roots = await planner.PlanAsync(new HistoryRetentionRequest(keepCount, HistoryRetentionOperationRoots.Empty, true) { SourceScope = group }, token).ConfigureAwait(false);
                    foreach (var row in rows)
                    {
                        var source = group.Single(s => s.ToString() == row.SourceId);
                        row.Before = await CountReadyAsync(source, token).ConfigureAwait(false);
                        row.After = row.Before;
                        var protectedForSource = roots.ProtectedVersions.Where(v => versions.TryGetValue(v.VersionId, out var version) && version.SourceId == source).ToArray();
                        row.Recent = protectedForSource.Count(v => v.Reasons.HasFlag(HistoryProtectionReason.KeepCount));
                        row.ExtraProtected = protectedForSource.Count(v => !v.Reasons.HasFlag(HistoryProtectionReason.KeepCount));
                    }
                    cache.Remove(groupId);
                    if (!roots.CanExecute)
                    {
                        foreach (var row in rows)
                        {
                            row.Status = "Blocked";
                            row.Issues.AddRange(roots.Diagnostics.Select(d => new HistoryCleanupIssue(d.Code, d.Detail, d.VersionId?.ToString())));
                        }
                        continue;
                    }
                    var result = await new HistoryChainRewriteRetentionService(history, engine, archive, planner, backupRoot)
                        .ExecuteAsync(keepCount, maximumDepth, token, group, policy, progress).ConfigureAwait(false);
                    // Capture committed results before doing any further cancellable inspection.
                    rows[0].CreatedBytes = result.CreatedBytes;
                    rows[0].ReclaimedBytes = result.ReclaimedBytes;
                    rows[0].DeletedArchives = result.DeletedArchives;
                    foreach (var row in rows)
                    {
                        row.Status = result.Committed ? result.CleanupPending ? "CleanupPending" : "Completed"
                            : result.ReasonCode == "NoSpaceBenefit" ? "NoSpaceBenefit" : string.IsNullOrEmpty(result.Diagnostic) ? "NoWork" : "Blocked";
                        if (!string.IsNullOrEmpty(result.Diagnostic)) row.Issues.Add(new(
                            result.ReasonCode.Length > 0 ? result.ReasonCode : "Blocked", result.Diagnostic));
                    }
                    Persist();
                    if (result.RecoveryRequired)
                    {
                        stopAfterGroup = true;
                        foreach (var pending in report.Sources.Where(r => r.Status == "Pending"))
                        { pending.Status = "Blocked"; pending.Issues.Add(new("RecoveryRequired")); }
                    }
                    if (result.ReasonCode == "NoSpaceBenefit" && fingerprint is not null) cache[groupId] = fingerprint;
                    foreach (var row in rows)
                    {
                        if (stopAfterGroup) { row.After = null; continue; }
                        row.After = null;
                        row.After = await CountReadyAsync(group.Single(s => s.ToString() == row.SourceId), CancellationToken.None).ConfigureAwait(false);
                        var source = group.Single(s => s.ToString() == row.SourceId);
                        var local = (await history.LocalReplicaCatalogStore.LoadAsync().ConfigureAwait(false)).Value!;
                        var currentGraph = (await history.Query.GetAllRepresentationsAsync().ConfigureAwait(false)).ToDictionary(r => r.RepresentationId);
                        var protectedVersions = roots.ProtectedVersions.Select(v => v.VersionId).ToHashSet();
                        if (local.Entries.Any(e => currentGraph.TryGetValue(e.RepresentationId, out var r) && versions.TryGetValue(r.VersionId, out var v)
                            && v.SourceId == source && !protectedVersions.Contains(v.VersionId)
                            && (v.BoundaryConfidence != HistoricalBoundaryConfidence.Known || r.Fidelity != MaterializationFidelity.Exact
                                || r.Kind is not (RepresentationKind.CoreFull or RepresentationKind.CoreRolling or RepresentationKind.CoreSmartDelta)
                                || e.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath)))
                        {
                            row.Issues.Add(new("UnsupportedArchive"));
                            if (row.Status is "NoWork" or "Completed") row.Status = "Incomplete";
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    evaluated = true;
                    foreach (var row in rows)
                    {
                        if (row.Status is "Completed" or "CleanupPending") row.Issues.Add(new("AssessmentFailed", ex.Message));
                        else { row.Status = "Blocked"; row.Issues.Add(new(ex is IOException ? "StorageFailure" : "Blocked", ex.Message)); }
                    }
                }
                finally
                {
                    completedSources += rows.Length;
                    if (evaluated) Persist();
                    try { store.SaveCache(cache); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report.Issues.Add(new("CacheWriteFailed", ex.Message)); }
                }
                if (stopAfterGroup) break;
                if ((await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Status != DeviceLocalStateStatus.Valid)
                    throw new InvalidDataException("Local catalog requires recovery before another group can run.");
            }
            if (automatic && !evaluated && groups.Count > 0) return null;
            report.Status = report.Sources.Any(r => r.Status is "Blocked" or "Incomplete" or "NoSpaceBenefit" or "CleanupPending")
                ? report.Sources.Any(r => r.Status == "Completed") ? "Partial" : "Incomplete"
                : report.Sources.Any(r => r.Status == "Completed") ? "Completed" : "NoWork";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { report.Status = "Canceled"; }
        catch (Exception ex)
        { report.Status = "Incomplete"; report.Issues.Add(new("GlobalStateUnavailable", ex.Message)); }
        foreach (var row in report.Sources.Where(r => r.Status == "Pending")) row.Status = report.Status == "Canceled" ? "Canceled" : "Interrupted";
        report.FinishedAtUtc = DateTimeOffset.UtcNow;
        Persist();
        return report;
    }

    public static List<ImmutableHashSet<SourceId>> GroupSources(IReadOnlyDictionary<VersionId, SourceVersion> versions,
        IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph, LocalReplicaCatalog catalog)
    {
        var parents = versions.Values.Select(v => v.SourceId).Distinct().ToDictionary(s => s, s => s);
        SourceId Find(SourceId s) => parents[s] == s ? s : parents[s] = Find(parents[s]);
        SourceId Owner(RepresentationId id) => graph.TryGetValue(id, out var r) && versions.TryGetValue(r.VersionId, out var v)
            ? v.SourceId : throw new InvalidDataException("An archive dependency has no provable source owner.");
        void Union(SourceId a, SourceId b) => parents[Find(a)] = Find(b);
        foreach (var r in graph.Values)
        {
            var owner = Owner(r.RepresentationId);
            // Validates missing references and cycles before allowing any scoped deletion.
            foreach (var id in HistoryChainRewritePlanner.Closure(r.RepresentationId, graph)) Union(owner, Owner(id));
        }
        foreach (var entry in catalog.Entries) _ = Owner(entry.RepresentationId);
        foreach (var shared in catalog.Entries.GroupBy(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                     ? Path.GetFullPath(e.Locator.AbsolutePath) : $"{e.Locator.Kind}:{e.Locator.StableRootId}:{e.Locator.RelativePath}", StringComparer.OrdinalIgnoreCase))
            foreach (var entry in shared) Union(Owner(shared.First().RepresentationId), Owner(entry.RepresentationId));
        return parents.Keys.ToArray().GroupBy(Find).Select(g => g.ToImmutableHashSet()).OrderBy(g => g.Min(s => s.ToString()), StringComparer.Ordinal).ToList();
    }

    private async Task<int> CountReadyAsync(SourceId source, CancellationToken token)
    {
        var graph = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value!;
        var environment = new RepresentationEnvironment(catalog.Entries, [], []);
        int count = 0;
        foreach (var version in (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).Where(v => v.SourceId == source))
            if ((await engine.AssessVersionAsync(version.VersionId, graph, environment, AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false)).Readiness == HistoryReadiness.Ready) count++;
        return count;
    }

    private async Task<string?> FingerprintAsync(ImmutableHashSet<SourceId> sources, int keep, int depth, string compression, CancellationToken token)
    {
        try
        {
            // Immutable facts are identified by ID; BackupRun-only commits do not invalidate a no-benefit result.
            var text = new StringBuilder("cleanup-v1|").Append(keep).Append('|').Append(depth).Append('|').Append(compression);
            foreach (var run in (await history.Query.GetRunsAsync(token).ConfigureAwait(false)).Where(r => r.SourceResults.Any(s => s.Outcome == BackupRunSourceOutcome.Captured)).OrderBy(r => r.RunId.ToString(), StringComparer.Ordinal)) text.Append(run.RunId);
            foreach (var c in (await history.Query.GetAllCheckpointsAsync(token).ConfigureAwait(false)).OrderBy(c => c.CheckpointId.ToString(), StringComparer.Ordinal)) text.Append(c.CheckpointId);
            foreach (var a in (await history.Query.GetAllAnnotationUpdatesAsync(token).ConfigureAwait(false)).OrderBy(a => a.UpdateId.ToString(), StringComparer.Ordinal)) text.Append(a.UpdateId);
            foreach (var b in (await history.Query.GetAllBranchUpdatesAsync(token).ConfigureAwait(false)).OrderBy(b => b.UpdateId.ToString(), StringComparer.Ordinal)) text.Append(b.UpdateId);
            foreach (var snapshot in (await history.Query.GetSafetySnapshotsAsync(token).ConfigureAwait(false)).OrderBy(s => s.SnapshotId.ToString(), StringComparer.Ordinal)) text.Append(snapshot.SnapshotId);
            foreach (var release in (await history.Query.GetSafetySnapshotReleasesAsync(null, token).ConfigureAwait(false)).OrderBy(s => s.SnapshotId.ToString(), StringComparer.Ordinal)) text.Append("released:").Append(release.SnapshotId);
            var workspace = (await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value!;
            foreach (var baseline in workspace.SourceBaselines.OrderBy(b => b.SourceId.ToString(), StringComparer.Ordinal)) text.Append(baseline);
            var representations = await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
            foreach (var r in representations.OrderBy(r => r.RepresentationId.ToString(), StringComparer.Ordinal)) text.Append(r.RepresentationId);
            foreach (var r in history.MergeSessions.ProtectedRepresentations(representations).OrderBy(r => r.ToString(), StringComparer.Ordinal)) text.Append("active:").Append(r);
            var versions = (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).Where(v => sources.Contains(v.SourceId)).Select(v => v.VersionId).ToHashSet();
            var ids = (await history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false)).Where(r => versions.Contains(r.VersionId)).Select(r => r.RepresentationId).ToHashSet();
            foreach (var version in versions.OrderBy(v => v.ToString(), StringComparer.Ordinal))
                foreach (var policy in (await history.Query.GetMaterializationPolicyTipsAsync(version, token).ConfigureAwait(false)).OrderBy(p => p.UpdateId.ToString(), StringComparer.Ordinal)) text.Append(policy.UpdateId);
            var catalog = (await history.LocalReplicaCatalogStore.LoadAsync(token).ConfigureAwait(false)).Value!;
            foreach (var e in catalog.Entries.Where(e => ids.Contains(e.RepresentationId)).OrderBy(e => e.LocalReplicaId.ToString(), StringComparer.Ordinal))
            {
                if (e.Locator.Kind != LocalReplicaLocatorKind.ControlledAbsolutePath || !File.Exists(e.Locator.AbsolutePath)) return null;
                await using var stream = new FileStream(e.Locator.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                text.Append(e.LocalReplicaId).Append(e.RepresentationId).Append(e.Locator.AbsolutePath).Append(new FileInfo(e.Locator.AbsolutePath).LastWriteTimeUtc.Ticks).Append(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
