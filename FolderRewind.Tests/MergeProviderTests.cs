using FolderRewind.History.Domain;
using FolderRewind.History.Merge;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Tests;

/// <summary>
/// 文本感知 provider 的对外行为：把「两侧改了同一文本文件的不同段落」自动合掉，
/// 剩下真正重叠的照旧上报成冲突。
/// <para>
/// 这里刻意只测「冲突变少、其余不动」这一条契约，不测具体某一种 diff 形态 ——
/// 行级算法本身由 <see cref="LineMergeAlgorithmTests"/> 覆盖。
/// </para>
/// </summary>
[TestClass]
public sealed class TextAwareMergeProviderTests
{
    private static readonly SourceId Source = SourceId.New();

    /// <summary>
    /// 两侧改同一个文本文件的不同段落：不产出冲突，自动合并树里拿到一份落盘的合并结果，
    /// 且它必须落在会话目录下 —— 提交那一步只认会话目录内的文件。
    /// </summary>
    [TestMethod]
    public void NonOverlappingTextEditsAreAutoMerged()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [("docs/notes.md", Utf8("one\ntwo\nthree\n"))],
            [("docs/notes.md", Utf8("one\nTWO\nthree\n"))],
            [("docs/notes.md", Utf8("one\ntwo\nTHREE\n"))]);

        Assert.IsEmpty(proposal.Conflicts);
        Assert.IsTrue(proposal.Automatic.Files.TryGetValue("docs/notes.md", out var value));
        StringAssert.StartsWith(value!.Handle, workingRoot);
        CollectionAssert.AreEqual(Utf8("one\nTWO\nTHREE\n"), File.ReadAllBytes(value.Handle));
    }

    /// <summary>
    /// 自动合并的结果文件，其摘要必须与「重扫暂存树」得到的一致 ——
    /// 提交时 <c>HistoryMergeCommitBuilder</c> 正是拿重扫的摘要与计划里的比对，不一致就拒绝提交。
    /// 顺带把摘要口径本身钉住：换域前缀或换算法会让存量会话里的摘要失效，必须是有意为之。
    /// </summary>
    [TestMethod]
    public async Task AutoMergedDigestMatchesTheTreeScan()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [("notes.md", Utf8("one\ntwo\nthree\n"))],
            [("notes.md", Utf8("one\nTWO\nthree\n"))],
            [("notes.md", Utf8("one\ntwo\nTHREE\n"))]);
        var value = proposal.Automatic.Files["notes.md"];

        // 复刻提交那一步：把 handle 拷到暂存树里的相对位置，再整棵树重扫一遍。
        var staging = System.IO.Path.Combine(directory.Root, "staging");
        var output = MergeStagingPathRules.ResolveUnderRoot(staging, "notes.md");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        File.Copy(value.Handle, output);
        var scanned = await MergeTreeManifest.ReadAsync(staging, _ => true, CancellationToken.None);

        Assert.AreEqual(value.Digest, scanned.Files["notes.md"].Digest);
        Assert.AreEqual(value.Length, scanned.Files["notes.md"].Length);
        Assert.AreEqual(ExpectedDigest(Utf8("one\nTWO\nTHREE\n")), value.Digest);
    }

    /// <summary>
    /// 同一份输入里，能合的文件合掉、合不掉的照旧上报：过滤而不是替换 ——
    /// 别把整条冲突列表丢掉，也别把合不掉的那条一起放行。
    /// </summary>
    [TestMethod]
    public void UnmergeableConflictSurvivesAlongsideAutoMergedFiles()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [("notes.md", Utf8("one\ntwo\nthree\n")), ("same.txt", Utf8("alpha\nbeta\n"))],
            [("notes.md", Utf8("one\nTWO\nthree\n")), ("same.txt", Utf8("alpha\nOURS\n"))],
            [("notes.md", Utf8("one\ntwo\nTHREE\n")), ("same.txt", Utf8("alpha\nTHEIRS\n"))]);

        Assert.IsTrue(proposal.Automatic.Files.ContainsKey("notes.md"));
        Assert.HasCount(1, proposal.Conflicts);
        Assert.AreEqual("same.txt", proposal.Conflicts[0].Subject.Paths.Single());
        Assert.AreEqual(MergeConflictKind.ModifyModify, proposal.Conflicts[0].Kind);
        Assert.IsFalse(proposal.Automatic.Files.ContainsKey("same.txt"));
    }

    /// <summary>
    /// 非白名单扩展名按二进制看：两侧都改了就报冲突，且不为此在会话目录里留下任何东西。
    /// </summary>
    [TestMethod]
    public void BinaryConflictIsNotAutoMergedAndWritesNothing()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [("logo.png", [0x01, 0x02, 0x03])],
            [("logo.png", [0x04, 0x05, 0x06])],
            [("logo.png", [0x07, 0x08, 0x09])]);

        Assert.HasCount(1, proposal.Conflicts);
        Assert.IsFalse(proposal.Automatic.Files.ContainsKey("logo.png"));
        Assert.IsFalse(Directory.Exists(workingRoot));
    }

    /// <summary>
    /// 扩展名在白名单里、但内容不是可认的文本（非法 UTF-8）：不做行级合并，照旧报冲突。
    /// 猜错编码会把内容写成乱码，比让用户手动选一边代价大得多。
    /// </summary>
    [TestMethod]
    public void UndecodableTextConflictIsNotAutoMerged()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [("notes.txt", [0x61, 0x80, 0x0A])],
            [("notes.txt", [0x62, 0x80, 0x0A])],
            [("notes.txt", [0x63, 0x80, 0x0A])]);

        Assert.HasCount(1, proposal.Conflicts);
        Assert.IsFalse(proposal.Automatic.Files.ContainsKey("notes.txt"));
    }

    /// <summary>
    /// 两侧各自新增同一个文件（没有共同祖先）：保持冲突，不做行级合并。
    /// <para>
    /// 这一条钉的是<b>行为</b>而不是那处早退判断：没有 base 时两侧内容都落在同一道缝上，
    /// 行级合并本来也只会把整篇报成一个冲突，结果一样。写出来是为了让「add/add 必须人工定」这件事有据可查。
    /// </para>
    /// </summary>
    [TestMethod]
    public void AddAddIsNotAutoMerged()
    {
        using var directory = new TempDirectory();
        var workingRoot = System.IO.Path.Combine(directory.Root, "session");
        var proposal = Analyze(directory, workingRoot,
            [],
            [("new.md", Utf8("alpha\nbeta\n"))],
            [("new.md", Utf8("gamma\ndelta\n"))]);

        Assert.HasCount(1, proposal.Conflicts);
        Assert.AreEqual(MergeConflictKind.AddAdd, proposal.Conflicts[0].Kind);
        Assert.IsFalse(proposal.Automatic.Files.ContainsKey("new.md"));
    }

    /// <summary>
    /// 两个版本串是刻意钉住的：改它等于让存量会话的计划对不上 provider，
    /// 换 provider 或换合并策略的口径都必须是有意为之，顺带也要想清楚存量会话怎么办。
    /// </summary>
    [TestMethod]
    public void VersionStringsArePinned()
    {
        var provider = new TextAwareMergeProvider();

        Assert.AreEqual("text-aware-file/1", provider.Version);
        Assert.AreEqual("text-merge/1", provider.PolicyVersion);
    }

    /// <summary>
    /// 三个目录各建一份树再跑 provider。文件一律用数组形式给：
    /// 写成 <c>(("a", x), ("b", y))</c> 会被当成「一个元组」而不是「两个元素」。
    /// </summary>
    private static MergeFileProposal Analyze(
        TempDirectory directory,
        string workingRoot,
        (string Path, byte[] Bytes)[] @base,
        (string Path, byte[] Bytes)[] ours,
        (string Path, byte[] Bytes)[] theirs)
        => new TextAwareMergeProvider().Analyze(
            Source,
            Manifest(directory.Create("base", @base)),
            Manifest(directory.Create("ours", ours)),
            Manifest(directory.Create("theirs", theirs)),
            workingRoot);

    private static MergeTreeManifest Manifest(string root)
        => MergeTreeManifest.ReadAsync(root, _ => true, CancellationToken.None).GetAwaiter().GetResult();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>按合并结果落盘的那套口径算期望摘要：域前缀 + 内容，十六进制。</summary>
    private static string ExpectedDigest(byte[] content)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("folderrewind/file/1\0").Concat(content).ToArray()));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Directory.CreateTempSubdirectory("folderrewind-provider-").FullName;
        }

        public string Root { get; }

        /// <summary>建一个目录并写入若干文件；相对路径里的 '/' 会落成真实层级。</summary>
        public string Create(string name, params (string Path, byte[] Bytes)[] files)
        {
            var directory = System.IO.Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            foreach (var file in files)
            {
                var path = System.IO.Path.Combine(directory, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, file.Bytes);
            }

            return directory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
