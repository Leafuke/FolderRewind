using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;

namespace FolderRewind.ViewModels
{
    /// <summary>
    /// 「所属分支」下拉的一项。首项是「全部分支」哨兵（<see cref="Branch"/> 为空），
    /// 其余项各包一个 <see cref="BackupBranchItem"/> —— 名称与徽标全部沿用分支列表那一套，不另算。
    /// <para>
    /// 选中具体分支时，5 个分支命令的可用性跟着它走；选「全部分支」等于没有选中分支，
    /// 按钮整体置灰 —— 这正是想要的效果。
    /// </para>
    /// </summary>
    public sealed class BranchFilterOption
    {
        // 文案一次进程只加载一次，取一次即可（与 BackupBranchItem 里的徽标同一手法）。
        private static readonly string AllBranchesText = I18n.GetString("BackupBranchPage_AllBranches");

        /// <summary>「全部分支」哨兵。</summary>
        public static BranchFilterOption AllBranches { get; } = new();

        private BranchFilterOption() => Name = AllBranchesText;

        public BranchFilterOption(BackupBranchItem branch)
        {
            ArgumentNullException.ThrowIfNull(branch);
            Branch = branch;
            Name = branch.Name;
        }

        /// <summary>此项对应的分支；「全部分支」为 <c>null</c>。</summary>
        public BackupBranchItem? Branch { get; }

        public BranchId? BranchId => Branch?.BranchId;

        public string Name { get; }

        public bool IsCurrent => Branch?.IsCurrent == true;

        public bool IsMultiTip => Branch?.IsMultiTip == true;

        public bool HasNameCollision => Branch?.HasNameCollision == true;

        public string CurrentBadge => Branch?.CurrentBadge ?? string.Empty;

        public string MultiTipBadge => Branch?.MultiTipBadge ?? string.Empty;

        public string NameCollisionBadge => Branch?.NameCollisionBadge ?? string.Empty;

        /// <summary>下拉项右端的创建时间。哨兵为空。</summary>
        public string CreatedDisplay => Branch?.CreatedDisplay ?? string.Empty;
    }
}
