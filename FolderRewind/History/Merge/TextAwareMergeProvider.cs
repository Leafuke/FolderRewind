using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

namespace FolderRewind.History.Merge;

/// <summary>
/// 在 <see cref="GenericFileMergeProvider"/> 之上加一层「文本文件按行合」。
/// <para>
/// 先原样委托通用实现，再逐条看它报出来的冲突：凡是白名单文本文件、只有「两侧都改了同一个文件」这一种，
/// 就拿三份 handle 读内容跑 <see cref="TextMergePolicy"/> 做行级合并；
/// 合得干净就把结果落成会话目录内的文件、塞回自动合并树、把这条冲突去掉；
/// 合不干净（仍留冲突、编码不认、超限、不是文本）就原样留着，交给人去解决 —— 不写任何冲突标记。
/// </para>
/// <para>
/// 因此这个 provider 只会让冲突<b>变少</b>，不会新增、也不会改变其余冲突的形态：
/// 结构冲突、源层面冲突、add/add、modify/delete 全部按通用实现的原样输出。
/// add/add 之所以不做行级：两侧没有共同祖先，整份文件都落在同一道缝上，行级合并只会把整篇报成一个冲突，
/// 结果与现在一样，白白多读两次文件。modify/delete 少一侧内容，三方合并无从谈起。
/// </para>
/// <para>
/// 版本串全都写在这里、也只在这里：<see cref="GenericFileMergeProvider"/> 用它们算冲突签名，
/// 于是「谁算出来的冲突」这件事只有一处定义，不会出现签名里写着通用版、行为却是文本感知版的分裂。
/// 换 provider 会改变计划里的版本串，存量会话因此失效 —— 这是接受的代价。
/// </para>
/// </summary>
public sealed class TextAwareMergeProvider : IHistoryMergeProvider
{
    private const string ProviderId = "text-aware-file/1";
    private const string PolicyId = "text-merge/1";

    private readonly GenericFileMergeProvider _generic = new(ProviderId, PolicyId);

    public string Version => ProviderId;

    public string PolicyVersion => PolicyId;

    public MergeFileProposal Analyze(
        SourceId source,
        MergeTreeManifest @base,
        MergeTreeManifest ours,
        MergeTreeManifest theirs,
        string workingRoot)
    {
        var proposal = _generic.Analyze(source, @base, ours, theirs, workingRoot);
        if (proposal.Conflicts.IsEmpty) return proposal;

        // workingRoot 只在真的合出东西时才用得上，别为「一条都合不掉」的常见情形建目录。
        var automatic = proposal.Automatic.Files.ToBuilder();
        var remaining = ImmutableArray.CreateBuilder<MergeConflict>(proposal.Conflicts.Length);
        foreach (var conflict in proposal.Conflicts)
        {
            if (TryAutoMerge(conflict, workingRoot) is { } merged) automatic[merged.Path] = merged.Value;
            else remaining.Add(conflict);
        }

        return new(MergeTreeManifest.Create(automatic), remaining.ToImmutable());
    }

    /// <summary>
    /// 试把一条内容冲突自动合掉；合不掉返回 <c>null</c>，那条冲突照旧上报。
    /// <para>
    /// 合并策略给的诊断串在这里被丢掉：它只说明「为什么没自动合」，而这个后果本身就表现为
    /// 冲突照常出现在界面上、由人来选，没有额外信息要传给谁（History 层也没有日志设施）。
    /// </para>
    /// <para>
    /// 「结果写不进会话目录」也归到同一个后果里，见下面的 catch。
    /// </para>
    /// </summary>
    private static (string Path, MergeFileValue Value)? TryAutoMerge(MergeConflict conflict, string workingRoot)
    {
        if (conflict.Kind != MergeConflictKind.ModifyModify || conflict.Subject.Paths.Length != 1) return null;
        var relative = conflict.Subject.Paths[0];

        // 三份内容都得在：ModifyModify 下构造时必然齐全，这里只是不给「缺一侧」留猜的余地。
        if (conflict.Base.GetValueOrDefault(relative) is not { } baseValue
            || conflict.Ours.GetValueOrDefault(relative) is not { } ourValue
            || conflict.Theirs.GetValueOrDefault(relative) is not { } theirValue) return null;

        // 白名单（按逻辑路径判，文本口径的扩展名，不做内容嗅探）、编码、大小上限、行级合并，
        // 全都在 TryMerge 这一条路径上判，这里不重复判一遍。
        if (!TextMergePolicy.TryMerge(relative, baseValue.Handle, ourValue.Handle, theirValue.Handle, out var merged, out _))
            return null;

        // 落盘失败（盘满、会话目录不可写）不该把整次准备掀掉：这条文件退回「有冲突、等人选」，
        // 与「策略判它合不了」是同一个后果，用户在窗口里照样处理得了。
        try
        {
            return (relative, Write(workingRoot, merged!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把自动合并的结果落成会话目录内的文件。
    /// <para>
    /// 必须落盘：提交那一步只认 <c>Handle</c> 指向会话目录内的真实文件（会逐级查重解析点后整份拷贝），
    /// 光把字节留在内存里是交不出去的。形态与 <c>HistoryMergeService.ImportManualAsync</c> 的手改结果一致，
    /// 都是「各自独立目录 + 一个 content 文件」，互不覆盖。
    /// </para>
    /// <para>
    /// 摘要走 <see cref="MergeTreeManifest.ReadFileValue"/> —— 与提交时重扫暂存树用的是同一套算法，
    /// 口径不一致会让提交在摘要比对那一步失败。
    /// </para>
    /// </summary>
    private static MergeFileValue Write(string workingRoot, byte[] content)
    {
        var directory = Path.Combine(workingRoot, "auto", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "content");
        File.WriteAllBytes(path, content);
        return MergeTreeManifest.ReadFileValue(path);
    }
}
