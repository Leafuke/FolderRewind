using FolderRewind.Services;
using FolderRewind.Services.Hotkeys;
using FolderRewind.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace FolderRewind.Views.Settings
{
    public sealed partial class CoreBehaviorControl : UserControl
    {
        private readonly SettingsPresentationLifetime _presentation = new();
        internal DeferredSettingsSection BehaviorSection { get; } = new();
        internal DeferredSettingsSection NotificationsSection { get; } = new();
        internal DeferredSettingsSection HotkeysSection { get; } = new();

        private void UpdatePresentationBindings() => _presentation.UpdateBindings(Bindings.Update);

        internal void DeactivatePresentation()
        {
            _presentation.Deactivate();
            Bindings.StopTracking();
        }

        private void EnsureSection(DeferredSettingsSection section, SettingsSection kind)
        {
            if (!_presentation.IsActive) return;
            System.Threading.Tasks.Task? initialization = null;
            _presentation.UpdateBindings(() =>
            {
                initialization = ViewModel.InitializeSectionAsync(kind);
                section.Create();
                Bindings.Update();
                ApplyDeferredCardStyles();
            });
            TaskObserver.Observe(initialization ?? System.Threading.Tasks.Task.CompletedTask, nameof(CoreBehaviorControl));
        }


        private void ApplyDeferredCardStyles()
        {
            SettingsDeferredContent.ApplyCardStyles(HotkeysBody, HotkeysExpander);
            SettingsDeferredContent.ApplyCardStyles(NotificationsBody, NotificationsExpander);
            SettingsDeferredContent.ApplyCardStyles(BehaviorBody, BehaviorExpander);
        }

        private void RefreshCreatedSections()
        {
            if (BehaviorSection.IsCreated) EnsureSection(BehaviorSection, SettingsSection.CoreBehavior);
            if (NotificationsSection.IsCreated) EnsureSection(NotificationsSection, SettingsSection.Notifications);
            if (HotkeysSection.IsCreated) EnsureSection(HotkeysSection, SettingsSection.Hotkeys);
        }

        private void OnBehaviorExpanded(object? sender, object e) => EnsureSection(BehaviorSection, SettingsSection.CoreBehavior);

        private void OnNotificationsExpanded(object? sender, object e) => EnsureSection(NotificationsSection, SettingsSection.Notifications);

        private void OnHotkeysExpanded(object? sender, object e) => EnsureSection(HotkeysSection, SettingsSection.Hotkeys);

        public SettingsPageViewModel ViewModel { get; private set; } = null!;
        private bool _handlingStartupToggle;

        public CoreBehaviorControl()
        {
            this.InitializeComponent();
            Unloaded += (_, _) => DeactivatePresentation();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            _presentation.Activate();
            UpdatePresentationBindings();
            RefreshCreatedSections();
        }

        private async void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            var generation = _presentation.Generation;
            if (sender is ToggleSwitch ts)
            {
                var desired = ts.IsOn;
                if (_handlingStartupToggle || ViewModel == null || desired == ViewModel.RunOnStartup) return;
                _handlingStartupToggle = true;
                try
                {
                    var result = await ViewModel.HandleRunOnStartupToggledAsync(desired);
                    if (!_presentation.IsCurrent(generation)) return;
                    if (!result.Success && desired)
                    {
                        ts.IsOn = false;
                        if (result.DisabledByUser)
                        {
                            await AppDialogService.Default.ShowMessageAsync(
                                I18n.GetString("Startup_DisabledByUser_Title"),
                                I18n.GetString("Startup_DisabledByUser_Content"),
                                this.XamlRoot);
                            await Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));
                        }
                    }
                }
                catch (Exception error) { NotificationService.ShowError(I18n.Format("Startup_SetFailed", error.Message)); }
                finally { _handlingStartupToggle = false; }

            }
        }

        private void OnCreateDesktopShortcutClick(object sender, RoutedEventArgs e)
        {
            if (DesktopShortcutService.TryCreateDesktopShortcut(out var errorMessage))
            {
                NotificationService.ShowSuccess(I18n.GetString("DesktopShortcut_CreateSucceeded"));
            }
            else
            {
                NotificationService.ShowError(
                    I18n.Format("DesktopShortcut_CreateFailed", errorMessage));
            }
        }

        private void OnSilentStartupToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ts.IsOn = ViewModel.HandleSilentStartupToggled(ts.IsOn);
            }
            else if (!ViewModel.Settings.RunOnStartup)
            {
                ViewModel.HandleSilentStartupToggled(false);
            }
        }

        private void OnCloseBehaviorSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleCloseBehaviorSelectionChanged(cb.SelectedIndex);
            }
        }

        private void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleNotificationsToggled(ts.IsOn);
            }
        }

        private void OnToastLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ComboBox cb)
            {
                ViewModel.HandleToastLevelChanged(cb.SelectedIndex);
            }
        }

        private void OnNoticesToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleNoticesToggled(ts.IsOn);
            }
        }

        private void OnUpdateReminderToggled(object sender, RoutedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleUpdateReminderToggled(ts.IsOn);
            }
        }

        private void OnFileSizeWarningThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            if (!_presentation.CanEdit) return;
            ViewModel.HandleFileSizeWarningThresholdChanged(e.NewValue);
        }

        private async void OnEditHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            var def = FindHotkeyDefinition(hotkeyId);
            if (def == null) return;

            var captureBox = new TextBox
            {
                IsReadOnly = true,
                PlaceholderText = I18n.GetString("Hotkeys_CapturePlaceholder"),
                Text = HotkeyManager.GetEffectiveGestureString(hotkeyId),
                MinWidth = 260,
            };

            HotkeyGesture? captured = null;

            captureBox.KeyDown += (_, args) =>
            {
                try
                {
                    var key = args.Key;
                    if (key == VirtualKey.Control || key == VirtualKey.Shift || key == VirtualKey.Menu || key == VirtualKey.LeftWindows || key == VirtualKey.RightWindows)
                    {
                        args.Handled = true;
                        return;
                    }

                    var mods = HotkeyModifiers.None;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Ctrl;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Alt;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Shift;
                    if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0
                        || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                        mods |= HotkeyModifiers.Win;

                    captured = new HotkeyGesture(mods, key);
                    captureBox.Text = captured.Value.ToString();
                    args.Handled = true;
                }
                catch
                {
                }
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = def.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(def.Description))
                panel.Children.Add(new TextBlock { Text = def.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = I18n.GetString("Hotkeys_CaptureHint"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(captureBox);

            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Hotkeys_EditDialogTitle"),
                Content = panel,
                PrimaryButtonText = I18n.GetString("Common_Save"),
                SecondaryButtonText = I18n.GetString("Hotkeys_Disable"),
                CloseButtonText = I18n.GetString("Common_Cancel"),
                XamlRoot = this.XamlRoot,
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await AppDialogService.Default.ShowCustomAsync(dialog, this.XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                if (captured == null)
                {
                    await ShowSimpleMessageAsync(I18n.GetString("Hotkeys_NoCapture"));
                    return;
                }

                var candidate = captured.Value.ToString();
                var defs = HotkeyManager.GetDefinitionsSnapshot();
                foreach (var other in defs)
                {
                    if (string.Equals(other.Id, def.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    if (other.Scope != def.Scope) continue;

                    var otherGesture = HotkeyManager.GetEffectiveGestureString(other.Id);
                    if (string.Equals(otherGesture, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        await ShowSimpleMessageAsync(I18n.Format("Hotkeys_ConflictDialog", candidate, other.DisplayName));
                        return;
                    }
                }

                await ViewModel.SetHotkeyOverrideAsync(hotkeyId, candidate);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ViewModel.SetHotkeyOverrideAsync(hotkeyId, string.Empty);
            }
        }

        private async void OnResetHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            await ViewModel.ResetHotkeyOverrideAsync(hotkeyId);
        }

        private HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return ViewModel.FindHotkeyDefinition(id);
        }

        private Task ShowSimpleMessageAsync(string message) =>
            AppDialogService.Default.ShowMessageAsync(
                I18n.GetString("Common_Tip"),
                message,
                this.XamlRoot,
                I18n.GetString("Common_Close"));
    }
}
