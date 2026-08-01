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
                    RaisePerformancePresetProperties();
                    break;
                case nameof(ArchiveSettings.RunCompressionAtLowPriority):
                    OnPropertyChanged(nameof(RunCompressionAtLowPriority));
                    RaisePerformancePresetProperties();
                    break;
                case nameof(ArchiveSettings.AdditionalSevenZipArguments):
                    OnPropertyChanged(nameof(AdditionalSevenZipArgumentsText));
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
            RaisePerformancePresetProperties();
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

        private int DerivePerformancePresetIndex()
        {
            if (_archive.CpuThreads == 0 && !_archive.RunCompressionAtLowPriority)
            {
                return PerformancePresetAutoIndex;
            }

            if (!_archive.RunCompressionAtLowPriority)
            {
                return PerformancePresetCustomIndex;
            }

            bool matchesLight = _archive.CpuThreads == LightPerformanceThreadCount;
            bool matchesVeryLight = _archive.CpuThreads == VeryLightPerformanceThreadCount;
            if (matchesLight && matchesVeryLight)
            {
                return _lastAppliedPerformancePresetIndex is PerformancePresetLightIndex or PerformancePresetVeryLightIndex
                    ? _lastAppliedPerformancePresetIndex
                    : PerformancePresetLightIndex;
            }

            if (matchesLight)
            {
                return PerformancePresetLightIndex;
            }

            if (matchesVeryLight)
            {
                return PerformancePresetVeryLightIndex;
            }

            return PerformancePresetCustomIndex;
        }

        private void ApplyPerformancePreset(int value)
        {
            switch (value)
            {
                case PerformancePresetAutoIndex:
                    _lastAppliedPerformancePresetIndex = PerformancePresetAutoIndex;
                    _archive.CpuThreads = 0;
                    _archive.RunCompressionAtLowPriority = false;
                    break;
                case PerformancePresetLightIndex:
                    _lastAppliedPerformancePresetIndex = PerformancePresetLightIndex;
                    _archive.CpuThreads = LightPerformanceThreadCount;
                    _archive.RunCompressionAtLowPriority = true;
                    break;
                case PerformancePresetVeryLightIndex:
                    _lastAppliedPerformancePresetIndex = PerformancePresetVeryLightIndex;
                    _archive.CpuThreads = VeryLightPerformanceThreadCount;
                    _archive.RunCompressionAtLowPriority = true;
                    break;
                default:
                    _lastAppliedPerformancePresetIndex = PerformancePresetCustomIndex;
                    break;
            }

            RaisePerformancePresetProperties();
        }

        private void RaisePerformancePresetProperties()
        {
            OnPropertyChanged(nameof(PerformancePresetSelectedIndex));
            OnPropertyChanged(nameof(PerformancePresetDescription));
        }

        private void RaisePageVisibilityProperties()
        {
            OnPropertyChanged(nameof(SelectedPageIndex));
            OnPropertyChanged(nameof(IsGeneralPageVisible));
            OnPropertyChanged(nameof(IsBackupStrategyPageVisible));
            OnPropertyChanged(nameof(IsRestoreStrategyPageVisible));
            OnPropertyChanged(nameof(IsAutomationPageVisible));
            OnPropertyChanged(nameof(IsCloudPageVisible));
            OnPropertyChanged(nameof(IsFilterPageVisible));
        }

        private void RaiseFilterUiProperties()
        {
            OnPropertyChanged(nameof(BackupFilterModeSelectedIndex));
            OnPropertyChanged(nameof(IsBackupBlacklistMode));
            OnPropertyChanged(nameof(IsBackupWhitelistMode));
            OnPropertyChanged(nameof(BackupWhitelistCleanWarningText));
        }

        private BackupScopeOption? SelectedBackupScopeOption
        {
            get
            {
                var scopeId = _config.BackupScope?.PluginScopeId ?? string.Empty;
                return _backupScopeOptions.FirstOrDefault(option =>
                           string.Equals(option.Id, scopeId, StringComparison.OrdinalIgnoreCase))
                       ?? _backupScopeOptions.FirstOrDefault();
            }
        }

        private void EnsureScopeParameterDefaults(BackupScopeOption? option)
        {
            if (option?.Definition?.Parameters == null)
            {
                return;
            }

            _config.BackupScope ??= new BackupScopeSettings();
            _config.BackupScope.Parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in option.Definition.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Key))
                {
                    continue;
                }

                if (!_config.BackupScope.Parameters.ContainsKey(parameter.Key))
                {
                    _config.BackupScope.Parameters[parameter.Key] = parameter.DefaultValue ?? string.Empty;
                }
            }
        }

        private void RaiseBackupScopeUiProperties()
        {
            OnPropertyChanged(nameof(BackupScopeOptions));
            OnPropertyChanged(nameof(BackupScopeSelectedIndex));
            OnPropertyChanged(nameof(HasPluginBackupScopeOptions));
            OnPropertyChanged(nameof(IsPluginBackupScopeSelected));
            OnPropertyChanged(nameof(BackupScopeDescription));
            OnPropertyChanged(nameof(SelectedBackupScopeParameters));
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
            var (min, max) = ArchiveCompressionPolicy.GetLevelRange(_archive.Method);
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

            var options = new List<AutomationFolderOption>();
            foreach (var folder in _config.SourceFolders.Where(folder => folder != null && !string.IsNullOrWhiteSpace(folder.Path)))
            {
                options.Add(new AutomationFolderOption
                {
                    Path = folder.Path,
                    DisplayName = BuildAutomationFolderDisplayName(folder)
                });
            }

            // 一次性替换完整选项表，避免 WinUI ComboBox 在 Clear/Add 的中间状态
            // 将临时选中的首项通过 TwoWay SelectedValue 反向写入自动化配置。
            _automationFolderOptions = options;
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

    }
}
