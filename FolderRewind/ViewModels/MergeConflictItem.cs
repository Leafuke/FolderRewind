using CommunityToolkit.Mvvm.ComponentModel;
using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.History.Merge;
using FolderRewind.Services;
using System;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 一条冲突在列表里的展示投影。
    /// <para>
    /// 是<b>可变</b>的：解决一条冲突后窗口只重新读取状态而不重建列表 —— 重建会把选中项与滚动位置一起丢掉，
    /// 用户刚处理完的那一条就会从眼前消失。
    /// </para>
    /// </summary>
    public sealed class MergeConflictItem : ObservableObject
    {
        private static readonly string ModifyModifyText = I18n.GetString("Merge_Kind_ModifyModify");
        private static readonly string ModifyDeleteText = I18n.GetString("Merge_Kind_ModifyDelete");
        private static readonly string AddAddText = I18n.GetString("Merge_Kind_AddAdd");
        private static readonly string PathStructureText = I18n.GetString("Merge_Kind_PathStructure");
        private static readonly string SourceRosterText = I18n.GetString("Merge_Kind_SourceRoster");
        private static readonly string SourceBoundaryText = I18n.GetString("Merge_Kind_SourceBoundary");

        public MergeConflictItem(MergeConflictView view)
        {
            ArgumentNullException.ThrowIfNull(view);
            View = view;
        }

        /// <summary>平台给出的冲突状态。解决之后由 <see cref="Update"/> 换成新的一份。</summary>
        public MergeConflictView View { get; private set; }

        public string ConflictId => View.ConflictId;

        public bool IsResolved => View.IsResolved;

        /// <summary>能逐块处理的冲突才有三方内容可看；别的只能整份取一边。</summary>
        public bool CanEditPerHunk => View.CanEditPerHunk;

        /// <summary>
        /// 列表里那一行怎么称呼这条冲突：单文件冲突显示路径，多文件冲突显示「首个路径 等 N 个文件」，
        /// 来源层面冲突没有路径，退回来源名 —— 三种都出现过，只认路径会让一部分条目整行空白。
        /// </summary>
        public string Title => View.Paths.Length switch
        {
            0 => View.SourceName,
            1 => View.Paths[0],
            _ => I18n.Format("Merge_ConflictPathsMore", View.Paths[0], View.Paths.Length)
        };

        public string KindText => View.Kind switch
        {
            MergeConflictKind.ModifyModify => ModifyModifyText,
            MergeConflictKind.ModifyDelete => ModifyDeleteText,
            MergeConflictKind.AddAdd => AddAddText,
            MergeConflictKind.PathStructure => PathStructureText,
            MergeConflictKind.SourceRoster => SourceRosterText,
            _ => SourceBoundaryText
        };

        /// <summary>副标题：这条冲突属于哪个来源、涉及多少文件。</summary>
        public string Subtitle => View.Paths.Length > 1
            ? I18n.Format("Merge_ConflictSubtitleMany", View.SourceName, View.Paths.Length)
            : View.SourceName;

        /// <summary>
        /// 解决状态。<see cref="MergeResolutionChoice.Manual"/> 只说「手动」不说具体内容 ——
        /// 库里的解决结果是一整份文件，没有「取了哪几块」这回事，说得细反而是编的。
        /// </summary>
        public string StateText => View.Choice switch
        {
            MergeResolutionChoice.Ours => I18n.GetString("Merge_State_Ours"),
            MergeResolutionChoice.Theirs => I18n.GetString("Merge_State_Theirs"),
            MergeResolutionChoice.Manual => I18n.GetString("Merge_State_Manual"),
            _ => I18n.GetString("Merge_State_Unresolved")
        };

        /// <summary>用最新状态刷新本条目，并通知界面重画这一行。</summary>
        public void Update(MergeConflictView view)
        {
            ArgumentNullException.ThrowIfNull(view);
            if (view.ConflictId != View.ConflictId)
            {
                throw new InvalidOperationException("A conflict item cannot change its identity.");
            }

            View = view;
            OnPropertyChanged(nameof(View));
            OnPropertyChanged(nameof(IsResolved));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(KindText));
            OnPropertyChanged(nameof(CanEditPerHunk));
        }
    }
}
