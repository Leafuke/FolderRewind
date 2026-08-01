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
                if (_automationFolderOptions.Count > 0)
                {
                    var matchingOption = _automationFolderOptions.FirstOrDefault(option =>
                        string.Equals(option.Path, normalized, StringComparison.OrdinalIgnoreCase));
                    if (matchingOption == null)
                    {
                        // ItemsSource 初始化或替换时，ComboBox 可能短暂回写 null/旧值；
                        // 只有当前选项表中的真实选择才能修改配置。
                        return;
                    }

                    normalized = matchingOption.Path;
                }

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

        public IReadOnlyList<string> PerformancePresetOptions { get; } =
        [
            I18n.GetString("ConfigSettingsDialog_PerformancePreset_Auto"),
            I18n.GetString("ConfigSettingsDialog_PerformancePreset_Light"),
            I18n.GetString("ConfigSettingsDialog_PerformancePreset_VeryLight"),
            I18n.GetString("ConfigSettingsDialog_PerformancePreset_Custom")
        ];

        public int LightPerformanceThreadCount => Math.Max(1, _cpuThreadMax / 2);

        public int VeryLightPerformanceThreadCount => Math.Min(2, Math.Max(1, _cpuThreadMax));

        public int PerformancePresetSelectedIndex
        {
            get => DerivePerformancePresetIndex();
            set => ApplyPerformancePreset(value);
        }

        public string PerformancePresetDescription => I18n.GetString("ConfigSettingsDialog_PerformancePresetDesc");

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

        public string AdditionalSevenZipArgumentsText
        {
            get => _archive.AdditionalSevenZipArguments;
            set
            {
                var normalized = value ?? string.Empty;
                if (_archive.AdditionalSevenZipArguments != normalized)
                {
                    _archive.AdditionalSevenZipArguments = normalized;
                }
            }
        }

    }
}
