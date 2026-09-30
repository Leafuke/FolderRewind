using FolderRewind.History.Merge;
using System.Text;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LineMergeAlgorithmTests
{
    [TestMethod]
    public void OnlyOursChangedIsAppliedWithoutConflict()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "B", "c"], ["a", "b", "c"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nB\nc", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual(1, KindCount(outcome, LineMergeKind.OursOnly));
    }

    [TestMethod]
    public void OnlyTheirsChangedIsAppliedWithoutConflict()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "b", "c"], ["a", "b", "C"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nb\nC", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void EditsToDifferentLinesAreBothKept()
    {
        var outcome = Merge(["a", "b", "c", "d"], ["a", "B", "c", "d"], ["a", "b", "c", "D"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nB\nc\nD", Compose(outcome, LineMergeChoice.Ours));
    }

    /// <summary>
    /// 两侧各改相邻的一行：没有共同的未改动行可当作切点，但两处改动互不重叠，应该自动合掉。
    /// 按「未改动的行」切段会把它们并成一段而误报冲突，这一条守的就是那个坑。
    /// </summary>
    [TestMethod]
    public void EditsToAdjacentLinesAreBothKept()
    {
        var outcome = Merge(["a", "b", "c", "d"], ["a", "B", "c", "d"], ["a", "b", "C", "d"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nB\nC\nd", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual(1, KindCount(outcome, LineMergeKind.OursOnly));
        Assert.AreEqual(1, KindCount(outcome, LineMergeKind.TheirsOnly));
    }

    [TestMethod]
    public void SameLineChangedBothWaysIsAConflict()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "X", "c"], ["a", "Y", "c"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nX\nc", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nY\nc", Compose(outcome, LineMergeChoice.Theirs));
        Assert.AreEqual("a\nX\nY\nc", Compose(outcome, LineMergeChoice.Both));
    }

    [TestMethod]
    public void SameLineChangedTheSameWayIsNotAConflict()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "X", "c"], ["a", "X", "c"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual(1, KindCount(outcome, LineMergeKind.BothSame));
        Assert.AreEqual("a\nX\nc", Compose(outcome, LineMergeChoice.Ours));
    }

    /// <summary>
    /// 一侧的区间套住另一侧改的那一段：必须一起判定，不能在中间切开 ——
    /// 切开会让被套住那侧的替换只出现一半，另一段看起来「只有另一侧改了」而被静默丢弃。
    /// </summary>
    [TestMethod]
    public void EnclosingEditsAreReportedAsOneConflict()
    {
        var outcome = Merge(["a", "b", "c", "d", "e"], ["a", "X", "e"], ["a", "b", "Y", "d", "e"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nX\ne", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nb\nY\nd\ne", Compose(outcome, LineMergeChoice.Theirs));
    }

    [TestMethod]
    public void EmptyBaseWithOnlyOursAddingKeepsOurs()
    {
        var outcome = Merge([], ["a", "b"], []);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nb", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void EmptyBaseWithBothSidesAddingDifferentContentIsAConflict()
    {
        var outcome = Merge([], ["a"], ["b"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("b", Compose(outcome, LineMergeChoice.Theirs));
    }

    [TestMethod]
    public void EmptyBaseWithBothSidesAddingTheSameContentIsNotAConflict()
    {
        var outcome = Merge([], ["a"], ["a"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void AppendsAtEndFromBothSidesAreBothKept()
    {
        var outcome = Merge(["a"], ["a", "ours"], ["a", "theirs"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nours", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nours\ntheirs", Compose(outcome, LineMergeChoice.Both));
    }

    /// <summary>
    /// 两侧各在不同的缝上插入：互不相干，必须自动合掉。
    /// 旧版把「有插入的缝」一律当作不可切，会把两处新增并成一段而误报冲突。
    /// </summary>
    [TestMethod]
    public void InsertionsAtDifferentGapsAreBothKept()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "b", "X", "c"], ["a", "Y", "b", "c"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nY\nb\nX\nc", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void InsertionsAtTheSameGapWithDifferentContentConflict()
    {
        var outcome = Merge(["a", "b"], ["a", "X", "b"], ["a", "Y", "b"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nX\nb", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nY\nb", Compose(outcome, LineMergeChoice.Theirs));
    }

    /// <summary>
    /// 在同一条缝隙上冲突时，冲突块只该覆盖那道缝。
    /// 缝被当成「不可切的位置」时，冲突会把前面一整段没动过的行一起卷进来，
    /// 界面上就会出现一个巨大的冲突块，选「两个都要」还会把公共前缀复制一遍。
    /// </summary>
    [TestMethod]
    public void ContestedGapConflictDoesNotSwallowThePrecedingLines()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "b", "c", "ours"], ["a", "b", "c", "theirs"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.HasCount(2, outcome.Hunks);
        Assert.AreEqual(LineMergeKind.Unchanged, outcome.Hunks[0].Kind);
        Assert.AreEqual(3, outcome.Hunks[0].BaseCount);
        Assert.AreEqual(0, outcome.Hunks[1].BaseCount);
        CollectionAssert.AreEqual(new[] { "ours" }, outcome.Hunks[1].OursLines);
        Assert.AreEqual("a\nb\nc\nours\ntheirs", Compose(outcome, LineMergeChoice.Both));
    }

    /// <summary>
    /// 缝上插入、而缝两侧的行正被另一侧改动：缝归那个改动一起判定，不能被单列成缝区段 ——
    /// 单列会把裹着它的改动切散，其中一半看起来「只有另一侧改了」而被静默丢弃。
    /// </summary>
    [TestMethod]
    public void GapInsideAnEditedRegionIsNotSplitOut()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "X"], ["a", "b", "Y", "c"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nX", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nb\nY\nc", Compose(outcome, LineMergeChoice.Theirs));
    }

    /// <summary>我方在对方改动区间紧后面插入：互不相干，应自动合掉。</summary>
    [TestMethod]
    public void InsertionAfterTheirEditedRegionIsNotAConflict()
    {
        var outcome = Merge(["a", "b", "c", "d"], ["a", "b", "c", "X", "d"], ["a", "b", "c", "D"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nb\nc\nX\nD", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void InsertionsAtTheSameGapWithSameContentDoNotConflict()
    {
        var outcome = Merge(["a", "b"], ["a", "X", "b"], ["a", "X", "b"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nX\nb", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void DeletionFromOneSideIsApplied()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "c"], ["a", "b", "c"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nc", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void DeleteAgainstModifyOnTheSameLineConflicts()
    {
        var outcome = Merge(["a", "b", "c"], ["a", "c"], ["a", "Y", "c"]);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.AreEqual("a\nc", Compose(outcome, LineMergeChoice.Ours));
        Assert.AreEqual("a\nY\nc", Compose(outcome, LineMergeChoice.Theirs));
    }

    [TestMethod]
    public void UnchangedInputYieldsOneUnchangedHunk()
    {
        var outcome = Merge(["a", "b"], ["a", "b"], ["a", "b"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.HasCount(1, outcome.Hunks);
        Assert.AreEqual(LineMergeKind.Unchanged, outcome.Hunks[0].Kind);
    }

    [TestMethod]
    public void IdenticalFilesWithNoCommonLinesMergeCleanly()
    {
        var outcome = Merge(["a", "b"], ["x", "y"], ["x", "y"]);

        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("x\ny", Compose(outcome, LineMergeChoice.Ours));
    }

    /// <summary>
    /// 差异规模超过上限时整份文件当作一个区段，宁可报一个冲突也不猜。
    /// </summary>
    [TestMethod]
    public void OversizedDiffFallsBackToWholeFileConflict()
    {
        var outcome = LineMergeAlgorithm.Merge(
            ["a", "b", "c"],
            ["x", "y", "z"],
            ["1", "2", "3"],
            maxEditDistance: 0);

        Assert.AreEqual(1, outcome.ConflictCount);
        Assert.HasCount(1, outcome.Hunks);
        Assert.AreEqual("x\ny\nz", Compose(outcome, LineMergeChoice.Ours));
    }

    [TestMethod]
    public void EveryHunkLineCountMatchesItsSlice()
    {
        var outcome = Merge(
            ["a", "b", "c", "d", "e", "f"],
            ["a", "B", "c", "d", "e", "f"],
            ["a", "b", "c", "D", "e", "g"]);

        var baseOffset = 0;
        var ourOffset = 0;
        var theirOffset = 0;
        foreach (var hunk in outcome.Hunks)
        {
            Assert.AreEqual(baseOffset, hunk.BaseStart);
            Assert.AreEqual(ourOffset, hunk.OursStart);
            Assert.AreEqual(theirOffset, hunk.TheirsStart);
            Assert.HasCount(hunk.BaseCount, hunk.BaseLines);
            Assert.HasCount(hunk.OursCount, hunk.OursLines);
            Assert.HasCount(hunk.TheirsCount, hunk.TheirsLines);
            baseOffset += hunk.BaseCount;
            ourOffset += hunk.OursCount;
            theirOffset += hunk.TheirsCount;
        }

        Assert.AreEqual(6, baseOffset);
        Assert.AreEqual(6, ourOffset);
        Assert.AreEqual(6, theirOffset);
        Assert.IsFalse(outcome.HasConflicts);
        Assert.AreEqual("a\nB\nc\nD\ne\ng", Compose(outcome, LineMergeChoice.Ours));
    }

    /// <summary>
    /// 大文件上两侧各改一行：合并的开销只该与改动数同阶，不能跟着行数一起长。
    /// <para>
    /// 这一条钉的是切段规则。逐位置切会把没改动的长段碎成单行区段、再逐段并回去，
    /// 并回去的结果<b>看上去完全正确</b>（区段数一样是 5，所以只断言形状抓不到它），
    /// 代价全烧在重复分配上：改之前 3.2 万行要 4400 MB / 467 ms，改之后 7 MB / 4 ms。
    /// 文件一大就是假死，而结果分毫不差 —— 没有任何一处会报错，只有用户感到卡。
    /// 所以这里盯的是<b>分配量</b>，留足两个数量级的余量免得抖。
    /// </para>
    /// </summary>
    [TestMethod]
    public void MergeCostGrowsWithEditsNotWithUnchangedLength()
    {
        const int lines = 20000;
        var baseLines = new string[lines];
        var ourLines = new string[lines];
        var theirLines = new string[lines];
        for (var i = 0; i < lines; i++)
        {
            var text = $"line {i}";
            baseLines[i] = text;
            ourLines[i] = text;
            theirLines[i] = text;
        }

        ourLines[lines / 4] = "ours changed";
        theirLines[lines / 4 * 3] = "theirs changed";

        // 只量 Merge 本身：造输入与拼结果的分配不算进去。
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var outcome = Merge(baseLines, ourLines, theirLines);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var composed = Compose(outcome, LineMergeChoice.Ours).Split('\n');

        Assert.IsFalse(outcome.HasConflicts);
        Assert.IsLessThan(10, outcome.Hunks.Length, $"区段数应与改动数同阶，实际 {outcome.Hunks.Length}");
        Assert.IsLessThan(
            128L * 1024 * 1024,
            allocated,
            $"分配量应随改动数而不是行数增长，实际 {allocated / 1024 / 1024} MB");
        Assert.HasCount(lines, composed);
        Assert.AreEqual("ours changed", composed[lines / 4]);
        Assert.AreEqual("theirs changed", composed[lines / 4 * 3]);
    }

    private static LineMergeOutcome Merge(string[] baseLines, string[] ourLines, string[] theirLines)
        => LineMergeAlgorithm.Merge(baseLines, ourLines, theirLines);

    /// <summary>
    /// 走 <see cref="Enumerable"/> 而不是 <c>ImmutableArray.Count</c>：后者的属性是显式接口实现，
    /// 直接写 <c>hunks.Count(predicate)</c> 会解析成方法组而不是一次调用。
    /// </summary>
    private static int KindCount(LineMergeOutcome outcome, LineMergeKind kind)
        => outcome.Hunks.Where(hunk => hunk.Kind == kind).Count();

    private static string Compose(LineMergeOutcome outcome, LineMergeChoice conflictChoice)
        => string.Join("\n", LineMergeAlgorithm.Compose(outcome, conflictChoice));
}

[TestClass]
public sealed class TextMergePolicyTests
{
    [TestMethod]
    [DataRow("notes.md", true)]
    [DataRow("appsettings.json", true)]
    [DataRow("Program.cs", true)]
    [DataRow("page.XAML", true)]
    [DataRow(".gitignore", true)]
    [DataRow("photo.png", false)]
    [DataRow("book.docx", false)]
    [DataRow("archive.7z", false)]
    [DataRow("payload.db", false)]
    [DataRow("README", false)]
    [DataRow("", false)]
    public void WhitelistIsExtensionBased(string path, bool expected)
        => Assert.AreEqual(expected, TextMergePolicy.IsWhitelisted(path));

    [TestMethod]
    public void NonOverlappingEditsAreMergedAndKeepOursLineEndingsAndBom()
    {
        using var directory = new TempDirectory();
        var basePath = directory.Write("base.txt", Utf8Bom("one\r\ntwo\r\nthree\r\n"));
        var ourPath = directory.Write("ours.txt", Utf8Bom("one\r\nTWO\r\nthree\r\n"));
        var theirPath = directory.Write("theirs.txt", Utf8Bom("one\r\ntwo\r\nTHREE\r\n"));

        Assert.IsTrue(TextMergePolicy.TryMerge("notes.txt", basePath, ourPath, theirPath, out var merged, out var refusal), refusal?.Diagnostic);
        CollectionAssert.AreEqual(
            Utf8Bom("one\r\nTWO\r\nTHREE\r\n"),
            merged);
    }

    [TestMethod]
    public void ConflictingEditsAreRefused()
    {
        using var directory = new TempDirectory();
        var basePath = directory.Write("base.txt", Utf8("one\ntwo\n"));
        var ourPath = directory.Write("ours.txt", Utf8("one\nOURS\n"));
        var theirPath = directory.Write("theirs.txt", Utf8("one\nTHEIRS\n"));

        Assert.IsFalse(TextMergePolicy.TryMerge("notes.txt", basePath, ourPath, theirPath, out var merged, out var refusal));
        Assert.IsNull(merged);
        Assert.AreEqual(TextMergeRefusalKind.UnresolvedConflict, refusal!.Kind);
        StringAssert.Contains(refusal.Diagnostic, "unresolved conflict");
    }

    /// <summary>
    /// 白名单按逻辑路径判：三个 <c>*Path</c> 是会话目录里的物化文件，名字里没有真实扩展名。
    /// 谁把白名单判定挪去按物理路径判，这一条就会红。
    /// </summary>
    [TestMethod]
    public void WhitelistIsJudgedByTheLogicalPathNotThePhysicalOne()
    {
        using var directory = new TempDirectory();
        var basePath = directory.Write("content", Utf8("one\ntwo\n"));
        var ourPath = directory.Write("content2", Utf8("one\nTWO\n"));

        Assert.IsFalse(TextMergePolicy.TryMerge("photo.png", basePath, ourPath, basePath, out _, out var refusal));
        Assert.AreEqual(TextMergeRefusalKind.NotWhitelisted, refusal!.Kind);
    }

    [TestMethod]
    public void MissingTrailingNewlineIsPreserved()
    {
        using var directory = new TempDirectory();
        var basePath = directory.Write("base.txt", Utf8("one\ntwo"));
        var ourPath = directory.Write("ours.txt", Utf8("one\nTWO"));
        var theirPath = directory.Write("theirs.txt", Utf8("one\ntwo"));

        Assert.IsTrue(TextMergePolicy.TryMerge("notes.txt", basePath, ourPath, theirPath, out var merged, out var refusal), refusal?.Diagnostic);
        CollectionAssert.AreEqual(Utf8("one\nTWO"), merged);
    }

    [TestMethod]
    public void BinaryContentIsRefusedEvenWithATextExtension()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("fake.txt", [0x61, 0x00, 0x62]);

        Assert.IsFalse(TextMergePolicy.TryLoad(path, out var content, out var refusal));
        Assert.IsNull(content);
        Assert.AreEqual(TextMergeRefusalKind.Binary, refusal!.Kind);
        StringAssert.Contains(refusal.Diagnostic, "NUL");
    }

    [TestMethod]
    public void NonUtf8ContentIsRefused()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("gbk.txt", [0x61, 0x80, 0x62]);

        Assert.IsFalse(TextMergePolicy.TryLoad(path, out _, out var refusal));
        Assert.AreEqual(TextMergeRefusalKind.Undecodable, refusal!.Kind);
        StringAssert.Contains(refusal.Diagnostic, "UTF-8");
    }

    [TestMethod]
    public void Utf16FilesAreRead()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("wide.txt", Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("one\ntwo\n"))
            .ToArray());

        Assert.IsTrue(TextMergePolicy.TryLoad(path, out var content, out var refusal), refusal?.Diagnostic);
        CollectionAssert.AreEqual(new[] { "one", "two" }, content!.Lines);
        Assert.IsTrue(content.EndsWithNewline);
    }

    [TestMethod]
    public void OversizedFileIsRefused()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Root, "big.txt");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(TextMergePolicy.MaxFileBytes + 1);
        }

        Assert.IsFalse(TextMergePolicy.TryLoad(path, out _, out var refusal));
        Assert.AreEqual(TextMergeRefusalKind.TooLarge, refusal!.Kind);
        StringAssert.Contains(refusal.Diagnostic, "limit");
    }

    /// <summary>
    /// 混用行尾的文件会被统一成最先出现的那一种 —— 这是有损的，但比把某一种行尾当正文写进内容里要诚实。
    /// 用户改完一个混用行尾的文件，看到的会是整份行尾变化，不是内容出错。
    /// </summary>
    [TestMethod]
    public void MixedLineEndingsNormalizeToTheFirstOne()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("mixed.txt", Utf8("one\r\ntwo\nthree\r\n"));

        Assert.IsTrue(TextMergePolicy.TryLoad(path, out var content, out var refusal), refusal?.Diagnostic);
        CollectionAssert.AreEqual(new[] { "one", "two", "three" }, content!.Lines);
        Assert.AreEqual("\r\n", content.Newline);
        CollectionAssert.AreEqual(Utf8("one\r\ntwo\r\nthree\r\n"), TextMergePolicy.Serialize(content, content.Lines));
    }

    [TestMethod]
    public void EmptyFileRoundTrips()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("empty.txt", []);

        Assert.IsTrue(TextMergePolicy.TryLoad(path, out var content, out var refusal), refusal?.Diagnostic);
        Assert.IsEmpty(content!.Lines);
        Assert.IsFalse(content.EndsWithNewline);
        Assert.IsEmpty(TextMergePolicy.Serialize(content, content.Lines));
    }

    /// <summary>
    /// 手改文本写回字节时的跨边界往返：编码、BOM、行尾风格必须沿用原文件，
    /// 而「末尾有没有换行」必须看用户写的文本、不看原文件。
    /// </summary>
    /// <remarks>
    /// 这里刻意用裸 <c>\r</c> 当用户文本的换行：界面文本框在多行时给出的就是 <c>\r</c>，
    /// 若 <c>SerializeEdited</c> 不按同一套切行规则处理，用户手改一次就会把整份文件的行尾风格换掉。
    /// </remarks>
    [TestMethod]
    public void HandEditedTextKeepsOriginalEncodingBomAndLineEndings()
    {
        using var directory = new TempDirectory();
        var path = directory.Write("notes.txt", Utf8Bom("one\r\ntwo\r\n"));

        Assert.IsTrue(TextMergePolicy.TryLoad(path, out var content, out var refusal), refusal?.Diagnostic);
        Assert.AreEqual("\r\n", content!.Newline);
        Assert.IsTrue(content.EndsWithNewline);

        // 文本框里删掉了末尾换行 —— 结果就不该有末尾换行，不能按旧文件的状态补回去。
        CollectionAssert.AreEqual(
            Utf8Bom("one\r\ntwo"),
            TextMergePolicy.SerializeEdited("one\rtwo", content));

        // 文本框末尾留着换行 —— 那就保留，且行尾仍是原有的 CRLF。
        CollectionAssert.AreEqual(
            Utf8Bom("one\r\ntwo\r\n"),
            TextMergePolicy.SerializeEdited("one\rtwo\r", content));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Utf8Bom(string text)
        => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Directory.CreateTempSubdirectory("folderrewind-textmerge-").FullName;
        }

        public string Root { get; }

        public string Write(string name, byte[] bytes)
        {
            var path = System.IO.Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
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
