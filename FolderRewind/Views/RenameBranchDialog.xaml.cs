using FolderRewind.Services;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Views
{
    /// <summary>
    /// 重命名分支。名称沿用平台规则（非空、设备内不重名），这里只保证用户至少改了点东西 ——
    /// 名称没变时确认按钮不可用，免得往只增不改的历史里追加一条无意义的改名记录。
    /// </summary>
    public sealed partial class RenameBranchDialog : ContentDialog
    {
        private readonly string _originalName;

        public RenameBranchDialog(string currentName)
        {
            InitializeComponent();

            _originalName = currentName ?? string.Empty;
            Title = I18n.GetString("BackupBranchPage_RenameDialog_Title");
            PrimaryButtonText = I18n.GetString("Common_Ok");
            CloseButtonText = I18n.GetString("Common_Cancel");
            DefaultButton = ContentDialogButton.Primary;
            ThemeService.ApplyThemeToDialog(this);

            BranchNameBox.Text = _originalName;
            BranchNameBox.SelectAll();

            UpdatePrimaryButtonState();
        }

        public string BranchName => BranchNameBox.Text?.Trim() ?? string.Empty;

        private void OnNameChanged(object sender, TextChangedEventArgs e)
            => UpdatePrimaryButtonState();

        private void UpdatePrimaryButtonState()
            => IsPrimaryButtonEnabled = BranchName.Length > 0
                && !string.Equals(BranchName, _originalName, StringComparison.Ordinal);
    }
}
