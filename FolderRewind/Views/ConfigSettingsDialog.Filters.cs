using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace FolderRewind.Views
{
    public sealed partial class ConfigSettingsDialog : ContentDialog
    {
        private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BlacklistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.Blacklist ??= new ObservableCollection<string>();
                Config.Filters.Blacklist.Add(BlacklistBox.Text.Trim());
                BlacklistBox.Text = "";
            }
        }

        private void OnRemoveBlacklistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.Blacklist.Remove(item);
            }
        }

        private void OnAddBackupWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(BackupWhitelistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.BackupWhitelist ??= new ObservableCollection<string>();
                Config.Filters.BackupWhitelist.Add(BackupWhitelistBox.Text.Trim());
                BackupWhitelistBox.Text = "";
            }
        }

        private void OnRemoveBackupWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.BackupWhitelist.Remove(item);
            }
        }

        // --- 还原白名单 ---
        private void OnAddRestoreWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(RestoreWhitelistBox.Text))
            {
                Config.Filters ??= new FilterSettings();
                Config.Filters.RestoreWhitelist ??= new ObservableCollection<string>();
                Config.Filters.RestoreWhitelist.Add(RestoreWhitelistBox.Text.Trim());
                RestoreWhitelistBox.Text = "";
            }
        }

        private void OnRemoveRestoreWhitelistClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is string item)
            {
                Config.Filters.RestoreWhitelist.Remove(item);
            }
        }

        private void OnIconSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IconGrid.SelectedItem is string glyph && !string.IsNullOrWhiteSpace(glyph))
            {
                Config.IconGlyph = glyph;
                ConfigService.Save();
            }
        }

        // --- 自定义文件类型处理规则 ---
        private void OnAddFileTypeRuleClick(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(FileTypePatternBox.Text))
            {
                var level = (int)FileTypeLevelBox.Value;
                if (double.IsNaN(FileTypeLevelBox.Value)) level = 1;
                level = Math.Clamp(level, 0, 9);

                Config.Archive.FileTypeRules.Add(new FileTypeRule
                {
                    Pattern = FileTypePatternBox.Text.Trim(),
                    CompressionLevel = level
                });
                FileTypePatternBox.Text = "";
                FileTypeLevelBox.Value = 1;
            }
        }

        private void OnRemoveFileTypeRuleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FileTypeRule rule)
            {
                Config.Archive.FileTypeRules.Remove(rule);
            }
        }
    }
}
