using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FolderRewind.History.Merge;

/// <summary>一个区段在三个版本里的关系。</summary>
public enum LineMergeKind
{
    /// <summary>三方一致，或两侧都没碰这一段。</summary>
    Unchanged,

    /// <summary>只有本地（ours）改了。</summary>
    OursOnly,

    /// <summary>只有对方（theirs）改了。</summary>
    TheirsOnly,

    /// <summary>两侧都改了，且改成了同样的内容 —— 结果确定，不算冲突。</summary>
    BothSame,

    /// <summary>两侧改了同一段且结果不同。只有这一段需要人来定。</summary>
    Conflict
}

/// <summary>人对一段冲突的处置。</summary>
public enum LineMergeChoice
{
    /// <summary>取本地版本。</summary>
    Ours,

    /// <summary>取对方版本。</summary>
    Theirs,

    /// <summary>两个都要：本地内容在前，对方内容在后。</summary>
    Both
}

/// <summary>
/// 一个区段：三边各自的行区间与行内容。
/// <para>
/// 区间是各版本内部的行坐标（0 基，起始为 <c>Start</c>、长度为 <c>Count</c>），
/// 供界面把区段映回三栏的行号；行内容直接带上，省得界面再切一次。
/// 未变化的区段三边内容相同，只有 <see cref="Conflict"/> 才需要选择。
/// </para>
/// <para>
/// 区间长度可以是 0：两侧在同一道缝上各插了一段时，产出的就是这种「只有插入」的区段，
/// 它的 base 区间为空、两侧区间非空。界面读数时不要假设 <c>Count</c> 一定大于 0。
/// </para>
/// </summary>
public sealed record LineMergeHunk(
    LineMergeKind Kind,
    int BaseStart,
    int BaseCount,
    int OursStart,
    int OursCount,
    int TheirsStart,
    int TheirsCount,
    ImmutableArray<string> BaseLines,
    ImmutableArray<string> OursLines,
    ImmutableArray<string> TheirsLines);

/// <summary>整份文件的合并结果。</summary>
public sealed record LineMergeOutcome(ImmutableArray<LineMergeHunk> Hunks)
{
    public bool HasConflicts => Hunks.Any(hunk => hunk.Kind == LineMergeKind.Conflict);

    public int ConflictCount => Hunks.Count(hunk => hunk.Kind == LineMergeKind.Conflict);
}

/// <summary>
/// 行级三方合并：以 base 为共同祖先，把 ours 与 theirs 的改动合到一份结果里。
/// <para>
/// 做法是 diff3 的标准两步 —— 先各自对 base 求行级差异，再按 base 坐标把两侧差异切成互不重叠的区段；
/// 每个区段只要比较「本地这一段的实际内容」与「对方这一段的实际内容」就能归类，不必逐条推断编辑操作。
/// 切段规则是全部正确性的来源，见 <see cref="BuildBlocked"/>。
/// </para>
/// <para>
/// 区段有两种：占若干 base 行的<b>行区段</b>，以及只占一道缝的<b>缝区段</b>
/// （缝 g 指 base 第 g 行之前）。把缝单列出来是为了让「两侧在同一道缝上各插一段」这种冲突
/// 只覆盖那道缝本身，而不会把前面一大段没动过的行也卷进冲突里。
/// </para>
/// <para>
/// 只有真正重叠的改动才会被判成冲突：两侧改同一段、两侧在同一道缝上各插一段、
/// 一侧的区间套住另一侧改动（含缝落在某侧区间内部的情形，见 <see cref="BuildBlocked"/>）。
/// 其余情形一律自动合掉，包括两侧各改相邻的段落、两侧各插在不同的缝上、在我方改动的紧后方插入。
/// </para>
/// <para>
/// 本类只做同内容判定，不看扩展名、不管编码与行尾 —— 那些是 <see cref="TextMergePolicy"/> 的事。
/// </para>
/// </summary>
public static class LineMergeAlgorithm
{
    /// <summary>
    /// 差异规模的上限。超过它的输入不做行级合并，由调用方回退到整文件语义。
    /// 没有这个闸门，两侧大规模改写会让编辑距离逼近行数和，时间和内存都会失控
    /// （回溯要留着每一步的对角线快照，内存约为 D²/2 个 int）。
    /// </summary>
    public const int DefaultMaxEditDistance = 2000;

    /// <summary>合并三个行序列。不改变输入，也不依赖任何外部状态。</summary>
    public static LineMergeOutcome Merge(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> ourLines,
        IReadOnlyList<string> theirLines,
        int maxEditDistance = DefaultMaxEditDistance)
    {
        ArgumentNullException.ThrowIfNull(baseLines);
        ArgumentNullException.ThrowIfNull(ourLines);
        ArgumentNullException.ThrowIfNull(theirLines);
        if (maxEditDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxEditDistance));

        // 行先映射成整数再比，避免反复比字符串。
        var interner = new LineInterner();
        var baseIds = interner.Intern(baseLines);
        var ourChanges = Diff(baseIds, interner.Intern(ourLines), maxEditDistance);
        var theirChanges = Diff(baseIds, interner.Intern(theirLines), maxEditDistance);

        // 任一侧差异超出上限：整份文件当成一个大区段，让它走「两侧都改了」的判定。
        // 宁可报一个冲突，也不能猜。
        if (ourChanges is null || theirChanges is null)
        {
            return MergeAsSingleRegion(baseLines, ourLines, theirLines);
        }

        var ours = SideChanges.Build(baseLines.Count, ourChanges, ourLines);
        var theirs = SideChanges.Build(baseLines.Count, theirChanges, theirLines);
        var blocked = BuildBlocked(ourChanges, theirChanges, baseLines.Count);

        var hunks = ImmutableArray.CreateBuilder<LineMergeHunk>();
        var ourOffset = 0;
        var theirOffset = 0;

        // 缝 0 在第一个行区段之前；缝 n 由循环最后一轮的 hi == n 收掉。
        EmitGap(ours, theirs, 0, ref ourOffset, ref theirOffset, hunks);
        var lo = 0;
        for (var hi = 1; hi <= baseLines.Count; hi++)
        {
            if (hi < baseLines.Count && blocked[hi]) continue;
            EmitLines(baseLines, ours, theirs, lo, hi, ref ourOffset, ref theirOffset, hunks);
            EmitGap(ours, theirs, hi, ref ourOffset, ref theirOffset, hunks);
            lo = hi;
        }

        return new(Coalesce(hunks.ToImmutable()));
    }

    /// <summary>
    /// 按给定选择把区段拼成最终行序列。
    /// <para>
    /// <paramref name="resolveConflict"/> 的调用时机是有约定的：<b>每个冲突区段恰好调用一次，按区段在文件里的先后顺序</b>，
    /// 未变化的区段不会调用它。界面因此可以拿一个按顺序出队的队列当选择函数，不必自己按区段下标查表 ——
    /// 但也意味着这个函数必须是纯的：有副作用（比如弹窗问人）时，调用次数与顺序就是它的全部语义。
    /// </para>
    /// </summary>
    public static ImmutableArray<string> Compose(
        LineMergeOutcome outcome,
        Func<LineMergeHunk, LineMergeChoice> resolveConflict)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(resolveConflict);
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var hunk in outcome.Hunks)
        {
            foreach (var line in LinesFor(hunk, resolveConflict))
            {
                builder.Add(line);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>只对真冲突做选择的便捷入口，用于「全部取某一边」。</summary>
    public static ImmutableArray<string> Compose(LineMergeOutcome outcome, LineMergeChoice conflictChoice)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Compose(outcome, _ => conflictChoice);
    }

    /// <summary>
    /// 单个区段的最终行。界面逐块预览「这一块最终会长成什么样」时用它。
    /// <para>
    /// 特意做成公开的：归类规则（未变化取哪边、两侧同改取哪边）只该有一处实现，
    /// 界面层再抄一遍就等于把「抄错」的机会也抄了进来。
    /// </para>
    /// <para>
    /// 非冲突区段不看 <paramref name="conflictChoice"/> —— 那些区段的结果是确定的，没有可选项。
    /// </para>
    /// </summary>
    public static ImmutableArray<string> Resolve(LineMergeHunk hunk, LineMergeChoice conflictChoice)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        return LinesFor(hunk, _ => conflictChoice);
    }

    /// <summary>
    /// 一个区段的最终行。未变化的区段三边相同，取哪边都一样；
    /// 两侧同改的区段结果也已确定，不需要问人。
    /// </summary>
    private static ImmutableArray<string> LinesFor(LineMergeHunk hunk, Func<LineMergeHunk, LineMergeChoice> resolveConflict)
        => hunk.Kind switch
        {
            LineMergeKind.Unchanged => hunk.BaseLines,
            LineMergeKind.OursOnly => hunk.OursLines,
            LineMergeKind.TheirsOnly => hunk.TheirsLines,
            LineMergeKind.BothSame => hunk.OursLines,
            _ => resolveConflict(hunk) switch
            {
                LineMergeChoice.Theirs => hunk.TheirsLines,
                LineMergeChoice.Both => hunk.OursLines.AddRange(hunk.TheirsLines),
                _ => hunk.OursLines
            }
        };

    /// <summary>
    /// 标出不能当作行区段边界的位置。两条判据：
    /// <list type="number">
    /// <item><b>该位置严格落在某个改动区间的内部</b>。区间的两端是能切的 ——
    /// 这正是「两侧各改相邻的一行」能被自动合掉而不是误报冲突的原因：我方改 [1,2)、对方改 [2,3)，
    /// 在 2 处切开后两个区段各自只含一侧的改动。
    /// 反过来，若区间只是部分重叠（我方 [1,3)、对方 [2,4)），2 与 3 分别落在对方/我方的区间内部，
    /// 谁都切不开，于是两个改动被留在同一个区段里一起判定 —— 这是必需的，
    /// 否则会在区间中间切出一刀，让一侧的替换被拆到两个区段、其中一段看起来「只有另一侧改了」而被静默丢弃。</item>
    /// <item><b>该位置落在某个改动区间的内部，而那里正好有一道缝上有插入</b>。同一条判据 ——
    /// 缝两侧的行都属于同一个改动，缝也就跟着归那个行区段一起判定，
    /// 不能单列成缝区段，否则会把裹着它的那个改动切散。</item>
    /// </list>
    /// 注意插入<b>不会</b>挡住它所在的缝：缝单列成区段之后，两侧在同一道缝上各插一段就是那个缝区段自己的冲突，
    /// 与前后文无关，不必、也不该把前面的行卷进来。
    /// </summary>
    private static bool[] BuildBlocked(List<Change> ourChanges, List<Change> theirChanges, int baseCount)
    {
        var blocked = new bool[baseCount + 1];
        MarkRegionInteriors(ourChanges, blocked);
        MarkRegionInteriors(theirChanges, blocked);
        return blocked;
    }

    private static void MarkRegionInteriors(List<Change> changes, bool[] blocked)
    {
        foreach (var change in changes)
        {
            for (var i = change.BaseStart + 1; i < change.BaseStart + change.BaseCount; i++) blocked[i] = true;
        }
    }

    private static void EmitLines(
        IReadOnlyList<string> baseLines,
        SideChanges ours,
        SideChanges theirs,
        int lo,
        int hi,
        ref int ourOffset,
        ref int theirOffset,
        ImmutableArray<LineMergeHunk>.Builder hunks)
    {
        if (lo == hi) return;
        var baseSeg = Slice(baseLines, lo, hi - lo);
        var ourSeg = ours.LineSegment(baseLines, lo, hi);
        var theirSeg = theirs.LineSegment(baseLines, lo, hi);
        Add(Classify(baseSeg, ourSeg, theirSeg), lo, hi - lo, baseSeg, ourSeg, theirSeg, ref ourOffset, ref theirOffset, hunks);
    }

    /// <summary>
    /// 一道缝上的插入。只在切点处调用 —— 落在改动区间内部的缝归那个行区段，轮的不到这里。
    /// </summary>
    private static void EmitGap(
        SideChanges ours,
        SideChanges theirs,
        int gap,
        ref int ourOffset,
        ref int theirOffset,
        ImmutableArray<LineMergeHunk>.Builder hunks)
    {
        var ourSeg = ours.GapSegment(gap);
        var theirSeg = theirs.GapSegment(gap);
        if (ourSeg.Length == 0 && theirSeg.Length == 0) return;
        Add(
            Classify(ImmutableArray<string>.Empty, ourSeg, theirSeg),
            gap,
            0,
            ImmutableArray<string>.Empty,
            ourSeg,
            theirSeg,
            ref ourOffset,
            ref theirOffset,
            hunks);
    }

    private static void Add(
        LineMergeKind kind,
        int baseStart,
        int baseCount,
        ImmutableArray<string> baseSeg,
        ImmutableArray<string> ourSeg,
        ImmutableArray<string> theirSeg,
        ref int ourOffset,
        ref int theirOffset,
        ImmutableArray<LineMergeHunk>.Builder hunks)
    {
        hunks.Add(new(
            kind,
            baseStart,
            baseCount,
            ourOffset,
            ourSeg.Length,
            theirOffset,
            theirSeg.Length,
            baseSeg,
            ourSeg,
            theirSeg));
        ourOffset += ourSeg.Length;
        theirOffset += theirSeg.Length;
    }

    /// <summary>
    /// 按「这一段三边各自的实际内容」归类。区段切得对时，段内至多只有一侧的改动，
    /// 所以只要比内容就能判，不需要逐条推断编辑操作。
    /// </summary>
    private static LineMergeKind Classify(
        ImmutableArray<string> baseSeg,
        ImmutableArray<string> ourSeg,
        ImmutableArray<string> theirSeg)
    {
        var ourMatchesBase = SequenceEqual(ourSeg, baseSeg);
        var theirMatchesBase = SequenceEqual(theirSeg, baseSeg);
        if (ourMatchesBase && theirMatchesBase) return LineMergeKind.Unchanged;
        if (ourMatchesBase) return LineMergeKind.TheirsOnly;
        if (theirMatchesBase) return LineMergeKind.OursOnly;
        return SequenceEqual(ourSeg, theirSeg) ? LineMergeKind.BothSame : LineMergeKind.Conflict;
    }

    /// <summary>相邻的同类型区段并成一段，免得未变化区段碎成一堆单行。</summary>
    private static ImmutableArray<LineMergeHunk> Coalesce(ImmutableArray<LineMergeHunk> hunks)
    {
        if (hunks.Length < 2) return hunks;
        var result = ImmutableArray.CreateBuilder<LineMergeHunk>();
        var current = hunks[0];
        for (var i = 1; i < hunks.Length; i++)
        {
            var next = hunks[i];
            if (next.Kind == current.Kind)
            {
                current = current with
                {
                    BaseCount = current.BaseCount + next.BaseCount,
                    OursCount = current.OursCount + next.OursCount,
                    TheirsCount = current.TheirsCount + next.TheirsCount,
                    BaseLines = current.BaseLines.AddRange(next.BaseLines),
                    OursLines = current.OursLines.AddRange(next.OursLines),
                    TheirsLines = current.TheirsLines.AddRange(next.TheirsLines)
                };
                continue;
            }

            result.Add(current);
            current = next;
        }

        result.Add(current);
        return result.ToImmutable();
    }

    /// <summary>
    /// 差异大到无法行级归并时的兜底：整份文件算一个区段，交给统一的归类逻辑去判
    /// 「两侧是否改成了同一份内容」，改不成就是冲突。
    /// </summary>
    private static LineMergeOutcome MergeAsSingleRegion(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> ourLines,
        IReadOnlyList<string> theirLines)
    {
        var baseSeg = ToImmutable(baseLines);
        var ourSeg = ToImmutable(ourLines);
        var theirSeg = ToImmutable(theirLines);
        return new([new(
            Classify(baseSeg, ourSeg, theirSeg),
            0, baseSeg.Length,
            0, ourSeg.Length,
            0, theirSeg.Length,
            baseSeg,
            ourSeg,
            theirSeg)]);
    }

    /// <summary>
    /// 把 a 变成 b 的改动区间；<paramref name="maxEditDistance"/> 内解不出来时返回 null。
    /// <para>
    /// Myers 的贪心 O(ND) 算法：每轮 d 记录「走了 d 步能到的最远 x」，靠回溯把匹配对还原出来，
    /// 相邻匹配对之间的空档就是改动区间。回溯时按匹配对而不是按编辑操作还原，
    /// 是为了让「删两行又添两行」自然并成一个区间，不会碎成两个。
    /// </para>
    /// </summary>
    private static List<Change>? Diff(int[] a, int[] b, int maxEditDistance)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0 || m == 0)
        {
            return n == 0 && m == 0 ? [] : [new Change(0, n, 0, m)];
        }

        // trace[d][(k + d) / 2] = 第 d 步走完后对角线 k 上能到达的最远 x。
        // 只留需要的窗口：完整的 V 是 O((N+M) * D) 内存，大会直接压垮进程。
        var trace = new List<int[]>();
        for (var d = 0; d <= n + m; d++)
        {
            if (d > maxEditDistance) return null;
            var slice = new int[d + 1];
            trace.Add(slice);
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (d == 0) x = 0;
                else if (k == -d || (k != d && At(trace, d - 1, k - 1) < At(trace, d - 1, k + 1))) x = At(trace, d - 1, k + 1);
                else x = At(trace, d - 1, k - 1) + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                slice[(k + d) / 2] = x;
                if (x >= n && y >= m) return Matches(trace, d, n, m);
            }
        }

        return null;
    }

    /// <summary>第 d 步、对角线 k 上的最远 x。调用方保证 |k| &lt;= d 且 k 与 d 同奇偶。</summary>
    private static int At(List<int[]> trace, int d, int k) => trace[d][(k + d) / 2];

    /// <summary>回溯出全部匹配对（两侧下标一一对应且行内容相同），据此算出改动区间。</summary>
    private static List<Change> Matches(List<int[]> trace, int finalD, int n, int m)
    {
        var matches = new List<(int A, int B)>();
        var x = n;
        var y = m;
        for (var d = finalD; d > 0; d--)
        {
            var k = x - y;
            var prevK = k == -d || (k != d && At(trace, d - 1, k - 1) < At(trace, d - 1, k + 1)) ? k + 1 : k - 1;
            var prevX = At(trace, d - 1, prevK);
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY) { matches.Add((x - 1, y - 1)); x--; y--; }
            if (x == prevX) y--; else x--;
        }

        // d 归零后还剩开头那段共同的斜线。
        while (x > 0 && y > 0) { matches.Add((x - 1, y - 1)); x--; y--; }
        matches.Reverse();

        var changes = new List<Change>();
        var ai = 0;
        var bi = 0;
        foreach (var (ma, mb) in matches)
        {
            if (ma > ai || mb > bi) changes.Add(new Change(ai, ma - ai, bi, mb - bi));
            ai = ma + 1;
            bi = mb + 1;
        }

        if (ai < n || bi < m) changes.Add(new Change(ai, n - ai, bi, m - bi));
        return changes;
    }

    /// <summary>
    /// base 坐标下的一个改动区间：base 的 [BaseStart, BaseStart+BaseCount) 换成该侧的第 [SideStart, SideStart+SideCount)。
    /// <c>BaseCount == 0</c> 表示纯插入，位置就是那道缝。
    /// </summary>
    private readonly record struct Change(int BaseStart, int BaseCount, int SideStart, int SideCount);

    /// <summary>某一侧的全部改动，按 base 坐标摊平成查表结构，便于逐区段拼内容。</summary>
    private sealed class SideChanges
    {
        private readonly bool[] _touched;
        private readonly ImmutableArray<string>?[] _replaceAt;
        private readonly ImmutableArray<string>?[] _insertAt;

        private SideChanges(
            bool[] touched,
            ImmutableArray<string>?[] replaceAt,
            ImmutableArray<string>?[] insertAt)
        {
            _touched = touched;
            _replaceAt = replaceAt;
            _insertAt = insertAt;
        }

        public static SideChanges Build(int baseCount, List<Change> changes, IReadOnlyList<string> sideLines)
        {
            var touched = new bool[baseCount];
            var replaceAt = new ImmutableArray<string>?[baseCount];
            var insertAt = new ImmutableArray<string>?[baseCount + 1];
            foreach (var change in changes)
            {
                var replacement = Slice(sideLines, change.SideStart, change.SideCount);
                if (change.BaseCount == 0)
                {
                    insertAt[change.BaseStart] = replacement;
                    continue;
                }

                for (var i = change.BaseStart; i < change.BaseStart + change.BaseCount; i++) touched[i] = true;
                replaceAt[change.BaseStart] = replacement;
            }

            return new(touched, replaceAt, insertAt);
        }

        /// <summary>一道缝上的插入，没插就是空。</summary>
        public ImmutableArray<string> GapSegment(int gap) => _insertAt[gap] ?? ImmutableArray<string>.Empty;

        /// <summary>
        /// base 区间 [lo, hi) 上的实际内容：逐行取出（改动过的行用替换内容）。
        /// <para>
        /// 缝只取<b>严格在区间内部</b>的那些 —— 因切段规则，落在它们上面的缝必定被某个改动区间裹着，
        /// 不能单列。区间两端的缝归相邻的缝区段，在这里再取一次会算重。
        /// </para>
        /// </summary>
        public ImmutableArray<string> LineSegment(IReadOnlyList<string> baseLines, int lo, int hi)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            for (var i = lo; i < hi; i++)
            {
                if (i > lo && _insertAt[i] is { } inserted) builder.AddRange(inserted);
                if (_replaceAt[i] is { } replacement) builder.AddRange(replacement);
                else if (!_touched[i]) builder.Add(baseLines[i]);
            }

            return builder.ToImmutable();
        }
    }

    private static ImmutableArray<string> Slice(IReadOnlyList<string> lines, int start, int count)
    {
        var builder = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++) builder.Add(lines[start + i]);
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<string> ToImmutable(IReadOnlyList<string> lines)
        => lines is ImmutableArray<string> array ? array : [.. lines];

    private static bool SequenceEqual(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    /// <summary>把行映射成整数，让差异算法只比引用相等。</summary>
    private sealed class LineInterner
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

        public int[] Intern(IReadOnlyList<string> lines)
        {
            var ids = new int[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? string.Empty;
                if (!_ids.TryGetValue(line, out var id))
                {
                    id = _ids.Count;
                    _ids.Add(line, id);
                }

                ids[i] = id;
            }

            return ids;
        }
    }
}
