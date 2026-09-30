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
    // 默认为文本感知实现：通用实现只看整份文件的内容是否相同，
    // 两侧改了同一文件的不同段落也会判成冲突。要退回纯按内容比对，显式传 new GenericFileMergeProvider()。
    private readonly IHistoryMergeProvider _provider = provider ?? new TextAwareMergeProvider();
    public async Task<MergeSession?> StartAsync(BranchId source, string configRevision,
        IReadOnlyList<HistoryRestoreSourceBinding> bindings, CancellationToken token = default)
    {
        MergeSession session;
        await using (var lease = await history.MutationGate.EnterAsync(token).ConfigureAwait(false))
        {
            var workspace = (await history.WorkspaceStore.LoadAsync(token).ConfigureAwait(false)).Value
                ?? throw new InvalidOperationException("Workspace is unavailable.");
            var plan = await new HistoryMergePlanner(history, _provider.Version, _provider.PolicyVersion)
                .BuildAsync(source, workspace, configRevision, bindings, token).ConfigureAwait(false);
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
            // 重算沿用本会话计划里的版本串：provider 是构造时固定的，
            // 上面那处 Version 守卫已保证会话与 provider 同源，取哪个都一样，但用会话里的更直白。
            var plan = await new HistoryMergePlanner(history, session.Plan.ProviderVersion, session.Plan.PolicyVersion)
                .BuildAsync(session.Plan.Theirs.BranchId, workspace, configRevision, bindings, token).ConfigureAwait(false);
            if (plan.Mode is not (HistoryMergeMode.ThreeWay or HistoryMergeMode.FastForwardLike))
                throw new InvalidOperationException(plan.Mode.ToString());
            session = history.MergeSessions.Replan(session, plan, Roots(plan));
        }
        return await PrepareAsync(session, token).ConfigureAwait(false);
    }

    public async Task<MergeSession> PrepareAsync(MergeSession session, CancellationToken token = default)
    {
        // 两个版本串都盯住：只比 provider 的话，换了策略（白名单、大小上限）而 provider 没换时，
        // 存量会话会拿着旧策略算出来的计划继续准备，谁都不报错。
        if (session.State != MergeSessionState.Preparing
            || session.Plan.ProviderVersion != _provider.Version
            || session.Plan.PolicyVersion != _provider.PolicyVersion)
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
                    // root 是本次修订的会话可写目录，provider 把自动合并的结果落它下面（见 IHistoryMergeProvider.Analyze）。
                    b = await Materialize(plan.Base); var proposal = _provider.Analyze(plan.SourceId, b, o, t, root);
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

    /// <summary>
    /// 整份取一边：把一条冲突按 ours 或 theirs 解决掉。
    /// <para>
    /// <see cref="MergeResolutionChoice.Manual"/> 不能走这里 —— 手改要连内容一起交，用 <see cref="ImportManualAsync"/>。
    /// </para>
    /// <para>
    /// <paramref name="inputSignature"/> 是调用方<b>看到的那一份</b>冲突的签名；存储层会拿它与库里的比，
    /// 不一致即拒绝（输入变了，这个决定就作废了）。
    /// </para>
    /// </summary>
    public MergeSession ResolveAsSide(MergeSession session, string conflictId, string inputSignature, MergeResolutionChoice choice)
    {
        if (choice == MergeResolutionChoice.Manual)
            throw new InvalidOperationException("Manual resolution must carry its content.");
        return history.MergeSessions.Resolve(session, new(session.Plan.Revision, conflictId, inputSignature, choice));
    }

    /// <summary>
    /// 把界面自己合成的整份文件登记为一条冲突的解决结果（<see cref="MergeResolutionChoice.Manual"/>）。
    /// 单一文件、非结构冲突这些限制由存储层校验（它才看得到那条冲突的真身），这里不重复判一遍。
    /// </summary>
    /// <remarks>
    /// 只收内存里的字节：界面手里的结果本来就在内存里，先落一个临时文件再读回来是白绕一圈。
    /// </remarks>
    public Task<MergeSession> ImportManualAsync(MergeSession session, string conflictId, string inputSignature,
        ReadOnlyMemory<byte> content, CancellationToken token = default)
        => StageManualAsync(session, conflictId, inputSignature,
            async (output, cancel) => await output.WriteAsync(content, cancel).ConfigureAwait(false), token);

    private async Task<MergeSession> StageManualAsync(MergeSession session, string conflictId, string inputSignature,
        Func<FileStream, CancellationToken, Task> write, CancellationToken token)
    {
        var root = Path.Combine(history.MergeSessions.SessionDirectory(session.Id), "manual", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "content");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            await write(output, token).ConfigureAwait(false);
            output.Flush(true);
        }

        // 摘要走与提交时重扫暂存树同一套算法，否则提交会在摘要比对那一步失败。
        var manifest = await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false);
        return history.MergeSessions.Resolve(session, new(session.Plan.Revision, conflictId, inputSignature,
            MergeResolutionChoice.Manual, manifest.Files["content"]));
    }

    /// <summary>
    /// 放弃会话：此后再也不可能被应用。
    /// <para>
    /// 已发布的版本与产物<b>不</b>在这里删 —— 会话目录的清理另有其人，
    /// 它按 <c>ActiveRoots</c>（不再含本会话）判定哪些可以回收。
    /// </para>
    /// </summary>
    public MergeSession Abandon(MergeSession session) => history.MergeSessions.Update(session, MergeSessionState.Abandoned);

    /// <summary>本次会话的全部冲突及其解决状态。分页细则在存储层，这里只是一层转出。</summary>
    public IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> AllConflicts(MergeSession session)
        => history.MergeSessions.AllConflicts(session);
}
