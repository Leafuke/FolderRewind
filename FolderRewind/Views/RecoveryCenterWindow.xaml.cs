using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class RecoveryCenterWindow : Window
{
    public RecoveryCenterWindow()
    {
        InitializeComponent();
        ApplyLocalizedText();
        LoadDiagnostic();
        RefreshRecoveryCopies();
    }

    private void ApplyLocalizedText()
    {
        TitleText.Text = I18n.GetString("RecoveryCenter_Title");
        RestrictedText.Text = I18n.GetString("RecoveryCenter_Restricted");
        DiagnosticHeaderText.Text = I18n.GetString("RecoveryCenter_DiagnosticHeader");
        ConfigPathHeaderText.Text = I18n.GetString("RecoveryCenter_ConfigPathHeader");
        OpenFolderButton.Content = I18n.GetString("RecoveryCenter_OpenFolder");
        RecoveryCopiesHeaderText.Text = I18n.GetString("RecoveryCenter_RecoveryCopiesHeader");
        RecoveryCopiesDescriptionText.Text = I18n.GetString("RecoveryCenter_RecoveryCopiesDescription");
        RestoreCopyButton.Content = I18n.GetString("RecoveryCenter_RestoreCopy");
        RefreshCopiesButton.Content = I18n.GetString("RecoveryCenter_RefreshCopies");
        RetryButton.Content = I18n.GetString("RecoveryCenter_Retry");
        ExportButton.Content = I18n.GetString("RecoveryCenter_ExportOriginal");
        ResetButton.Content = I18n.GetString("RecoveryCenter_Reset");
        CloseButton.Content = I18n.GetString("Common_Close");
    }

    private void LoadDiagnostic()
    {
        var diagnostic = ConfigService.RecoveryDiagnostic;
        ConfigPathText.Text = diagnostic?.ConfigPath ?? ConfigService.ConfigFilePath;
        DiagnosticText.Text = I18n.Format(
            "RecoveryCenter_DiagnosticFormat",
            diagnostic?.Code ?? "config_recovery_required",
            diagnostic?.Message ?? I18n.GetString("RecoveryCenter_UnknownDiagnostic"));
    }

    private void RefreshRecoveryCopies()
    {
        var selected = RecoveryCopyComboBox.SelectedItem as string;
        var copies = ConfigService.GetRecoveryCopies();
        RecoveryCopyComboBox.ItemsSource = copies;
        RecoveryCopyComboBox.SelectedItem = copies.FirstOrDefault(path =>
            string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)) ?? copies.FirstOrDefault();
        RestoreCopyButton.IsEnabled = copies.Count > 0;
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        => ConfigService.OpenConfigFolder();

    private void OnRefreshCopiesClick(object sender, RoutedEventArgs e)
        => RefreshRecoveryCopies();

    private void OnRetryClick(object sender, RoutedEventArgs e)
        => CompleteRecoveryAction(ConfigService.RetryRecovery());

    private void OnRestoreCopyClick(object sender, RoutedEventArgs e)
    {
        if (RecoveryCopyComboBox.SelectedItem is not string selected) return;
        CompleteRecoveryAction(ConfigService.RestoreRecoveryCopy(selected));
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickSaveFilePathAsync(
            I18n.GetString("RecoveryCenter_ExportPickerTitle"),
            "RecoveryCenterExport",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [I18n.GetString("RecoveryCenter_JsonFiles")] = new[] { ".json" }
            },
            FormattableString.Invariant($"folderrewind-config-recovery-{DateTime.Now:yyyyMMdd-HHmmss}.json"));
        if (string.IsNullOrWhiteSpace(path)) return;

        ShowStatus(
            ConfigService.ExportRecoverySource(path),
            "RecoveryCenter_ExportSuccess",
            "RecoveryCenter_ExportFailed");
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync(
                "RecoveryCenter_ResetConfirmTitle",
                "RecoveryCenter_ResetConfirmContent",
                "RecoveryCenter_ResetConfirmButton"))
        {
            return;
        }

        if (!await ConfirmAsync(
                "RecoveryCenter_ResetFinalTitle",
                "RecoveryCenter_ResetFinalContent",
                "RecoveryCenter_ResetFinalButton"))
        {
            return;
        }

        CompleteRecoveryAction(ConfigService.ResetFromRecovery());
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmAsync(string titleKey, string contentKey, string buttonKey)
    {
        return await AppDialogService.Default.ConfirmAsync(
            I18n.GetString(titleKey),
            I18n.GetString(contentKey),
            I18n.GetString(buttonKey),
            RootGrid.XamlRoot,
            isDestructive: true);
    }

    private void CompleteRecoveryAction(bool success)
    {
        if (success)
        {
            RetryButton.IsEnabled = false;
            RestoreCopyButton.IsEnabled = false;
            ResetButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            ShowStatus(true, "RecoveryCenter_ActionSuccessRestart", "RecoveryCenter_ActionFailed");
        }
        else
        {
            LoadDiagnostic();
            RefreshRecoveryCopies();
            ShowStatus(false, "RecoveryCenter_ActionSuccessRestart", "RecoveryCenter_ActionFailed");
        }
    }

    private void ShowStatus(bool success, string successKey, string failureKey)
    {
        StatusInfoBar.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        StatusInfoBar.Message = I18n.GetString(success ? successKey : failureKey);
        StatusInfoBar.IsOpen = true;
    }
}
