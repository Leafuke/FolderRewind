using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryMergeService(HistoryRuntime history, HistoryRestoreService restore, IHistoryMergeProvider? provider = null)
{
    private readonly IHistoryMergeProvider _provider = provider ?? new GenericFileMergeProvider();
    public async Task<MergeSession?> StartAsync(BranchId source, string configRevision,
        IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token = default)
    {
        MergeSession session;
        await using (var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false))
        {
            var workspace = (await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
                ?? throw new InvalidOperationException("Workspace is unavailable.");
            var plan = await new HistoryMergePlanner(history).BuildAsync(source, workspace, configRevision, bindings, token).ConfigureAwait(false);
            if (plan.Mode == HistoryMergeMode.NoOp) return null;
            if (plan.Mode is HistoryMergeMode.NoCommonBase or HistoryMergeMode.MultipleMergeBases)
                throw new InvalidOperationException(plan.Mode.ToString());
            session = history.MergeSessions.Create(plan, Roots(plan));
        }
        return await PrepareAsync(session, token).ConfigureAwait(false);
    }
    private static IEnumerable<VersionId> Roots(HistoryMergePlan plan) => plan.Sources
        .SelectMany(s => new[] { s.Base?.VersionId, s.Ours?.VersionId, s.Theirs?.VersionId }).OfType<VersionId>();

    public async Task<MergeSession> RecomputeAsync(MergeSession session, string configRevision,
        IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token = default)
    {
        await using (var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false))
        {
            var workspace = (await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
                ?? throw new InvalidOperationException("Workspace is unavailable.");
            var plan = await new HistoryMergePlanner(history).BuildAsync(session.Plan.Theirs.BranchId, workspace, configRevision, bindings, token).ConfigureAwait(false);
            if (plan.Mode is not (HistoryMergeMode.ThreeWay or HistoryMergeMode.FastForwardLike))
                throw new InvalidOperationException(plan.Mode.ToString());
            session = history.MergeSessions.Replan(session, plan, Roots(plan));
        }
        return await PrepareAsync(session, token).ConfigureAwait(false);
    }

    public async Task<MergeSession> PrepareAsync(MergeSession session, CancellationToken token = default)
    {
        if (session.State != MergeSessionState.Preparing || session.Plan.ProviderVersion != _provider.Version)
            throw new InvalidOperationException("Merge preparation requires its fixed provider and Preparing state.");
        var root = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), session.Plan.Revision.ToString("N"));
        Directory.CreateDirectory(root);
        var cache = new Dictionary<VersionId, MergeTreeManifest>();
        async Task<MergeTreeManifest> Materialize(CheckpointSource? source)
        {
            if (source?.VersionId is not { } id) return MergeTreeManifest.Empty;
            if (cache.TryGetValue(id, out var cached)) return cached;
            var version = await history.Query.GetVersionAsync(id, token).ConfigureAwait(false) ?? throw new InvalidDataException("Merge Version is missing.");
            var destination = Path.Combine(root, "inputs", id.ToString());
            var binding = new HistoryRestoreSourceBinding(source.SourceId, destination, source.EffectiveSourceBoundary);
            var prepared = await restore.PrepareSourceAsync(version, binding, MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, token).ConfigureAwait(false);
            try
            {
                // 每次重试使用新目录；已发布引用保持不可变。
                destination += "-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(prepared.StagingDirectory, destination);
                var tree = await MergeTreeManifest.ReadAsync(destination, FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding), token).ConfigureAwait(false);
                cache.Add(id, tree); return tree;
            }
            finally { HistoryRestoreTransactionJournalStore.CleanupStaging([prepared.StagingDirectory]); }
        }
        bool any = false;
        foreach (var original in session.Plan.Sources)
        {
            token.ThrowIfCancellationRequested();
            var plan = original; var empty = MergeTreeManifest.Empty;
            MergeTreeManifest b = empty, o = empty, t = empty, automatic = empty;
            ImmutableArray<MergeConflict> conflicts = [];
            if (plan.Action == HistoryMergeSourceAction.Reuse)
                automatic = await Materialize(plan.Ours?.VersionId == plan.ReuseVersionId ? plan.Ours : plan.Theirs);
            else if (plan.Action != HistoryMergeSourceAction.Remove)
            {
                o = await Materialize(plan.Ours); t = await Materialize(plan.Theirs);
                if (plan.Action == HistoryMergeSourceAction.MergeFiles)
                {
                    b = await Materialize(plan.Base); var proposal = _provider.Analyze(plan.SourceId, b, o, t);
                    automatic = proposal.Automatic; conflicts = proposal.Conflicts;
                }
                else
                {
                    // Version identity 无法证明时，仍可用完整字节树证明 add/add 或 delete/unchanged。
                    if (plan.Action is HistoryMergeSourceAction.SourceDeleteModify or HistoryMergeSourceAction.SourceModifyDelete)
                        b = await Materialize(plan.Base);
                    CheckpointSource? reuse = null; bool resolved = false;
                    if (plan.Action == HistoryMergeSourceAction.SourceAddAdd && o.Digest == t.Digest
                        && plan.Ours!.EffectiveSourceBoundaryFingerprint == plan.Theirs!.EffectiveSourceBoundaryFingerprint)
                    { reuse = plan.Ours; resolved = true; automatic = o; }
                    if (plan.Action == HistoryMergeSourceAction.SourceDeleteModify && b.Digest == t.Digest
                        && plan.Base!.EffectiveSourceBoundaryFingerprint == plan.Theirs!.EffectiveSourceBoundaryFingerprint) resolved = true;
                    if (plan.Action == HistoryMergeSourceAction.SourceModifyDelete && b.Digest == o.Digest
                        && plan.Base!.EffectiveSourceBoundaryFingerprint == plan.Ours!.EffectiveSourceBoundaryFingerprint) resolved = true;
                    if (resolved) plan = plan with { Action = reuse is null ? HistoryMergeSourceAction.Remove : HistoryMergeSourceAction.Reuse, ReuseVersionId = reuse?.VersionId };
                    else
                    {
                        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("folderrewind/source-conflict/1\0"
                            + JsonSerializer.Serialize(plan) + b.Digest + o.Digest + t.Digest + _provider.Version + session.Plan.PolicyVersion)));
                        conflicts = [new(signature, new(plan.SourceId, [], "source"),
                            plan.Action == HistoryMergeSourceAction.SourceBoundaryConflict ? MergeConflictKind.SourceBoundary : MergeConflictKind.SourceRoster,
                            signature, b.Files, o.Files, t.Files)];
                    }
                }
            }
            history.MergeSessions.SaveSource(session, new(plan, automatic, b, o, t), conflicts); any |= conflicts.Length > 0;
        }
        return history.MergeSessions.Update(session, any ? MergeSessionState.Resolving : MergeSessionState.Ready);
    }

    public async Task<MergeSession> ImportManualAsync(MergeSession session, MergeConflict conflict, string externalFile, CancellationToken token = default)
    {
        if (conflict.Subject.Paths.Length != 1 || conflict.Kind == MergeConflictKind.PathStructure)
            throw new InvalidOperationException("Manual import requires a single-file content conflict.");
        var root = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), "manual", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "content");
        await using (var input = new FileStream(externalFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        { await input.CopyToAsync(output, token).ConfigureAwait(false); output.Flush(true); }
        var manifest = await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false);
        return history.MergeSessions.Resolve(session, new(session.Plan.Revision, conflict.Id, conflict.InputSignature,
            MergeResolutionChoice.Manual, manifest.Files["content"]));
    }

    public IEnumerable<(MergeConflict Conflict, MergeResolution? Resolution)> AllConflicts(MergeSession session)
    {
        for (int offset = 0; ; offset += 500)
        {
            var page = history.MergeSessions.Conflicts(session, offset, 500);
            foreach (var item in page) yield return item;
            if (page.Count < 500) yield break;
        }
    }
}
