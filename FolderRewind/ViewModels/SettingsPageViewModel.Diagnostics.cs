using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.Services.Plugins;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace FolderRewind.ViewModels
{
    public sealed partial class SettingsPageViewModel : ViewModelBase, IDisposable
    {
        public void RefreshCoreValidationState()
        {
            OnPropertyChanged(nameof(IsCoreValidationRunning));
            OnPropertyChanged(nameof(IsCoreValidationIdle));
            OnPropertyChanged(nameof(HasCoreValidationReport));
            OnPropertyChanged(nameof(CoreValidationStatusText));
            OnPropertyChanged(nameof(CoreValidationLastRunText));
            OnPropertyChanged(nameof(CoreValidationLastSummaryText));
        }

        public void RefreshSponsorState()
        {
            OnPropertyChanged(nameof(IsSponsorUnlocked));
            OnPropertyChanged(nameof(IsSponsorLocked));
            OnPropertyChanged(nameof(SponsorStatusText));
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(SponsorBackgroundImageOpacityPercent));
            OnPropertyChanged(nameof(SponsorBackgroundOverlayOpacityPercent));
            ClearSponsorBackgroundCommand.NotifyCanExecuteChanged();
            ClearCustomCompletionSoundCommand.NotifyCanExecuteChanged();
        }

        public void RefreshHotkeyBindingsView()
        {
            HotkeyBindingsView.Clear();

            var defs = HotkeyManager.GetDefinitionsSnapshot();
            var overrides = Settings?.Hotkeys?.Bindings ?? new Dictionary<string, string>();

            // 构建纯展示模型，避免页面直接依赖 HotkeyDefinition 内部结构。
            foreach (var def in defs)
            {
                var effective = HotkeyManager.GetEffectiveGestureString(def.Id);
                var hasOverride = overrides.ContainsKey(def.Id);

                var scopeText = def.Scope == HotkeyScope.GlobalHotkey
                    ? I18n.GetString("Hotkeys_Scope_Global")
                    : I18n.GetString("Hotkeys_Scope_Shortcut");

                var ownerText = string.IsNullOrWhiteSpace(def.OwnerPluginId)
                    ? I18n.GetString("Hotkeys_Owner_Core")
                    : I18n.Format("Hotkeys_Owner_Plugin", def.OwnerPluginName ?? def.OwnerPluginId);

                HotkeyBindingsView.Add(new HotkeyBindingItem
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    Description = def.Description,
                    ScopeText = scopeText,
                    OwnerText = ownerText,
                    CurrentGesture = string.IsNullOrWhiteSpace(effective) ? I18n.GetString("Hotkeys_Unbound") : effective,
                    DefaultGesture = I18n.Format("Hotkeys_Default", string.IsNullOrWhiteSpace(def.DefaultGesture) ? I18n.GetString("Hotkeys_Unbound") : def.DefaultGesture),
                    IsOverridden = hasOverride,
                });
            }
        }

        public HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return HotkeyManager.GetDefinitionsSnapshot().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public void SetHotkeyOverride(string hotkeyId, string gesture)
        {
            HotkeyManager.SetGestureOverride(hotkeyId, gesture);
            RefreshHotkeyBindingsView();
        }

        public void ResetHotkeyOverride(string hotkeyId)
        {
            HotkeyManager.ResetGestureOverride(hotkeyId);
            RefreshHotkeyBindingsView();
        }

        public void UpdateKnotLinkStatus()
        {
            if (!Settings.EnableKnotLink)
            {
                KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Disabled");
                KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Gray);
                return;
            }

            if (KnotLinkService.IsInitialized)
            {
                var responserOk = KnotLinkService.IsResponserRunning;
                var senderOk = KnotLinkService.IsSenderRunning;

                if (responserOk && senderOk)
                {
                    KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_Connected");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
                }
                else if (responserOk || senderOk)
                {
                    KnotLinkStatusMessage = I18n.Format(
                        "SettingsPage_KnotLinkStatus_Partial",
                        responserOk ? "✓" : "✗",
                        senderOk ? "✓" : "✗");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                }
                else
                {
                    KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_InitFailed");
                    KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                }
            }
            else
            {
                KnotLinkStatusMessage = I18n.GetString("SettingsPage_KnotLinkStatus_NotInitialized");
                KnotLinkStatusColor = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            }
        }


        private void HotkeyManager_DefinitionsChanged(object? sender, EventArgs e)
        {
            EnqueueOnUiThread(RefreshHotkeyBindingsView);
        }

        private void CoreFeatureValidationService_StateChanged()
        {
            EnqueueOnUiThread(RefreshCoreValidationState);
        }

        private void SponsorService_StateChanged()
        {
            EnqueueOnUiThread(RefreshSponsorState);
        }
    }
}
