using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using System;
using System.Collections.Immutable;

namespace FolderRewind.ViewModels
{
    /// <summary>一行带行号的文本。行号是这一侧文件里的真实行号（1 基），供人对照。</summary>
    public sealed record MergeLineItem(int Number, string Text);

    /// <summary>
    /// 一个区段的展示项：三栏（本地 / 结果 / 对方）各自的行，冲突块额外带一组选择。
    /// <para>
    /// <b>三栏为什么不需要同步滚动</b>：这里的每个区段本身就是一行（三栏同处一个 Grid 行），
    /// 区段按顺序堆在一个滚动容器里。竖直方向的位置因此是<b>布局</b>算出来的，不是三份滚动条互相对齐出来的 ——
    /// 互驱滚动要防重入、要处理惯性滚动，还总会在快速拖动时抖一下。
    /// </para>
    /// <para>
    /// 代价是三栏只能做到<b>区段级</b>对齐：同一个区段内两侧行数不同时，这一块的下边缘对齐而块内各行不逐行对齐。
    /// 这正是 IDEA 的做法，也是此处唯一可行的做法 —— 逐行对齐需要三侧行数在每一段都相等，而合并的全部意义就是它们不相等。
    /// </para>
    /// </summary>
    public sealed class MergeHunkItem : ObservableObject
    {
        private static readonly string UnchangedText = I18n.GetString("Merge_Hunk_Unchanged");
        private static readonly string OursOnlyText = I18n.GetString("Merge_Hunk_OursOnly");
        private static readonly string TheirsOnlyText = I18n.GetString("Merge_Hunk_TheirsOnly");
        private static readonly string BothSameText = I18n.GetString("Merge_Hunk_BothSame");
        private static readonly string ConflictText = I18n.GetString("Merge_Hunk_Conflict");
        private static readonly string ExpandText = I18n.GetString("Merge_Hunk_Expand");
        private static readonly string CollapseText = I18n.GetString("Merge_Hunk_Collapse");

        private LineMergeChoice? _choice;
        private bool _canChoose;
        private bool _showBase;
        private bool _isExpanded;

        public MergeHunkItem(int index, LineMergeHunk hunk, int collapseThreshold)
        {
            ArgumentNullException.ThrowIfNull(hunk);
            Index = index;
            Hunk = hunk;
            OurLines = Number(hunk.OursStart, hunk.OursLines);
            TheirLines = Number(hunk.TheirsStart, hunk.TheirsLines);
            BaseLines = Number(hunk.BaseStart, hunk.BaseLines);

            // 未变化的长段落默认折叠：大文件里没动过的地方可能上万行，逐行摆出来窗口会卡住，
            // 而它在合并里没有任何可看的信息。展开只是展示，不影响结果。
            LongUnchanged = hunk.Kind == LineMergeKind.Unchanged
                && hunk.BaseCount > collapseThreshold
                && hunk.OursCount == hunk.BaseCount
                && hunk.TheirsCount == hunk.BaseCount;
            _isExpanded = !LongUnchanged;
        }

        public int Index { get; }

        public LineMergeHunk Hunk { get; }

        /// <summary>
        /// 这一块属于哪个文件。只在「已自动合并」预览里非空 —— 那份清单会把好几个文件的区段铺在同一个流里，
        /// 没有这一行就分不清哪几块是哪个文件的。逐冲突视图里一次只有一个文件，不需要重复说。
        /// <para>
        /// 只挂在每个文件的第一块上：每块都挂一遍，同一份文件名字会重复十几遍。
        /// </para>
        /// </summary>
        public string FileHeader { get; init; } = string.Empty;

        public bool HasFileHeader => FileHeader.Length > 0;

        public bool IsConflict => Hunk.Kind == LineMergeKind.Conflict;

        public ImmutableArray<MergeLineItem> OurLines { get; }

        public ImmutableArray<MergeLineItem> TheirLines { get; }

        public ImmutableArray<MergeLineItem> BaseLines { get; }

        /// <summary>折叠钮只给「没动过又很长」的段落，别的段落没有可折叠的东西。</summary>
        public bool LongUnchanged { get; }

        /// <summary>展开为假时这一块只显示一行「未变化的 N 行」。</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            private set
            {
                if (SetProperty(ref _isExpanded, value))
                {
                    OnPropertyChanged(nameof(IsCollapsed));
                    OnPropertyChanged(nameof(ToggleText));
                }
            }
        }

        public bool IsCollapsed => !_isExpanded;

        public string ToggleText => _isExpanded ? CollapseText : ExpandText;

        public string KindText => Hunk.Kind switch
        {
            LineMergeKind.Unchanged => UnchangedText,
            LineMergeKind.OursOnly => OursOnlyText,
            LineMergeKind.TheirsOnly => TheirsOnlyText,
            LineMergeKind.BothSame => BothSameText,
            _ => ConflictText
        };

        /// <summary>折叠行上的说明文字。</summary>
        public string CollapsedText => Hunk.BaseCount == Hunk.OursCount && Hunk.BaseCount == Hunk.TheirsCount
            ? I18n.Format("Merge_Hunk_CollapsedSame", Hunk.BaseCount)
            : I18n.Format("Merge_Hunk_CollapsedChanged", Hunk.BaseCount, Hunk.OursCount, Hunk.TheirsCount);

        /// <summary>
        /// 人对这一块的处置。<c>null</c> 表示还没选 —— 这一格是给界面看的，
        /// 拼结果时未选的冲突块按「取本地」预览，但<b>不允许提交</b>（见 <c>MergeWindowViewModel.CanSubmitManual</c>）：
        /// 悄悄替用户选一边，正是冲突处理里最不能犯的错。
        /// </summary>
        public LineMergeChoice? Choice
        {
            get => _choice;
            set
            {
                if (!SetProperty(ref _choice, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(ChoiceText));
                OnPropertyChanged(nameof(ResultLines));
            }
        }

        /// <summary>这一块还没被选择。只有冲突块会为真。</summary>
        public bool IsPending => IsConflict && _choice is null;

        public string ChoiceText => _choice switch
        {
            LineMergeChoice.Ours => I18n.GetString("Merge_Choice_Ours"),
            LineMergeChoice.Theirs => I18n.GetString("Merge_Choice_Theirs"),
            LineMergeChoice.Both => I18n.GetString("Merge_Choice_Both"),
            _ => I18n.GetString("Merge_Choice_Pending")
        };

        /// <summary>结果栏里这一块会长成什么样。非冲突块的结果是确定的，与选择无关。</summary>
        public ImmutableArray<MergeLineItem> ResultLines => Number(0, ResolvedLines());

        /// <summary>「共同祖先」栏是否显示。由窗口顶部的开关统一推动。</summary>
        public bool ShowBase
        {
            get => _showBase;
            set => SetProperty(ref _showBase, value);
        }

        /// <summary>选择钮是否可用：不能逐块编辑、正在忙、已经手改过结果时为假（手改后再按选择会把手改内容抹掉）。</summary>
        public bool CanChoose
        {
            get => _canChoose;
            set => SetProperty(ref _canChoose, value);
        }

        public void ToggleExpanded() => IsExpanded = !IsExpanded;

        /// <summary>取出这一块的最终行。未选择时按「取本地」预览。</summary>
        public ImmutableArray<string> ResolvedLines()
            => LineMergeAlgorithm.Resolve(Hunk, _choice ?? LineMergeChoice.Ours);

        /// <summary>
        /// 结果栏的行号从 1 起按块内位置排 —— 结果文件此刻还没有行号可言，
        /// 显示它在<b>这一块里</b>的第几行，比编一个整份文件的行号诚实。
        /// </summary>
        private static ImmutableArray<MergeLineItem> Number(int start, ImmutableArray<string> lines)
        {
            var builder = ImmutableArray.CreateBuilder<MergeLineItem>(lines.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                builder.Add(new(start + i + 1, lines[i]));
            }

            return builder.MoveToImmutable();
        }
    }
}
