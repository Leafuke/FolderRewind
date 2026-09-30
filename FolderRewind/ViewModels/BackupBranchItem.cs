using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Globalization;
using System.Linq;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 分支在页面上的展示投影。分支本身是不可变事实，这里只做读取与格式化，不承载状态。
    /// <para>
    /// 「能否重命名 / 删除 / 切换」一律直接取平台给出的判定，不在页面重算规则 ——
    /// 页面自己判一套必然与命令里的校验漂移，最后变成按钮亮着但点了报错。
    /// </para>
    /// </summary>
    public sealed class BackupBranchItem
    {
        // 徽标文案在每个条目上都要用，但语言一次进程只加载一次，取一次即可。
        private static readonly string CurrentBadgeText = I18n.GetString("BackupBranchPage_CurrentBadge");
        private static readonly string MultiTipBadgeText = I18n.GetString("BackupBranchPage_MultiTip");
        private static readonly string NameCollisionBadgeText = I18n.GetString("BackupBranchPage_NameCollisionBadge");

        public BackupBranchItem(BranchSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            Summary = summary;
            Name = summary.Name;

            // 端点是按创建时间升序给出的，最后一个就是这条分支最近一次变化。
            var tip = summary.Tips.LastOrDefault();
            TipUpdateId = tip?.UpdateId;
            var createdAtUtc = tip?.CreatedAtUtc ?? DateTimeOffset.MinValue;
            CreatedDisplay = createdAtUtc == DateTimeOffset.MinValue
                ? string.Empty
                : createdAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.CurrentCulture);
        }

        public BranchSummary Summary { get; }

        public BranchId BranchId => Summary.BranchId;

        public string Name { get; }

        public bool IsCurrent => Summary.IsActive;

        public bool IsMultiTip => Summary.IsMultiTip;

        public bool HasNameCollision => Summary.HasNameCollision;

        /// <summary>分支端点所在的那次提交。切换分支切到的就是它。</summary>
        public BranchUpdateId? TipUpdateId { get; }

        public string CreatedDisplay { get; }

        public bool CanRename => Summary.CanRename;

        public bool CanDelete => Summary.CanDelete;

        /// <summary>已经站在这个分支上时不需要再切换。</summary>
        public bool CanCheckout => Summary.HasCheckoutTarget && !Summary.IsActive;

        public string CurrentBadge => CurrentBadgeText;

        public string MultiTipBadge => MultiTipBadgeText;

        public string NameCollisionBadge => NameCollisionBadgeText;
    }
}
