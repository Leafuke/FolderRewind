using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FolderRewind.Models
{

    /// <summary>
    /// 备份过滤模式。
    /// </summary>
    public enum BackupFilterMode
    {
        Blacklist = 0,
        Whitelist = 1
    }

    /// <summary>
    /// 过滤器设置
    /// </summary>
    public class FilterSettings : ObservableObject
    {
        private BackupFilterMode _backupFilterMode = BackupFilterMode.Blacklist;
        // 这里的黑名单是相对于 Config 的，应用于所有 SourceFolder
        private ObservableCollection<string> _blacklist = new();
        private ObservableCollection<string> _backupWhitelist = new();
        public ObservableCollection<string> Blacklist
        {
            get => _blacklist;
            set => SetProperty(ref _blacklist, value ?? new ObservableCollection<string>());
        }

        /// <summary>
        /// 备份过滤模式。默认黑名单，保证旧配置继续按“排除规则”工作。
        /// </summary>
        public BackupFilterMode BackupFilterMode
        {
            get => _backupFilterMode;
            set => SetProperty(ref _backupFilterMode, value);
        }

        /// <summary>
        /// 备份白名单：启用白名单模式时，仅备份匹配这些规则的文件。
        /// </summary>
        public ObservableCollection<string> BackupWhitelist
        {
            get => _backupWhitelist;
            set => SetProperty(ref _backupWhitelist, value ?? new ObservableCollection<string>());
        }

        public bool UseRegex { get; set; } = false;

        /// <summary>
        /// 还原白名单：Clean 还原时不会清除的文件/文件夹
        /// </summary>
        private ObservableCollection<string> _restoreWhitelist = new();
        public ObservableCollection<string> RestoreWhitelist
        {
            get => _restoreWhitelist;
            set => SetProperty(ref _restoreWhitelist, value ?? new ObservableCollection<string>());
        }
    }
}
