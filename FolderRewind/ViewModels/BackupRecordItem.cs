using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Graph;
using FolderRewind.Services;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 一条备份记录（= 一个检查点）在页面上的展示投影，配一份算好的泳道布局。
    /// <para>
    /// 文案只取两层备注：标题是<b>运行备注</b>（那次备份／合并写下的备注），
    /// 副标题是<b>检查点级</b>备注。刻意不取版本级的名称／备注 —— 那是每个源文件夹一条，
    /// 会把一个检查点炸成好几行，正是选检查点粒度要消掉的东西。
    /// </para>
    /// </summary>
    public sealed class BackupRecordItem
    {
        public BackupRecordItem(CheckpointSummary summary, CheckpointGraphRow row, double railWidth)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(row);
            Row = row;
            RailWidth = railWidth;

            KindBadge = KindLabel(summary.CreationKind);
            HasRunComment = summary.RunComment.Length > 0;
            Title = HasRunComment ? summary.RunComment : KindBadge;
            Subtitle = summary.Comment;
            TimeDisplay = summary.CreatedAtUtc.ToLocalTime()
                .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
        }

        public CheckpointGraphRow Row { get; }

        /// <summary>泳道图宽度。全图一个值 —— 每行同宽，竖线才能在行与行之间对齐。</summary>
        public double RailWidth { get; }

        /// <summary>行高也交给布局定：行高与泳道列距是一对契约，不能一处写死一处另算。</summary>
        public double RowHeight => CheckpointGraphLayout.RowHeight;

        public string Title { get; }

        public string Subtitle { get; }

        public bool HasSubtitle => Subtitle.Length > 0;

        /// <summary>种类徽标（备份／合并／安全快照／聚合／导入）。</summary>
        public string KindBadge { get; }

        /// <summary>有运行备注时徽标才不重复：没有备注时标题本身就是种类名。</summary>
        public bool HasRunComment { get; }

        public string TimeDisplay { get; }

        private static string KindLabel(CheckpointCreationKind kind) => I18n.GetString(kind switch
        {
            CheckpointCreationKind.Merge => "BackupBranchPage_RecordKind_Merge",
            CheckpointCreationKind.SafetySnapshot => "BackupBranchPage_RecordKind_SafetySnapshot",
            CheckpointCreationKind.Aggregate => "BackupBranchPage_RecordKind_Aggregate",
            CheckpointCreationKind.Import => "BackupBranchPage_RecordKind_Import",
            _ => "BackupBranchPage_RecordKind_Capture"
        });
    }
}
