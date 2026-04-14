using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace FolderRewind.ViewModels
{
    public sealed class ConfigSettingsDialogViewModel : ViewModelBase
    {
        private readonly BackupConfig _config;
        private readonly ArchiveSettings _archive;
        private readonly AutomationSettings _automation;
        private readonly CloudSettings _cloud;
        private readonly int _cpuThreadMax;
        private readonly ObservableCollection<AutomationFolderOption> _automationFolderOptions = new();

        public ConfigSettingsDialogViewModel(BackupConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _archive = _config.Archive ??= new ArchiveSettings();
            _automation = _config.Automation ??= new AutomationSettings();
            _cloud = _config.Cloud ??= new CloudSettings();
            _cpuThreadMax = Math.Max(Environment.ProcessorCount, 1);

            _archive.PropertyChanged += OnArchivePropertyChanged;
            _automation.PropertyChanged += OnAutomationPropertyChanged;
            _config.PropertyChanged += OnConfigPropertyChanged;
            _cloud.PropertyChanged += OnCloudPropertyChanged;
            _config.SourceFolders.CollectionChanged += OnSourceFoldersCollectionChanged;

            AttachSourceFolderHandlers(_config.SourceFolders);

            NormalizeArchiveSettings();
            RefreshAutomationFolderOptions();
            RaiseCloudUiProperties();
        }

        public BackupConfig Config => _config;

        public double CompressionLevelMin => GetCompressionLevelRange(_archive.Method).Min;

        public double CompressionLevelMax => GetCompressionLevelRange(_archive.Method).Max;

        public double CpuThreadMax => _cpuThreadMax;

        public ObservableCollection<AutomationFolderOption> AutomationFolderOptions => _automationFolderOptions;

        public int AutomationScopeSelectedIndex
        {
            get => _automation.Scope == AutomationScope.SingleFolder ? 1 : 0;
            set
            {
                var newScope = value == 1 ? AutomationScope.SingleFolder : AutomationScope.AllFolders;
                if (_automation.Scope == newScope)
                {
                    return;
                }

                _automation.Scope = newScope;
                _automation.Normalize(_config.SourceFolders);
                RaiseAutomationUiProperties();
            }
        }

        public string AutomationScopeAllText => I18n.GetString("ConfigSettingsDialog_AutomationScope_AllFolders");

        public string AutomationScopeSingleText => I18n.GetString("ConfigSettingsDialog_AutomationScope_SingleFolder");

        public string AutomationScopeDescription => IsAutomationSingleFolderScope
            ? I18n.GetString("ConfigSettingsDialog_AutomationScopeSingleDesc")
            : I18n.GetString("ConfigSettingsDialog_AutomationScopeAllDesc");

        public bool IsAutomationSingleFolderScope => _automation.Scope == AutomationScope.SingleFolder;

        public bool HasAutomationFolderOptions => _automationFolderOptions.Count > 0;

        public bool CanSelectAutomationTargetFolder => IsAutomationSingleFolderScope && HasAutomationFolderOptions;

        public string AutomationTargetFolderPath
        {
            get => _automation.TargetFolderPath;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_automation.TargetFolderPath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _automation.TargetFolderPath = normalized;
                RaiseAutomationUiProperties();
            }
        }

        public string AutomationTargetFolderDescription => HasAutomationFolderOptions
            ? I18n.GetString("ConfigSettingsDialog_AutomationTargetFolderDesc")
            : I18n.GetString("ConfigSettingsDialog_AutomationTargetFolderEmpty");

        public bool ConditionalModeEnabled
        {
            get => _automation.ConditionalModeEnabled;
            set
            {
                if (_automation.ConditionalModeEnabled == value)
                {
                    return;
                }

                _automation.ConditionalModeEnabled = value;
                _automation.ConditionType = AutomationConditionType.FileUnlocked;
                RaiseAutomationUiProperties();
            }
        }

        public string ConditionRelativePathText
        {
            get => _automation.ConditionRelativePath;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_automation.ConditionRelativePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _automation.ConditionRelativePath = normalized;
                RaiseAutomationUiProperties();
            }
        }

        public bool IsConditionConfigurationValid => !ConditionalModeEnabled
            || (!string.IsNullOrWhiteSpace(ConditionRelativePathText) && !Path.IsPathRooted(ConditionRelativePathText));

        public bool ShowConditionConfigurationWarning => ConditionalModeEnabled && !IsConditionConfigurationValid;

        public string ConditionConfigurationHint
        {
            get
            {
                if (!ConditionalModeEnabled)
                {
                    return I18n.GetString("ConfigSettingsDialog_ConditionalModeDesc");
                }

                if (string.IsNullOrWhiteSpace(ConditionRelativePathText))
                {
                    return I18n.GetString("ConfigSettingsDialog_ConditionRelativePathWarning");
                }

                if (Path.IsPathRooted(ConditionRelativePathText))
                {
                    return I18n.GetString("ConfigSettingsDialog_ConditionRelativePathAbsoluteWarning");
                }

                return I18n.GetString("ConfigSettingsDialog_ConditionRelativePathDesc");
            }
        }

        public double CpuThreadsValue
        {
            get => Math.Clamp(_archive.CpuThreads, 0, _cpuThreadMax);
            set
            {
                int clamped = Math.Clamp((int)Math.Round(value), 0, _cpuThreadMax);
                if (_archive.CpuThreads != clamped)
                {
                    _archive.CpuThreads = clamped;
                }
            }
        }

        public string CpuThreadsDescription => I18n.Format("ConfigSettingsDialog_CpuThreadsDescription", _cpuThreadMax);

        public bool RunCompressionAtLowPriority
        {
            get => _archive.RunCompressionAtLowPriority;
            set => _archive.RunCompressionAtLowPriority = value;
        }

        public bool AutoUploadEnabled
        {
            get => _cloud.Enabled;
            set
            {
                if (_cloud.Enabled == value)
                {
                    return;
                }

                _cloud.Enabled = value;
                RaiseCloudUiProperties();
            }
        }

        public bool ShowCloudUploadAdvancedSettings => AutoUploadEnabled;

        public bool CanUseManualCloudActions => CloudSyncService.CanUseManualCloudActions(_config);

        public bool IsLegacyCustomCommandMode => _cloud.CommandMode == CloudCommandMode.Custom;

        public bool ShowCloudTemplateOptions => !IsLegacyCustomCommandMode;

        public string EffectiveCloudExecutablePath => CloudSyncService.GetEffectiveExecutablePath(_config);

        public string CloudExecutablePathText
        {
            get
            {
                string configured = _cloud.ExecutablePath?.Trim() ?? string.Empty;
                string global = ConfigService.CurrentConfig?.GlobalSettings?.RcloneExecutablePath?.Trim() ?? string.Empty;
                bool usesGlobalFallback = string.IsNullOrWhiteSpace(configured)
                    || string.Equals(configured, "rclone.exe", StringComparison.OrdinalIgnoreCase);
                if (usesGlobalFallback && !string.IsNullOrWhiteSpace(global))
                {
                    return global;
                }

                return string.IsNullOrWhiteSpace(configured) ? "rclone.exe" : configured;
            }
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                string global = ConfigService.CurrentConfig?.GlobalSettings?.RcloneExecutablePath?.Trim() ?? string.Empty;
                bool usesGlobalFallback = string.IsNullOrWhiteSpace(_cloud.ExecutablePath)
                    || string.Equals(_cloud.ExecutablePath, "rclone.exe", StringComparison.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(global)
                    && usesGlobalFallback
                    && string.Equals(normalized, global, StringComparison.OrdinalIgnoreCase))
                {
                    _cloud.ExecutablePath = "rclone.exe";
                }
                else
                {
                    _cloud.ExecutablePath = string.IsNullOrWhiteSpace(normalized) ? "rclone.exe" : normalized;
                }

                RaiseCloudUiProperties();
            }
        }

        public string CloudExecutableDescription => I18n.Format("ConfigSettingsDialog_CloudExecutableDesc", EffectiveCloudExecutablePath);

        public string CloudWorkingDirectoryText
        {
            get => _cloud.WorkingDirectory;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_cloud.WorkingDirectory, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _cloud.WorkingDirectory = normalized;
                RaiseCloudUiProperties();
            }
        }

        public string CloudRemoteBasePathText
        {
            get => _cloud.RemoteBasePath;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(_cloud.RemoteBasePath, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _cloud.RemoteBasePath = normalized;
                RaiseCloudUiProperties();
            }
        }

        public int CloudTemplateSelectedIndex
        {
            get => _cloud.TemplateKind switch
            {
                CloudTemplateKind.UploadBackupDirectory => 1,
                CloudTemplateKind.Custom => 2,
                _ => 0
            };
            set
            {
                var newKind = value switch
                {
                    1 => CloudTemplateKind.UploadBackupDirectory,
                    2 => CloudTemplateKind.Custom,
                    _ => CloudTemplateKind.UploadCurrentArchive
                };

                if (_cloud.TemplateKind == newKind)
                {
                    return;
                }

                _cloud.TemplateKind = newKind;
                RaiseCloudUiProperties();
            }
        }

        public string CloudArgumentsTemplateText
        {
            get => _cloud.ArgumentsTemplate;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(_cloud.ArgumentsTemplate, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _cloud.ArgumentsTemplate = normalized;
                RaiseCloudUiProperties();
            }
        }

        public int CloudTimeoutSeconds
        {
            get => _cloud.TimeoutSeconds;
            set
            {
                int normalized = Math.Clamp(value, 10, 86400);
                if (_cloud.TimeoutSeconds == normalized)
                {
                    return;
                }

                _cloud.TimeoutSeconds = normalized;
                RaiseCloudUiProperties();
            }
        }

        public int CloudRetryCount
        {
            get => _cloud.RetryCount;
            set
            {
                int normalized = Math.Clamp(value, 0, 5);
                if (_cloud.RetryCount == normalized)
                {
                    return;
                }

                _cloud.RetryCount = normalized;
                RaiseCloudUiProperties();
            }
        }

        public bool SyncHistoryAfterUpload
        {
            get => _cloud.SyncHistoryAfterUpload;
            set
            {
                if (_cloud.SyncHistoryAfterUpload == value)
                {
                    return;
                }

                _cloud.SyncHistoryAfterUpload = value;
                RaiseCloudUiProperties();
            }
        }

        public string CloudVariablesHelpText => CloudSyncService.VariablesHelpText;

        public string CloudPreviewText => CloudSyncService.BuildPreview(_config);

        public string CloudLastRunDisplay => _cloud.LastRunDisplay;

        public string CloudLastExitCodeDisplay => _cloud.LastExitCodeDisplay;

        public string CloudLastErrorMessage => _cloud.LastErrorMessage;

        public string CloudManualSyncHint => CanUseManualCloudActions
            ? I18n.GetString("ConfigSettingsDialog_CloudSyncHint")
            : I18n.GetString("ConfigSettingsDialog_CloudLegacyModeHint");

        public void ApplyCloudTemplate()
        {
            if (_cloud.TemplateKind == CloudTemplateKind.Custom || IsLegacyCustomCommandMode)
            {
                RaiseCloudUiProperties();
                return;
            }

            CloudSyncService.ApplyRecommendedTemplate(_cloud);
            RaiseCloudUiProperties();
        }

        public void RefreshCloudUi()
        {
            RaiseCloudUiProperties();
        }

        private void OnAutomationPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                _automation.Normalize(_config.SourceFolders);
                RaiseAutomationUiProperties();
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(AutomationSettings.Scope):
                case nameof(AutomationSettings.TargetFolderPath):
                case nameof(AutomationSettings.ConditionalModeEnabled):
                case nameof(AutomationSettings.ConditionType):
                case nameof(AutomationSettings.ConditionRelativePath):
                    _automation.Normalize(_config.SourceFolders);
                    RaiseAutomationUiProperties();
                    break;
            }
        }

        private void OnArchivePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                NormalizeArchiveSettings();
                RaiseArchiveUiProperties();
                RaiseCloudUiProperties();
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(ArchiveSettings.Method):
                    NormalizeCompressionLevel();
                    OnPropertyChanged(nameof(CompressionLevelMin));
                    OnPropertyChanged(nameof(CompressionLevelMax));
                    RaiseCloudUiProperties();
                    break;
                case nameof(ArchiveSettings.CpuThreads):
                    NormalizeCpuThreads();
                    OnPropertyChanged(nameof(CpuThreadsValue));
                    break;
                case nameof(ArchiveSettings.RunCompressionAtLowPriority):
                    OnPropertyChanged(nameof(RunCompressionAtLowPriority));
                    break;
                case nameof(ArchiveSettings.Mode):
                case nameof(ArchiveSettings.Format):
                    RaiseCloudUiProperties();
                    break;
            }
        }

        private void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(BackupConfig.Name):
                case nameof(BackupConfig.DestinationPath):
                    RaiseCloudUiProperties();
                    break;
            }
        }

        private void OnSourceFoldersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var folder in e.OldItems.OfType<ManagedFolder>())
                {
                    folder.PropertyChanged -= OnSourceFolderPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (var folder in e.NewItems.OfType<ManagedFolder>())
                {
                    folder.PropertyChanged += OnSourceFolderPropertyChanged;
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                AttachSourceFolderHandlers(_config.SourceFolders);
            }

            RefreshAutomationFolderOptions();
        }

        private void OnSourceFolderPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName) ||
                e.PropertyName == nameof(ManagedFolder.Path) ||
                e.PropertyName == nameof(ManagedFolder.DisplayName))
            {
                RefreshAutomationFolderOptions();
            }
        }

        private void OnCloudPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                RaiseCloudUiProperties();
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(CloudSettings.Enabled):
                case nameof(CloudSettings.TemplateKind):
                case nameof(CloudSettings.ExecutablePath):
                case nameof(CloudSettings.ArgumentsTemplate):
                case nameof(CloudSettings.WorkingDirectory):
                case nameof(CloudSettings.TimeoutSeconds):
                case nameof(CloudSettings.RetryCount):
                case nameof(CloudSettings.RemoteBasePath):
                case nameof(CloudSettings.LastRunUtc):
                case nameof(CloudSettings.LastExitCode):
                case nameof(CloudSettings.LastErrorMessage):
                case nameof(CloudSettings.CommandMode):
                    RaiseCloudUiProperties();
                    break;
            }
        }

        private void NormalizeArchiveSettings()
        {
            NormalizeCompressionLevel();
            NormalizeCpuThreads();
            RaiseArchiveUiProperties();
        }

        private void RaiseArchiveUiProperties()
        {
            OnPropertyChanged(nameof(CompressionLevelMin));
            OnPropertyChanged(nameof(CompressionLevelMax));
            OnPropertyChanged(nameof(CpuThreadMax));
            OnPropertyChanged(nameof(CpuThreadsValue));
            OnPropertyChanged(nameof(CpuThreadsDescription));
            OnPropertyChanged(nameof(RunCompressionAtLowPriority));
        }

        private void RaiseAutomationUiProperties()
        {
            OnPropertyChanged(nameof(AutomationFolderOptions));
            OnPropertyChanged(nameof(AutomationScopeSelectedIndex));
            OnPropertyChanged(nameof(AutomationScopeAllText));
            OnPropertyChanged(nameof(AutomationScopeSingleText));
            OnPropertyChanged(nameof(AutomationScopeDescription));
            OnPropertyChanged(nameof(IsAutomationSingleFolderScope));
            OnPropertyChanged(nameof(HasAutomationFolderOptions));
            OnPropertyChanged(nameof(CanSelectAutomationTargetFolder));
            OnPropertyChanged(nameof(AutomationTargetFolderPath));
            OnPropertyChanged(nameof(AutomationTargetFolderDescription));
            OnPropertyChanged(nameof(ConditionalModeEnabled));
            OnPropertyChanged(nameof(ConditionRelativePathText));
            OnPropertyChanged(nameof(IsConditionConfigurationValid));
            OnPropertyChanged(nameof(ShowConditionConfigurationWarning));
            OnPropertyChanged(nameof(ConditionConfigurationHint));
        }

        private void RaiseCloudUiProperties()
        {
            OnPropertyChanged(nameof(AutoUploadEnabled));
            OnPropertyChanged(nameof(ShowCloudUploadAdvancedSettings));
            OnPropertyChanged(nameof(CanUseManualCloudActions));
            OnPropertyChanged(nameof(IsLegacyCustomCommandMode));
            OnPropertyChanged(nameof(ShowCloudTemplateOptions));
            OnPropertyChanged(nameof(EffectiveCloudExecutablePath));
            OnPropertyChanged(nameof(CloudExecutablePathText));
            OnPropertyChanged(nameof(CloudExecutableDescription));
            OnPropertyChanged(nameof(CloudWorkingDirectoryText));
            OnPropertyChanged(nameof(CloudRemoteBasePathText));
            OnPropertyChanged(nameof(CloudTemplateSelectedIndex));
            OnPropertyChanged(nameof(CloudArgumentsTemplateText));
            OnPropertyChanged(nameof(CloudTimeoutSeconds));
            OnPropertyChanged(nameof(CloudRetryCount));
            OnPropertyChanged(nameof(SyncHistoryAfterUpload));
            OnPropertyChanged(nameof(CloudVariablesHelpText));
            OnPropertyChanged(nameof(CloudPreviewText));
            OnPropertyChanged(nameof(CloudLastRunDisplay));
            OnPropertyChanged(nameof(CloudLastExitCodeDisplay));
            OnPropertyChanged(nameof(CloudLastErrorMessage));
            OnPropertyChanged(nameof(CloudManualSyncHint));
        }

        private void NormalizeCompressionLevel()
        {
            var (min, max) = GetCompressionLevelRange(_archive.Method);
            int clamped = Math.Clamp(_archive.CompressionLevel, min, max);
            if (_archive.CompressionLevel != clamped)
            {
                _archive.CompressionLevel = clamped;
            }
        }

        private void NormalizeCpuThreads()
        {
            int clamped = Math.Clamp(_archive.CpuThreads, 0, _cpuThreadMax);
            if (_archive.CpuThreads != clamped)
            {
                _archive.CpuThreads = clamped;
            }
        }

        private void RefreshAutomationFolderOptions()
        {
            _automation.Normalize(_config.SourceFolders);

            _automationFolderOptions.Clear();
            foreach (var folder in _config.SourceFolders.Where(folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path)))
            {
                _automationFolderOptions.Add(new AutomationFolderOption
                {
                    Path = folder.Path,
                    DisplayName = BuildAutomationFolderDisplayName(folder)
                });
            }

            RaiseAutomationUiProperties();
        }

        private void AttachSourceFolderHandlers(IEnumerable<ManagedFolder> folders)
        {
            foreach (var folder in folders.Where(folder => folder != null))
            {
                folder.PropertyChanged -= OnSourceFolderPropertyChanged;
                folder.PropertyChanged += OnSourceFolderPropertyChanged;
            }
        }

        private static string BuildAutomationFolderDisplayName(ManagedFolder folder)
        {
            string path = folder.Path ?? string.Empty;
            string displayName = folder.DisplayName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return path;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return displayName;
            }

            string leafName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(displayName, leafName, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }

            return $"{displayName} ({path})";
        }

        private static (int Min, int Max) GetCompressionLevelRange(string? method)
        {
            return method switch
            {
                "zstd" => (1, 22),
                "BZip2" => (1, 9),
                "LZMA2" => (0, 9),
                "Deflate" => (0, 9),
                _ => (0, 9),
            };
        }
    }

    public sealed class AutomationFolderOption
    {
        public string Path { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
    }
}
