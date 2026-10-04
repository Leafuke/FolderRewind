using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

public sealed class PreparedHistoryChainRewrite : IAsyncDisposable
{
    internal PreparedHistoryChainRewrite(HistoryChainRewritePlan plan, HistoryChainRewriteJournalStore journals)
    { Plan = plan; Journals = journals; }
    public HistoryChainRewritePlan Plan { get; }
    public ImmutableArray<HistoryRewriteMapping> Mappings { get; internal set; } = [];
    public ImmutableArray<HistoryRewriteRetiredFile> RetiredFiles { get; internal set; } = [];
    internal ImmutableArray<HistoryRewriteFileWitness> VerifiedFiles { get; set; } = [];
    public LocalReplicaCatalog Catalog { get; internal set; } = null!;
    public long CreatedBytes { get; internal set; }
    public long ReclaimedBytes => RetiredFiles.Sum(f => f.Size);
    public long NetReleasedBytes => ReclaimedBytes - CreatedBytes;
    public bool CanCommit { get; internal set; } = true;
    public string Diagnostic { get; internal set; } = string.Empty;
    internal List<FileStream> Reads { get; } = [];
    internal HistoryChainRewriteJournalStore Journals { get; }
    internal bool CommitStarted { get; set; }
    internal bool Disposed { get; private set; }
    internal void ReleaseReads() { foreach (var read in Reads) read.Dispose(); Reads.Clear(); }
    public async ValueTask DisposeAsync()
    {
        if (Disposed) return;
        Disposed = true;
        ReleaseReads();
        if (!CommitStarted)
            await Journals.FinishAsync(new(Plan.OperationId, null, false, false, [], [], BackupRoot: Journals.BackupRoot), CancellationToken.None).ConfigureAwait(false);
    }
}

public sealed class HistoryChainRewriteExecutor(HistoryRuntime history, RepresentationRuntime engine,
    IHistoryChainRewriteArchiveBackend archives, Action<string>? checkpoint = null,
    Func<string, long>? availableSpace = null)
{
    public async Task<PreparedHistoryChainRewrite> PrepareAsync(HistoryChainRewritePlan plan,
        IProgress<HistoryChainRewriteProgress>? progress = null, CancellationToken token = default)
    {
        if (!plan.CanExecute) throw new InvalidOperationException(string.Join(" ", plan.Blockers));
        if (await HistoryChainRewritePlanner.FingerprintAsync(history, token).ConfigureAwait(false) != plan.StateFingerprint)
            throw new InvalidOperationException("History changed; prepare the deletion again.");
        var destination = HistoryRewriteStoragePaths.NormalizeBackupRoot(plan.Request.BackupRoot);
        HistoryRewriteStoragePaths.RequireUnlinkedAncestors(HistoryRewriteStoragePaths.OperationRoot(destination, history.ConfigId, plan.OperationId));
        var journals = new HistoryChainRewriteJournalStore(history, checkpoint, destination);
        var prepared = new PreparedHistoryChainRewrite(plan, journals);
        journals.Save(new(plan.OperationId, null, false, false, [], [], BackupRoot: destination));
        try
        {
            RequireSpace(journals.WorkRoot(plan.OperationId), 1024 * 1024);
            var inputIds = plan.Steps.SelectMany(s => HistoryChainRewritePlanner.Closure(s.Original.RepresentationId,
                plan.Representations.ToDictionary(r => r.RepresentationId))).ToHashSet();
            foreach (var alternate in plan.Steps.Where(s => s.ExistingAlternativeId is not null))
                inputIds.UnionWith(HistoryChainRewritePlanner.Closure(alternate.ExistingAlternativeId!.Value,
                    plan.Representations.ToDictionary(r => r.RepresentationId)));
            var targets = plan.Request.TargetReplicaIds.ToHashSet();
            foreach (var path in plan.Catalog.Entries.Where(e => inputIds.Contains(e.RepresentationId) || targets.Contains(e.LocalReplicaId)
                             || (plan.Request.SourceScope is null && IsLegacyPayload(e)))
                         .Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
                         .Select(e => e.Locator.AbsolutePath).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
                prepared.Reads.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true));
            await BuildAsync(prepared, false, progress, token).ConfigureAwait(false);
            if (plan.Request.Origin == HistoryChainRewriteOrigin.Retention && plan.Request.BenefitPolicy == HistoryRetentionBenefitPolicy.SpaceFirst && prepared.NetReleasedBytes <= 0 && plan.Steps.Length > 0)
            {
                journals.CleanupOperationTree(plan.OperationId, true);
                journals.CleanupOperationTree(plan.OperationId, false);
                await BuildAsync(prepared, true, progress, token).ConfigureAwait(false);
            }
            if (plan.Request.Origin == HistoryChainRewriteOrigin.Retention && plan.Request.BenefitPolicy == HistoryRetentionBenefitPolicy.SpaceFirst && prepared.NetReleasedBytes <= 0)
            { prepared.CanCommit = false; prepared.Diagnostic = "Safe chain rewriting would not release disk space."; }
            if (prepared.CanCommit)
                foreach (var witness in prepared.VerifiedFiles)
                {
                    if (!prepared.Reads.Any(read => StringComparer.OrdinalIgnoreCase.Equals(read.Name, witness.Path)))
                        prepared.Reads.Add(new FileStream(witness.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true));
                    if (await HistoryChainRewriteJournalStore.HashAsync(witness.Path, token).ConfigureAwait(false) != witness.StorageSha256)
                        throw new IOException("A verified archive changed during preparation.");
                }
            return prepared;
        }
        catch { await prepared.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task BuildAsync(PreparedHistoryChainRewrite prepared, bool preferDelta,
        IProgress<HistoryChainRewriteProgress>? progress, CancellationToken token)
    {
        var plan = prepared.Plan;
        var graph = plan.Representations.ToDictionary(r => r.RepresentationId);
        var originals = new RepresentationEnvironment(plan.Catalog.Entries, [], []);
        var entries = plan.Catalog.Entries.ToList();
        var mappings = new Dictionary<RepresentationId, HistoryRewriteMapping>();
        var versions = (await history.Query.GetAllVersionsAsync(token).ConfigureAwait(false)).ToDictionary(v => v.VersionId);
        var restored = new Dictionary<RepresentationId, (string Directory, HistoryRewriteTree Tree)>();
        var work = prepared.Journals.WorkRoot(plan.OperationId);
        var outputs = prepared.Journals.PayloadRoot(plan.OperationId);
        long created = 0;
        async Task<(string Directory, HistoryRewriteTree Tree)> OriginalAsync(RepresentationId id)
        {
            if (restored.TryGetValue(id, out var known)) return known;
            var directory = Path.Combine(work, "original", id.ToString());
            await engine.MaterializeAsync(id, plan.Representations, originals, MaterializationFidelity.Exact, directory, token).ConfigureAwait(false);
            var tree = await HistoryRewriteTree.ReadAsync(directory, token).ConfigureAwait(false);
            restored[id] = (directory, tree);
            return (directory, tree);
        }
        foreach (var step in plan.Steps)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("rebuild", mappings.Count, plan.Steps.Length));
            var original = await OriginalAsync(step.Original.RepresentationId).ConfigureAwait(false);
            RequireSpace(outputs, checked(original.Tree.TotalBytes * 3 + 1024 * 1024));
            var parent = step.BaseRepresentationId is { } oldParent
                ? mappings.GetValueOrDefault(oldParent)?.Replacement.RepresentationId ?? oldParent : (RepresentationId?)null;
            var depth = parent is { } p ? DeltaDepth(p, graph) + 1 : 0;
            var reuse = step.ReusePayload && (plan.Request.MaximumDeltaDepth <= 0 || depth <= plan.Request.MaximumDeltaDepth);
            string path;
            VersionRepresentation replacement;
            if (step.ExistingAlternativeId is { } alternateId)
            {
                replacement = graph[alternateId];
                var assessment = await engine.AssessVersionAsync(replacement.VersionId, plan.Representations,
                    new RepresentationEnvironment(plan.Catalog.Entries.Where(e => !plan.Request.TargetReplicaIds.Contains(e.LocalReplicaId)), [], []),
                    AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
                path = assessment.Candidates.Single(a => a.RepresentationId == alternateId).SelectedLocalPath
                    ?? throw new InvalidDataException("Alternative archive is unavailable.");
            }
            else if (reuse)
            {
                var assessment = await engine.AssessVersionAsync(step.Original.VersionId, plan.Representations, originals,
                    AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
                path = assessment.Candidates.Single(a => a.RepresentationId == step.Original.RepresentationId).SelectedLocalPath
                    ?? throw new InvalidDataException("Reusable archive is unavailable.");
                replacement = new(step.ReplacementId, step.Original.VersionId, step.Original.Kind, step.Original.Format,
                    parent is { } rp ? [rp] : [], MaterializationFidelity.Exact, step.Original.LogicalSha256,
                    step.Original.StateFingerprint, step.Original.RepresentationSpecificMetadata);
            }
            else
            {
                var baseline = step.BaseRepresentationId is { } b ? await OriginalAsync(b).ConfigureAwait(false) : default;
                var changed = baseline.Tree is null ? original.Tree.Files.Keys.ToImmutableArray() : original.Tree.ChangedFrom(baseline.Tree);
                var full = HistoryChainRewriteStrategy.PreferFull(parent is not null,
                    changed.Sum(f => original.Tree.Files[f].Length), original.Tree.TotalBytes, depth,
                    plan.Request.MaximumDeltaDepth, baseline.Tree is not null && original.Tree.RequiresFullAgainst(baseline.Tree), preferDelta);
                var output = Path.Combine(outputs, step.ReplacementId.ToString());
                var payload = full
                    ? await archives.CreateFullAsync(versions[step.Original.VersionId], original.Directory, step.ReplacementId, output, token).ConfigureAwait(false)
                    : await archives.CreateDeltaAsync(versions[step.Original.VersionId], original.Directory, changed, step.ReplacementId, output, token).ConfigureAwait(false);
                path = Path.GetFullPath(payload.PayloadPath);
                checkpoint?.Invoke("payload-created");
                var relative = Path.GetRelativePath(output, path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                    || !File.Exists(path) || new FileInfo(path).Length != payload.Size)
                    throw new InvalidDataException("Rewrite backend produced an invalid durable payload.");
                var metadata = payload.Metadata.SetItem("fileName", Path.GetFileName(path))
                    .SetItem("storageSha256", await HistoryChainRewriteJournalStore.HashAsync(path, token).ConfigureAwait(false));
                if (!full)
                {
                    var deleted = baseline.Tree!.Files.Keys.Except(original.Tree.Files.Keys, StringComparer.Ordinal);
                    metadata = metadata.SetItem("deletedFiles", string.Join('\n', deleted));
                }
                replacement = new(step.ReplacementId, step.Original.VersionId,
                    full ? RepresentationKind.CoreFull : RepresentationKind.CoreSmartDelta, payload.Format,
                    !full && parent is { } bp ? [bp] : [], MaterializationFidelity.Exact,
                    step.Original.LogicalSha256, step.Original.StateFingerprint, metadata);
                var verified = await archives.DeepVerifyAsync(replacement, path, token).ConfigureAwait(false);
                if (!verified.Success) throw new InvalidDataException(verified.Diagnostic);
                created += payload.Size;
            }
            graph.TryAdd(replacement.RepresentationId, replacement);
            if (!entries.Any(e => e.RepresentationId == replacement.RepresentationId
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                    && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, path)))
                entries.Add(new(replacement.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(path), DateTimeOffset.UtcNow));
            var verifyDirectory = Path.Combine(work, "verify", replacement.RepresentationId.ToString());
            progress?.Report(new("verify", mappings.Count, plan.Steps.Length));
            await engine.MaterializeAsync(replacement.RepresentationId, graph.Values.ToArray(),
                new RepresentationEnvironment(entries, [], []), MaterializationFidelity.Exact, verifyDirectory, token).ConfigureAwait(false);
            if (!original.Tree.EquivalentTo(await HistoryRewriteTree.ReadAsync(verifyDirectory, token).ConfigureAwait(false)))
                throw new InvalidDataException("Rewritten chain does not reproduce the original file state.");
            checkpoint?.Invoke("replacement-verified");
            mappings.Add(step.Original.RepresentationId, new(step.Original.RepresentationId, replacement, path, DeltaDepth(replacement.RepresentationId, graph)));
        }
        var targetIds = plan.Request.TargetReplicaIds.ToHashSet();
        // Older builds put durable rewrite/compaction payloads under the application data
        // repository. Relocate still-registered bytes in this same verified transaction.
        // A pending earlier journal may still reference those paths, so defer that case.
        var relocatedIds = new HashSet<LocalReplicaId>();
        var relocatedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var protectedRepresentations = history.MergeSessions.ProtectedRepresentations(plan.Representations);
        async Task<string> RelocateAsync(RepresentationId id, string oldPath)
        {
            if (relocatedPaths.TryGetValue(oldPath, out var known)) return known;
            var hash = await HistoryChainRewriteJournalStore.HashAsync(oldPath, token).ConfigureAwait(false);
            var newPath = Path.Combine(outputs, "relocated", id.ToString(), Path.GetFileName(oldPath));
            if (!File.Exists(newPath))
            {
                RequireSpace(outputs, new FileInfo(oldPath).Length);
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                await using (var input = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var output = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                created += new FileInfo(newPath).Length;
            }
            if (await HistoryChainRewriteJournalStore.HashAsync(newPath, token).ConfigureAwait(false) != hash)
                throw new InvalidDataException("Relocated archive does not match its original bytes.");
            relocatedPaths.Add(oldPath, newPath);
            checkpoint?.Invoke("payload-relocated");
            return newPath;
        }
        if (!HistoryChainRewriteJournalStore.HasPending(history.Repository.Paths.LocalStateRoot, plan.OperationId))
        {
            foreach (var group in plan.Catalog.Entries.Where(e => !targetIds.Contains(e.LocalReplicaId)
                         && !protectedRepresentations.Contains(e.RepresentationId)
                         && (plan.Request.SourceScope is null || plan.Request.SourceScope.Contains(versions[graph[e.RepresentationId].VersionId].SourceId))
                         && !mappings.ContainsKey(e.RepresentationId) && IsLegacyPayload(e) && File.Exists(e.Locator.AbsolutePath))
                         .GroupBy(e => e.RepresentationId))
            {
                var replacement = graph[group.Key];
                // These are existing verified archive bytes, not a change of logical state.
                var originalEntry = group.First();
                var oldPath = originalEntry.Locator.AbsolutePath;
                var newPath = await RelocateAsync(group.Key, oldPath).ConfigureAwait(false);
                var relocatedHash = await HistoryChainRewriteJournalStore.HashAsync(newPath, token).ConfigureAwait(false);
                foreach (var other in group.Skip(1))
                {
                    if (await HistoryChainRewriteJournalStore.HashAsync(other.Locator.AbsolutePath, token).ConfigureAwait(false) != relocatedHash)
                        throw new InvalidDataException("Local copies of an archive disagree; repair them before relocation.");
                    relocatedPaths[other.Locator.AbsolutePath] = newPath;
                }
                entries.Add(new(group.Key, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(newPath), DateTimeOffset.UtcNow));
                mappings.Add(group.Key, new(group.Key, replacement, newPath, DeltaDepth(group.Key, graph), StorageRelocationOnly: true));
                foreach (var old in group) relocatedIds.Add(old.LocalReplicaId);
            }
            // A descendant can reuse bytes from an old application-data payload. Move the
            // registration for its new representation as well, without recompressing it.
            foreach (var pair in mappings.Where(p => !p.Value.StorageRelocationOnly
                         && !protectedRepresentations.Contains(p.Value.Replacement.RepresentationId)
                         && IsLegacyPayloadPath(p.Value.Path)).ToArray())
            {
                var mapping = pair.Value;
                var newPath = await RelocateAsync(mapping.Replacement.RepresentationId, mapping.Path).ConfigureAwait(false);
                entries.RemoveAll(e => e.RepresentationId == mapping.Replacement.RepresentationId
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                    && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, mapping.Path));
                if (!entries.Any(e => e.RepresentationId == mapping.Replacement.RepresentationId
                    && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath
                    && StringComparer.OrdinalIgnoreCase.Equals(e.Locator.AbsolutePath, newPath)))
                    entries.Add(new(mapping.Replacement.RepresentationId, LocalReplicaId.New(), LocalReplicaLocator.ControlledAbsolute(newPath), DateTimeOffset.UtcNow));
                mappings[pair.Key] = mapping with { Path = newPath };
            }
        }
        var removedIds = plan.Catalog.Entries.Where(e => targetIds.Contains(e.LocalReplicaId)
            || relocatedIds.Contains(e.LocalReplicaId)
            || (mappings.TryGetValue(e.RepresentationId, out var mapping) && !mapping.StorageRelocationOnly
                && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath))
            .Select(e => e.LocalReplicaId).ToHashSet();
        var finalEntries = entries.Where(e => !removedIds.Contains(e.LocalReplicaId)).ToArray();
        var retainedPaths = finalEntries.Where(e => e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath)
            .Select(e => e.Locator.AbsolutePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Catalog.Entries.Any(e => targetIds.Contains(e.LocalReplicaId) && retainedPaths.Contains(e.Locator.AbsolutePath)))
            throw new InvalidOperationException("The selected archive path is shared by another retained local replica.");
        var deletions = new List<HistoryRewriteRetiredFile>();
        foreach (var group in plan.Catalog.Entries.Where(e => removedIds.Contains(e.LocalReplicaId)
                     && e.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && !retainedPaths.Contains(e.Locator.AbsolutePath))
                     .GroupBy(e => e.Locator.AbsolutePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key)) continue;
            deletions.Add(new(group.Key, new FileInfo(group.Key).Length,
                await HistoryChainRewriteJournalStore.HashAsync(group.Key, token).ConfigureAwait(false),
                [.. group.Select(e => e.RepresentationId).Distinct()]));
        }
        var finalEnvironment = new RepresentationEnvironment(finalEntries, [], []);
        var verifiedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        verifiedPaths.UnionWith(mappings.Values.Select(m => m.Path));
        foreach (var id in plan.ProtectedVersions)
        {
            var assessment = await engine.AssessVersionAsync(id, graph.Values.ToArray(), finalEnvironment,
                AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.Ready)
                throw new InvalidOperationException("A retained version would lose its Exact local restoration path.");
            foreach (var dependency in HistoryChainRewritePlanner.Closure(assessment.Selected!.RepresentationId, graph))
            {
                var dependencyAssessment = await engine.AssessVersionAsync(graph[dependency].VersionId, graph.Values.ToArray(),
                    finalEnvironment, AssessmentDepth.Deep, MaterializationFidelity.Exact, token).ConfigureAwait(false);
                verifiedPaths.Add(dependencyAssessment.Candidates.Single(a => a.RepresentationId == dependency).SelectedLocalPath
                    ?? throw new InvalidDataException("A verified dependency is no longer local."));
            }
        }
        var witnesses = new List<HistoryRewriteFileWitness>();
        foreach (var path in verifiedPaths)
            witnesses.Add(new(path, new FileInfo(path).Length, await HistoryChainRewriteJournalStore.HashAsync(path, token).ConfigureAwait(false)));
        prepared.VerifiedFiles = [.. witnesses];
        prepared.Mappings = [.. mappings.Values];
        prepared.RetiredFiles = [.. deletions];
        prepared.CreatedBytes = created;
        prepared.Catalog = new(history.ConfigId, checked(plan.Catalog.CatalogRevision + 1), finalEntries);
        progress?.Report(new("ready", plan.Steps.Length, plan.Steps.Length));
    }

    public async Task<HistoryChainRewriteResult> CommitAsync(PreparedHistoryChainRewrite prepared, CancellationToken token = default,
        IProgress<HistoryChainRewriteProgress>? progress = null)
    {
        if (prepared.Disposed || prepared.CommitStarted) throw new InvalidOperationException("Prepared deletion is no longer available.");
        if (!prepared.CanCommit) return new(false, false, 0, 0, 0, prepared.Diagnostic) { ReasonCode = "NoSpaceBenefit" };
        await using var gate = await history.MutationGate.EnterAsync(token).ConfigureAwait(false);
        var plan = prepared.Plan;
        if (await HistoryChainRewritePlanner.FingerprintAsync(history, token).ConfigureAwait(false) != plan.StateFingerprint)
            throw new InvalidOperationException("History changed after preparation; prepare the deletion again.");
        var facts = new List<object>(prepared.Mappings.Select(m => m.Replacement).DistinctBy(r => r.RepresentationId));
        foreach (var version in plan.Request.TargetVersionIds)
        {
            if (plan.Request.ReleaseTargets)
            {
                var tips = await history.Query.GetMaterializationPolicyTipsAsync(version, token).ConfigureAwait(false);
                facts.Add(new MaterializationPolicyUpdate(MaterializationPolicyUpdateId.New(), version,
                    tips.Select(t => t.UpdateId), MaterializationPolicyState.Released, DateTimeOffset.UtcNow, "Safe local archive deletion"));
            }
            if (plan.Request.HideTargets)
            {
                var target = new HistoryAnnotationTarget(HistoryAnnotationTargetKind.Version, version.Value);
                var updates = await history.Query.GetAnnotationUpdatesAsync(target, HistoryAnnotationKind.Suppression, token).ConfigureAwait(false);
                facts.Add(new HistoryAnnotationUpdate(AnnotationUpdateId.New(), target, HistoryAnnotationKind.Suppression,
                    HistoryAnnotationProjection.FindTips(updates).Select(t => t.UpdateId), "true", DateTimeOffset.UtcNow));
            }
        }
        // Repeating an identical immutable fact supplies a commit marker for Catalog-only deletions.
        if (facts.Count == 0)
            facts.Add(plan.Representations.First(r => plan.Request.TargetVersionIds.Contains(r.VersionId)));
        var codec = new HistoryPackCodec();
        var pack = new HistoryCommitPack(PackId.New(), HistoryTransactionId.New(), DateTimeOffset.UtcNow, facts.Select(f => codec.CreateObject(f)));
        var journal = HistoryTransactionJournal.Prepared(pack.TransactionId, pack.PackId,
            [HistoryLocalStateJournalRecovery.CreateCatalogIntent(prepared.Catalog, plan.Catalog.CatalogRevision)]);
        var rewrite = new HistoryChainRewriteJournal(plan.OperationId, pack.PackId, false, false,
            prepared.RetiredFiles, prepared.Mappings, prepared.VerifiedFiles, prepared.Journals.BackupRoot);
        token.ThrowIfCancellationRequested();
        prepared.Journals.Save(rewrite);
        prepared.CommitStarted = true;
        try
        {
            progress?.Report(new("committing", 0, 0));
            checkpoint?.Invoke("before-pack");
            await history.Repository.CommitAsync(pack, journal, CancellationToken.None).ConfigureAwait(false);
            checkpoint?.Invoke("pack-installed");
            await new HistoryLocalStateJournalRecovery(history.WorkspaceStore, history.LocalReplicaCatalogStore)
                .ApplyCommittedStateAsync(journal, CancellationToken.None).ConfigureAwait(false);
            checkpoint?.Invoke("catalog-applied");
            history.Repository.Journals.Save(journal with { Phase = HistoryTransactionPhase.Complete });
            await history.EnsureIndexCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            prepared.ReleaseReads();
            var clean = await prepared.Journals.FinishAsync(rewrite, CancellationToken.None).ConfigureAwait(false);
            await history.RefreshLocalStateHealthAsync(CancellationToken.None).ConfigureAwait(false);
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.TransactionCommitted);
            history.ChangeFeed.Publish(history.ConfigId, HistoryChangeKind.LocalStateChanged);
            return new(true, !clean, prepared.RetiredFiles.Where(f => !File.Exists(f.Path)).Sum(f => f.Size),
                prepared.CreatedBytes, prepared.Mappings.Length, clean ? string.Empty : "Backup chain rebuilt; some archive files await reclamation.")
            { DeletedArchives = prepared.RetiredFiles.Count(f => !File.Exists(f.Path)), ReasonCode = clean ? "Completed" : "CleanupPending" };
        }
        catch (Exception ex)
        {
            prepared.ReleaseReads();
            if (File.Exists(history.Repository.Paths.GetPackPath(pack.PackId)))
                return new(true, true, 0, prepared.CreatedBytes, prepared.Mappings.Length, "Backup chain commit is durable; recovery is required: " + ex.Message) { RecoveryRequired = true, ReasonCode = "RecoveryRequired" };
            // No commit exists. Repository recovery will discard the prepared Catalog intent.
            await history.Repository.Journals.RecoverAsync((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask).ConfigureAwait(false);
            await prepared.Journals.FinishAsync(rewrite, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static int DeltaDepth(RepresentationId id, IReadOnlyDictionary<RepresentationId, VersionRepresentation> graph)
    {
        var current = graph[id];
        return current.Kind != RepresentationKind.CoreSmartDelta ? 0
            : 1 + (current.DependencyRepresentationIds.IsEmpty ? 0 : current.DependencyRepresentationIds.Max(p => DeltaDepth(p, graph)));
    }

    private void RequireSpace(string path, long required)
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(path))!;
        // DriveInfo does not support UNC roots. Share I/O still fails closed on insufficient space.
        var available = availableSpace?.Invoke(path) ?? (volume.StartsWith(@"\\", StringComparison.Ordinal)
            ? long.MaxValue : new DriveInfo(volume).AvailableFreeSpace);
        if (available < required)
            throw new IOException("Insufficient temporary space to rebuild the backup chain. Original archives have been retained.");
    }

    private bool IsLegacyPayload(LocalReplicaCatalogEntry entry)
        => entry.Locator.Kind == LocalReplicaLocatorKind.ControlledAbsolutePath && IsLegacyPayloadPath(entry.Locator.AbsolutePath);

    private bool IsLegacyPayloadPath(string path)
        => HistoryRewriteStoragePaths.IsWithin(path, Path.Combine(history.Repository.Paths.RepositoryRoot, "payloads"));
}
