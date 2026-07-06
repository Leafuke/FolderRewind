using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FolderRewind.Views.Settings
{
    public sealed partial class DiagnosticsControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;

        public DiagnosticsControl()
        {
            this.InitializeComponent();
        }

        public void SetViewModel(SettingsPageViewModel viewModel)
        {
            ViewModel = viewModel;
            Bindings.Update();
        }

        private async void OnRunCoreValidationClick(object sender, RoutedEventArgs e)
        {
            if (CoreFeatureValidationService.IsRunning)
            {
                await ShowSimpleMessageAsync(I18n.GetString("CoreValidation_AlreadyRunning"));
                return;
            }

            var report = await CoreFeatureValidationService.RunValidationAsync(false);
            OnCoreValidationStateChanged();
            await ShowTextDialogAsync(I18n.GetString("CoreValidation_Report_Title"), report.ToDisplayText());
        }

        private async void OnViewCoreValidationReportClick(object sender, RoutedEventArgs e)
        {
            var report = CoreFeatureValidationService.LastReport;
            if (report == null)
            {
                await ShowSimpleMessageAsync(I18n.GetString("CoreValidation_Report_NoData"));
                return;
            }

            await ShowTextDialogAsync(I18n.GetString("CoreValidation_Report_Title"), report.ToDisplayText());
        }

        private void OnCoreValidationStateChanged()
        {
            ViewModel.RefreshCoreValidationState();
        }

        private void OnLoggingChanged(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts)
            {
                ViewModel.HandleLoggingChanged(ts.IsOn);
            }
        }

        private void OnLogSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            ViewModel.HandleLogSizeChanged(e.NewValue);
        }

        private void OnRetentionChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
        {
            ViewModel.HandleRetentionChanged(e.NewValue);
        }

        private void OnOpenLogCenterClick(object sender, RoutedEventArgs e)
        {
            _ = NavigationService.NavigateTo("Logs");
        }

        private async System.Threading.Tasks.Task ShowSimpleMessageAsync(string message)
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

        private async System.Threading.Tasks.Task ShowTextDialogAsync(string title, string content)
        {
            var tb = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 320
            };

            var scroll = new ScrollViewer
            {
                Content = tb,
                MaxHeight = 560
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = scroll,
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot
            };
            ThemeService.ApplyThemeToDialog(dialog);
            await dialog.ShowAsync();
        }
    }
}
