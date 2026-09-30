using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

/// <summary>
/// 窗口上一条冲突的展示投影。
/// <para>
/// 只带「展示与决策需要的那几样」：标识、类型、路径、解决状态。
/// 三方内容不在这里 —— 那要读盘，一条冲突一份，只在用户真正打开时才读（见
/// <see cref="HistoryMergeQueryService.LoadText"/>），列表阶段不碰。
/// </para>
/// </summary>
public sealed record MergeConflictView(
    string ConflictId,
    string InputSignature,
    MergeConflictKind Kind,
    SourceId SourceId,
    string SourceName,
    ImmutableArray<string> Paths,
    bool IsResolved,
    MergeResolutionChoice? Choice,
    bool CanEditPerHunk);

/// <summary>
/// 一条冲突的三方内容与行级合并结果。
/// <para>
/// <see cref="Documents"/> 与 <see cref="Refusal"/> 恰好只有一个非空：
/// 前者是「能逐块看、也能手改」，后者是「只能整份取一边」以及为什么。
/// 注意「合并结果里仍有冲突」不算拒绝 —— 那正是窗口存在的理由，此时 <see cref="Documents"/> 照给，
/// 由窗口把冲突块交给用户选。
/// </para>
/// </summary>
public sealed record MergeTextConflictView(string Path, TextMergeDocuments? Documents, TextMergeRefusal? Refusal)
{
    public bool CanEdit => Documents is not null;
}

/// <summary>
/// 一个<b>被自动合并掉</b>的文件：逻辑路径、所属来源，以及能逐行看的三方内容。
/// <para>
/// <see cref="Documents"/> 与 <see cref="Refusal"/> 的关系与 <see cref="MergeTextConflictView"/> 一致：
/// 恰好只有一个非空。不过这里的拒绝不表示「只能整份取一边」—— 这一份<b>已经合完了</b>，
/// 拒绝只说明窗口没法把它摊成逐行的样子给人看（不是文本、缺一侧内容、编码认不出）。
/// </para>
/// </summary>
public sealed record MergeAutoMergedFileView(
    SourceId SourceId,
    string SourceName,
    string Path,
    TextMergeDocuments? Documents,
    TextMergeRefusal? Refusal);

/// <summary>
/// 「两侧之间没有需要人工解决的冲突」时，窗口能拿到的全部信息。
/// <para>
/// <see cref="SourceCount"/> 只数真正做过逐文件合并的来源（见 <see cref="HistoryMergeQueryService.LoadAutoMergedAsync"/>），
/// <see cref="Files"/> 则是这些来源里合并结果与本地不同的那些文件。
/// </para>
/// </summary>
public sealed record MergeAutoMergeView(int SourceCount, ImmutableArray<MergeAutoMergedFileView> Files);

/// <summary>
/// 合并会话的读取投影：把库里存的冲突与三方内容，整成窗口能直接渲染的形状。
/// <para>
/// 不改变任何状态。解决一条冲突走 <see cref="HistoryMergeService"/>，那里才有写入口。
/// </para>
/// </summary>
public sealed class HistoryMergeQueryService
{
    private readonly HistoryRuntime _runtime;

    public HistoryMergeQueryService(HistoryRuntime runtime)
        => _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// 列出本次会话的全部冲突及其解决状态。
    /// <para>
    /// 一次全取。冲突数量与「两侧都改过同一批文件」的规模同阶，界面本身也要显示总数与进度，
    /// 分页在这里只会让计数与导航多出一次往返。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MergeConflictView>> ListConflictsAsync(
        MergeSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var names = await SourceNamesAsync(cancellationToken).ConfigureAwait(false);
        return AllConflicts(session).Select(item => new MergeConflictView(
            item.Conflict.Id,
            item.Conflict.InputSignature,
            item.Conflict.Kind,
            item.Conflict.Subject.SourceId,
            names.GetValueOrDefault(item.Conflict.Subject.SourceId, item.Conflict.Subject.SourceId.ToString()),
            item.Conflict.Subject.Paths,
            item.Resolution is not null,
            item.Resolution?.Choice,
            // 逐块编辑只对「两侧都改了同一个文本文件」成立：
            // 其余情形要么没有共同祖先（add/add），要么缺一侧（modify/delete），要么根本不是文件内容问题。
            item.Conflict.Kind == MergeConflictKind.ModifyModify
                && item.Conflict.Subject.Paths.Length == 1
                && TextMergePolicy.IsWhitelisted(item.Conflict.Subject.Paths[0]))).ToImmutableArray();
    }

    /// <summary>
    /// 读出这条冲突三方的内容并做行级合并。只对 <see cref="MergeConflictView.CanEditPerHunk"/>
    /// 为真的冲突有意义 —— 别的冲突没有「一个文件的三份内容」这回事，调用即错。
    /// </summary>
    public MergeTextConflictView LoadText(MergeSession session, string conflictId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var conflict = FindConflict(session, conflictId);
        if (conflict.Kind != MergeConflictKind.ModifyModify || conflict.Subject.Paths.Length != 1)
            throw new InvalidOperationException("This conflict has no single-file content to merge.");
        var path = conflict.Subject.Paths[0];

        // 三份 handle 未必都在（构造时 ModifyModify 一定齐，这里只是不吃「万一」）。
        // 缺的那一侧交给 TryMergeDocuments 判成 Missing，由它一家给原因，不在这里另判一套。
        if (!TextMergePolicy.TryMergeDocuments(
                path,
                Handle(conflict.Base, path),
                Handle(conflict.Ours, path),
                Handle(conflict.Theirs, path),
                out var documents,
                out var refusal))
        {
            return new(path, null, refusal);
        }

        return new(path, documents, null);
    }

    private static string Handle(ImmutableSortedDictionary<string, MergeFileValue> side, string path)
        => side.TryGetValue(path, out var value) ? value.Handle : string.Empty;

    /// <summary>
    /// 读出本次会话里<b>自动合并</b>掉的内容，供窗口在「一条冲突都没有」时铺开给人过目。
    /// <para>
    /// 只看 <see cref="HistoryMergeSourceAction.MergeFiles"/> 的来源：那是 provider 逐文件合过的地方，
    /// 也正是「自动合并」四个字的所指。其余来源（整份沿用一侧、整份丢弃）没有合并动作可言，
    /// 它们与本地的内容差异是两条分支之间的差异，不是本次合并算出来的，摆进来会让人误当成合并的产物。
    /// </para>
    /// <para>
    /// 每个文件各读一次三方、各跑一次行级合并，与窗口逐条冲突的做法相同；
    /// 数量与「这一侧真正改过、且两条分支都动过的文件数」同阶，不做分页。
    /// </para>
    /// </summary>
    public async Task<MergeAutoMergeView> LoadAutoMergedAsync(
        MergeSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _runtime.EnsureIndexCurrentAsync(cancellationToken).ConfigureAwait(false);
        var names = await SourceNamesAsync(cancellationToken).ConfigureAwait(false);
        var files = ImmutableArray.CreateBuilder<MergeAutoMergedFileView>();
        var sources = 0;
        foreach (var source in _runtime.MergeSessions.Sources(session))
        {
            if (source.Plan.Action != HistoryMergeSourceAction.MergeFiles) continue;
            sources++;
            var name = names.GetValueOrDefault(source.Plan.SourceId, source.Plan.SourceId.ToString());
            foreach (var path in ChangedPaths(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var basePath = Handle(source.Base.Files, path);
                var ourPath = Handle(source.Ours.Files, path);
                var theirPath = Handle(source.Theirs.Files, path);
                files.Add(TextMergePolicy.TryMergeDocuments(path, basePath, ourPath, theirPath, out var documents, out var refusal)
                    ? new(source.Plan.SourceId, name, path, documents, null)
                    : new(source.Plan.SourceId, name, path, null, refusal));
            }
        }

        return new(sources, files.ToImmutable());
    }

    /// <summary>
    /// 这次自动合并真正动过的文件：合并后的内容与本地那一份不同，或本地根本没有它。
    /// 与本地相同的文件不列 —— 它本来就在工作区里，列出来只会稀释真正要看的东西。
    /// <para>
    /// 摘要就是内容的身份（同一套域前缀的 SHA256），比摘要即比字节，不读盘。
    /// 清单是<b>有序</b>字典，遍历顺序稳定，窗口里的文件次序不会每次刷新都变。
    /// </para>
    /// </summary>
    private static IEnumerable<string> ChangedPaths(MergeSessionSource source)
        => source.Automatic.Files
            .Where(item => !source.Ours.Files.TryGetValue(item.Key, out var ours) || ours.Digest != item.Value.Digest)
            .Select(item => item.Key);

    /// <summary>分页取全部冲突；页大小与 <see cref="HistoryMergeService.AllConflicts"/> 对齐。</summary>
    private IReadOnlyList<(MergeConflict Conflict, MergeResolution? Resolution)> AllConflicts(MergeSession session)
    {
        var result = new List<(MergeConflict, MergeResolution?)>();
        for (var offset = 0; ; offset += 500)
        {
            var page = _runtime.MergeSessions.Conflicts(session, offset, 500);
            result.AddRange(page);
            if (page.Count < 500) return result;
        }
    }

    private MergeConflict FindConflict(MergeSession session, string conflictId)
    {
        var conflict = AllConflicts(session).Where(item => item.Conflict.Id == conflictId)
            .Select(item => item.Conflict).FirstOrDefault();
        return conflict ?? throw new InvalidDataException("Merge conflict is missing.");
    }

    /// <summary>
    /// 来源 id → 可读名。来源层面冲突（登记表变了、边界指纹不同）没有路径可显示，
    /// 只有 id 的话用户看不懂是哪一条来源。名字取该来源最近一次版本里的描述快照，与时间线用的是同一个字段。
    /// </summary>
    private async Task<IReadOnlyDictionary<SourceId, string>> SourceNamesAsync(CancellationToken cancellationToken)
    {
        var versions = await _runtime.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        return versions
            .GroupBy(version => version.SourceId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(version => version.CreatedAtUtc)
                    .Select(version => version.SourceDescriptorSnapshot.DisplayName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    ?? group.Key.ToString());
    }
}
