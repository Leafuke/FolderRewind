using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Microsoft.UI.Xaml.Automation;
using Windows.ApplicationModel.DataTransfer;

namespace FolderRewind.Views.Settings
{
    public sealed partial class DiagnosticsControl : UserControl
    {
        public SettingsPageViewModel ViewModel { get; private set; } = null!;
        public string ConfigFilePath => ConfigService.ConfigFilePath;
        private void OnConfigActionsSizeChanged(object sender, SizeChangedEventArgs e)
        { if (sender is StackPanel panel) panel.Orientation = e.NewSize.Width < 640 ? Orientation.Vertical : Orientation.Horizontal; }
        private void OnOpenConfigFolder(object sender, RoutedEventArgs e) => ConfigService.OpenConfigFolder();
        private async void OnOpenConfigFile(object sender, RoutedEventArgs e)
        {
            try { await ConfigService.OpenConfigFileAsync(); }
            catch (Exception error) { LogService.LogError("Could not open configuration file.", nameof(DiagnosticsControl), error); await ShowSimpleMessageAsync(I18n.GetString("SettingsDiagnostics_OpenFailed")); }
        }

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

        private System.Threading.Tasks.Task ShowSimpleMessageAsync(string message) =>
            AppDialogService.Default.ShowMessageAsync(
                I18n.GetString("Common_Tip"),
                message,
                this.XamlRoot,
                I18n.GetString("Common_Close"));

        private async System.Threading.Tasks.Task ShowTextDialogAsync(string title, string content)
        {
            var tb = new TextBlock
            {
                Text = content,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetName(tb, title);
            AutomationProperties.SetAutomationId(tb, "BackupEnvironmentReportText");

            var scroll = new ScrollViewer
            {
                Content = tb,
                MaxHeight = Math.Max(96, Math.Min(560, XamlRoot.Size.Height - 220)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            AutomationProperties.SetAutomationId(scroll, "BackupEnvironmentReportScroll");

            var dialog = new ContentDialog
            {
                Title = title,
                Content = scroll,
                PrimaryButtonText = I18n.GetString("CoreValidation_CopyReport"),
                CloseButtonText = I18n.GetString("Common_Close"),
                XamlRoot = this.XamlRoot
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                var package = new DataPackage(); package.SetText(content); Clipboard.SetContent(package);
            };
            var root = XamlRoot;
            void ResizeReport(XamlRoot sender, XamlRootChangedEventArgs args)
                => scroll.MaxHeight = Math.Max(96, Math.Min(560, sender.Size.Height - 220));
            root.Changed += ResizeReport;
            try { await AppDialogService.Default.ShowCustomAsync(dialog, root); }
            finally { root.Changed -= ResizeReport; }
        }
    }
}
