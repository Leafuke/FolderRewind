using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace FolderRewind.ViewModels
{
    public sealed partial class ConfigSettingsDialogViewModel : ViewModelBase
    {
        private BackupConfig _config;
        private ArchiveSettings _archive;
        private AutomationSettings _automation;
        private FilterSettings _filters;
        private CloudSettings _cloud;
        private readonly int _cpuThreadMax;
        private List<AutomationFolderOption> _automationFolderOptions = new();
        private List<BackupScopeOption> _backupScopeOptions = new();
        private int _selectedPageIndex;
        private int _lastAppliedPerformancePresetIndex = 3;

        private const int MinPageIndex = 0;
        private const int MaxPageIndex = 5;
        private const int PerformancePresetAutoIndex = 0;
        private const int PerformancePresetLightIndex = 1;
        private const int PerformancePresetVeryLightIndex = 2;
        private const int PerformancePresetCustomIndex = 3;

        public ConfigSettingsDialogViewModel(BackupConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _archive = _config.Archive ??= new ArchiveSettings();
            _automation = _config.Automation ??= new AutomationSettings();
            _filters = _config.Filters ??= new FilterSettings();
            _cloud = _config.Cloud ??= new CloudSettings();
            _config.BackupScope ??= new BackupScopeSettings();
            _cpuThreadMax = Math.Max(Environment.ProcessorCount, 1);

            _archive.PropertyChanged += OnArchivePropertyChanged;
            _automation.PropertyChanged += OnAutomationPropertyChanged;
            _filters.PropertyChanged += OnFilterPropertyChanged;
            _config.PropertyChanged += OnConfigPropertyChanged;
            _cloud.PropertyChanged += OnCloudPropertyChanged;
            _config.SourceFolders.CollectionChanged += OnSourceFoldersCollectionChanged;

            AttachSourceFolderHandlers(_config.SourceFolders);

            NormalizeArchiveSettings();
            RefreshBackupScopeOptions();
            RefreshAutomationFolderOptions();
            RaiseCloudUiProperties();
        }

        public void Unbind()
        {
            if (_config == null)
            {
                return;
            }

            _archive.PropertyChanged -= OnArchivePropertyChanged;
            _automation.PropertyChanged -= OnAutomationPropertyChanged;
            _filters.PropertyChanged -= OnFilterPropertyChanged;
            _config.PropertyChanged -= OnConfigPropertyChanged;
            _cloud.PropertyChanged -= OnCloudPropertyChanged;
            _config.SourceFolders.CollectionChanged -= OnSourceFoldersCollectionChanged;

            foreach (var folder in _config.SourceFolders)
            {
                if (folder != null)
                {
                    folder.PropertyChanged -= OnSourceFolderPropertyChanged;
                }
            }
        }

        public void Rebind(BackupConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _archive = _config.Archive ??= new ArchiveSettings();
            _automation = _config.Automation ??= new AutomationSettings();
            _filters = _config.Filters ??= new FilterSettings();
            _cloud = _config.Cloud ??= new CloudSettings();
            _config.BackupScope ??= new BackupScopeSettings();

            _archive.PropertyChanged += OnArchivePropertyChanged;
            _automation.PropertyChanged += OnAutomationPropertyChanged;
            _filters.PropertyChanged += OnFilterPropertyChanged;
            _config.PropertyChanged += OnConfigPropertyChanged;
            _cloud.PropertyChanged += OnCloudPropertyChanged;
            _config.SourceFolders.CollectionChanged += OnSourceFoldersCollectionChanged;

            AttachSourceFolderHandlers(_config.SourceFolders);

            _selectedPageIndex = 0;

            NormalizeArchiveSettings();
            RefreshBackupScopeOptions();
            RefreshAutomationFolderOptions();
            RaiseCloudUiProperties();
        }

        public BackupConfig Config => _config;

        public int SelectedPageIndex
        {
            get => _selectedPageIndex;
            set
            {
                int normalized = Math.Clamp(value, MinPageIndex, MaxPageIndex);
                if (!SetProperty(ref _selectedPageIndex, normalized))
                {
                    return;
                }

                RaisePageVisibilityProperties();
            }
        }

        public bool IsGeneralPageVisible => SelectedPageIndex == 0;

        public bool IsBackupStrategyPageVisible => SelectedPageIndex == 1;

        public bool IsRestoreStrategyPageVisible => SelectedPageIndex == 2;

        public bool IsAutomationPageVisible => SelectedPageIndex == 3;

        public bool IsCloudPageVisible => SelectedPageIndex == 4;

        public bool IsFilterPageVisible => SelectedPageIndex == 5;

        public int BackupFilterModeSelectedIndex
        {
            get => _config.Filters?.BackupFilterMode == BackupFilterMode.Whitelist ? 1 : 0;
            set
            {
                _config.Filters ??= new FilterSettings();
                var mode = value == 1 ? BackupFilterMode.Whitelist : BackupFilterMode.Blacklist;
                if (_config.Filters.BackupFilterMode == mode)
                {
                    return;
                }

                _config.Filters.BackupFilterMode = mode;
                RaiseFilterUiProperties();
            }
        }

        public bool IsBackupBlacklistMode => _config.Filters?.BackupFilterMode != BackupFilterMode.Whitelist;

        public bool IsBackupWhitelistMode => _config.Filters?.BackupFilterMode == BackupFilterMode.Whitelist;

        public string BackupWhitelistCleanWarningText => I18n.GetString("ConfigSettingsDialog_BackupWhitelistCleanWarning");

        public IReadOnlyList<BackupScopeOption> BackupScopeOptions => _backupScopeOptions;

        public int BackupScopeSelectedIndex
        {
            get
            {
                if (_backupScopeOptions.Count == 0)
                {
                    // 设置页初始化时会先清空再重建选项；空集合只能对应“未选择”。
                    return -1;
                }

                var scopeId = _config.BackupScope?.ScopeId ?? string.Empty;
                var index = _backupScopeOptions
                    .Select((option, i) => (option, i))
                    .FirstOrDefault(pair => string.Equals(pair.option.Id, scopeId, StringComparison.OrdinalIgnoreCase)).i;
                return index >= 0 ? index : 0;
            }
            set
            {
                SelectBackupScopeByIndex(value);
            }
        }

        public bool SelectBackupScopeByIndex(int value)
        {
            if (_backupScopeOptions.Count == 0)
            {
                // ComboBox 初始化 ItemsSource 时会出现 -1；空集合阶段不能反向污染配置。
                return false;
            }

            if (value < 0 || value >= _backupScopeOptions.Count)
            {
                return false;
            }

            _config.BackupScope ??= new BackupScopeSettings();
            var option = _backupScopeOptions[value];
            if (string.Equals(_config.BackupScope.ScopeId, option.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _config.BackupScope.OwnerId = option.Definition?.OwnerId ?? string.Empty;
            _config.BackupScope.ScopeId = option.Id;
            EnsureScopeParameterDefaults(option);
            RaiseBackupScopeUiProperties();
            return true;
        }

        public bool HasPluginBackupScopeOptions => _backupScopeOptions.Count > 1;

        public bool IsPluginBackupScopeSelected => SelectedBackupScopeOption?.Definition != null;

        public string BackupScopeDescription => SelectedBackupScopeOption?.Description ?? string.Empty;

        public IReadOnlyList<PluginFormFieldDefinition> SelectedBackupScopeParameters =>
            SelectedBackupScopeOption?.Definition?.Parameters ?? Array.Empty<PluginFormFieldDefinition>();

        public double CompressionLevelMin => ArchiveCompressionPolicy.GetLevelRange(_archive.Method).Min;

        public double CompressionLevelMax => ArchiveCompressionPolicy.GetLevelRange(_archive.Method).Max;

        public double CpuThreadMax => _cpuThreadMax;

        public IReadOnlyList<AutomationFolderOption> AutomationFolderOptions => _automationFolderOptions;

        public string GetBackupScopeParameterValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            _config.BackupScope ??= new BackupScopeSettings();
            _config.BackupScope.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return _config.BackupScope.Parameters.TryGetValue(key, out var value) ? value : string.Empty;
        }

        public void SetBackupScopeParameterValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            _config.BackupScope ??= new BackupScopeSettings();
            _config.BackupScope.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _config.BackupScope.Parameters[key] = value ?? string.Empty;
        }

        public void RefreshBackupScopeOptions()
        {
            var options = new List<BackupScopeOption>
            {
                new(
                string.Empty,
                I18n.GetString("ConfigSettingsDialog_BackupScopeFull"),
                I18n.GetString("ConfigSettingsDialog_BackupScopeFullDesc"),
                null)
            };

            foreach (var definition in PluginService.GetBackupScopeDefinitions(_config))
            {
                options.Add(new BackupScopeOption(
                    definition.Id,
                    string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Id : definition.DisplayName,
                    definition.Description ?? string.Empty,
                    definition));
            }

            var selectedScopeId = _config.BackupScope?.ScopeId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(selectedScopeId)
                && !options.Any(option => string.Equals(option.Id, selectedScopeId, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new BackupScopeOption(
                    selectedScopeId,
                    selectedScopeId,
                    I18n.GetString("ConfigSettingsDialog_BackupScopeMissingDesc"),
                    null));
            }

            // 这里使用整表替换而不是 ObservableCollection.Clear/Add。
            // WinUI ComboBox 在初始化绑定时会响应 ItemsSource 变化；集合变更重入会导致 ObjectModel.InvalidOperationException。
            _backupScopeOptions = options;
            EnsureScopeParameterDefaults(SelectedBackupScopeOption);
            RaiseBackupScopeUiProperties();
        }

    }
}
