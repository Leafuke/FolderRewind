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
                if (value && (_cloud.LegacyConversionRequired || _cloud.WritableEvidence.Length == 0))
                {
                    NotificationService.ShowWarning(I18n.GetString("CloudJob_WriteVerificationRequired"));
                    OnPropertyChanged(nameof(AutoUploadEnabled)); return;
                }

                _cloud.Enabled = value;
                RaiseCloudUiProperties();
            }
        }

        public bool CloudVerifyAfterUpload
        {
            get => _cloud.VerifyAfterUpload;
            set
            {
                if (_cloud.VerifyAfterUpload == value) return;
                _cloud.VerifyAfterUpload = value;
                OnPropertyChanged(nameof(CloudVerifyAfterUpload));
            }
        }

        public bool ShowCloudUploadAdvancedSettings => AutoUploadEnabled;

        public bool CanUseManualCloudActions => CloudSyncService.CanUseManualCloudActions(_config);

        public bool IsLegacyCustomCommandMode => _cloud.CommandMode == CloudCommandMode.Custom;

        public string CloudRemoteBasePathText => CloudConnectionCatalog.RemoteRoot(ConfigService.CurrentConfig, _config);

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
