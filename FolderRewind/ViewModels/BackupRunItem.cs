using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Globalization;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 「从某次备份创建分支」的候选条目。
    /// <para>
    /// 候选范围直接取平台给出的 <see cref="RunSummary.IsBranchableCheckpoint"/>：
    /// 它表示这次备份的结果检查点结构完整，够得着一条配置级分支。
    /// 页面不自己判断哪个备份「可以建分支」。
    /// </para>
    /// </summary>
    public sealed class BackupRunItem
    {
        public BackupRunItem(RunSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            if (summary.ResultCheckpointId is not { } checkpointId)
            {
                // 候选集已经筛过一遍，走到这里说明筛选条件与这里的前提不一致，是调用方的错误。
                throw new ArgumentException(
                    "A branchable backup run must have a result checkpoint.",
                    nameof(summary));
            }

            Summary = summary;
            CheckpointId = checkpointId;
            CompletedDisplay = summary.CompletedAtUtc.ToLocalTime()
                .ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
            Comment = summary.Comment;
            SourcesDisplay = I18n.Format("BackupBranchPage_RunSources", summary.Sources.Length);
        }

        public RunSummary Summary { get; }

        public CheckpointId CheckpointId { get; }

        public string CompletedDisplay { get; }

        /// <summary>这次备份的备注；没有备注时为空，由页面决定是否占位。</summary>
        public string Comment { get; }

        public string SourcesDisplay { get; }
    }
}
