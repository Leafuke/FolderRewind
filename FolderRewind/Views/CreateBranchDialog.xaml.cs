using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Views
{
    /// <summary>
    /// 从某次备份创建分支：选一个已有备份，再给它起个名字。
    /// <para>
    /// 候选集由调用方筛好（只有检查点结构完整的备份才在列表里）；
    /// 这里只做「选没选、名字空不空」两件输入层的事，业务规则仍在分支服务里。
    /// </para>
    /// </summary>
    public sealed partial class CreateBranchDialog : ContentDialog
    {
        public CreateBranchDialog(IEnumerable<BackupRunItem> runs)
        {
            InitializeComponent();

            Title = I18n.GetString("BackupBranchPage_CreateDialog_Title");
            PrimaryButtonText = I18n.GetString("Common_Ok");
            CloseButtonText = I18n.GetString("Common_Cancel");
            DefaultButton = ContentDialogButton.Primary;
            ThemeService.ApplyThemeToDialog(this);

            var items = runs?.ToList() ?? [];
            RunList.ItemsSource = items;
            if (items.Count > 0)
            {
                RunList.SelectedIndex = 0;
            }

            var hasCandidates = items.Count > 0;
            RunList.Visibility = hasCandidates ? Visibility.Visible : Visibility.Collapsed;
            NoBackupHint.Visibility = hasCandidates ? Visibility.Collapsed : Visibility.Visible;

            UpdatePrimaryButtonState();
        }

        public BackupRunItem? SelectedRun => RunList.SelectedItem as BackupRunItem;

        public string BranchName => BranchNameBox.Text?.Trim() ?? string.Empty;

        private void OnRunSelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdatePrimaryButtonState();

        private void OnNameChanged(object sender, TextChangedEventArgs e)
            => UpdatePrimaryButtonState();

        private void UpdatePrimaryButtonState()
            => IsPrimaryButtonEnabled = SelectedRun is not null && BranchName.Length > 0;
    }
}
