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

        public void OpenCloudGuideWebsite()
        {
            if (ShellPathService.TryOpenPath(OfficialLinksService.GetOfficialWebsiteUrl(), out var errorMessage))
            {
                return;
            }

            string errorDetail = string.IsNullOrWhiteSpace(errorMessage)
                ? I18n.GetString("Common_Failed")
                : errorMessage;
            string message = I18n.Format("ConfigSettingsDialog_CloudGuideOpenFailed", errorDetail);

            LogService.LogError(message, nameof(ConfigSettingsDialogViewModel));
            NotificationService.ShowError(message, I18n.GetString("CloudSync_Notification_Title"));
        }

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

        public bool TryValidateAndNormalizeAdditionalSevenZipArguments(out string errorMessage)
        {
            var result = SevenZipAdditionalArguments.Validate(_archive.AdditionalSevenZipArguments);
            if (!result.IsValid)
            {
                errorMessage = result.ErrorMessage ?? I18n.GetString("ConfigSettingsDialog_Additional7zArgsInvalid");
                return false;
            }

            _archive.AdditionalSevenZipArguments = result.Arguments;
            OnPropertyChanged(nameof(AdditionalSevenZipArgumentsText));
            errorMessage = string.Empty;
            return true;
        }

        public bool TryValidateFilters(out string errorMessage)
        {
            return BackupService.TryValidateFilterRules(_config.Filters, out errorMessage);
        }

        public bool TryValidateBackupScope(out string errorMessage)
        {
            var result = PluginService.ValidateBackupScope(_config);
            errorMessage = result.ErrorMessage;
            return result.Success;
        }

    }
}
