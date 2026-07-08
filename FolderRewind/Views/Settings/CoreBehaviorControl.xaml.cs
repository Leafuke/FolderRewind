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
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public CoreBehaviorControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private async void OnRunOnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                var desired = ts.IsOn;
                var result = await ViewModel.HandleRunOnStartupToggledAsync(desired);

                if (!result.Success && desired)
                {
                    ts.IsOn = false;

                    if (result.DisabledByUser)
                    {
                        var dialog = new ContentDialog
                        {
                            Title = I18n.GetString("Startup_DisabledByUser_Title"),
                            Content = I18n.GetString("Startup_DisabledByUser_Content"),
                            CloseButtonText = I18n.GetString("Common_Ok"),
                            XamlRoot = this.XamlRoot
                        };
                        ThemeService.ApplyThemeToDialog(dialog);
                        await dialog.ShowAsync();
                    }
                }

            }
        }

        private void OnSilentStartupToggled(object sender, RoutedEventArgs e)
        {
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
            if (sender is ComboBox cb)
            {
                ViewModel.HandleCloseBehaviorSelectionChanged(cb.SelectedIndex);
            }
        }

        private void OnNotificationsToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleNotificationsToggled(ts.IsOn);
            }
        }

        private void OnToastLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb)
            {
                ViewModel.HandleToastLevelChanged(cb.SelectedIndex);
            }
        }

        private void OnNoticesToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleNoticesToggled(ts.IsOn);
            }
        }

        private void OnUpdateReminderToggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleUpdateReminderToggled(ts.IsOn);
            }
        }

        private void OnFileSizeWarningThresholdChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
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
            ThemeService.ApplyThemeToDialog(dialog);

            var result = await dialog.ShowAsync();
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

                ViewModel.SetHotkeyOverride(hotkeyId, candidate);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ViewModel.SetHotkeyOverride(hotkeyId, string.Empty);
            }
        }

        private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var hotkeyId = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(hotkeyId)) return;

            ViewModel.ResetHotkeyOverride(hotkeyId);
        }

        private HotkeyDefinition? FindHotkeyDefinition(string id)
        {
            return ViewModel.FindHotkeyDefinition(id);
        }

        private async Task ShowSimpleMessageAsync(string message)
        {
            var dialog = new ContentDialog
            {
                Title = I18n.GetString("Common_Tip"),
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot,
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }
    }
}
