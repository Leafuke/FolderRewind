using FolderRewind.Models;
using FolderRewind.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace FolderRewind.Services
{
    internal static class TemplateSubmissionWorkflowService
    {
        public static async Task RunAsync(XamlRoot? xamlRoot, CancellationToken ct = default)
        {
            if (!TemplateService.GetTemplates().Any())
            {
                NotificationService.ShowWarning(I18n.GetString("Settings_Template_Export_NoTemplates"));
                return;
            }

            var dialog = new TemplateSubmissionDialog();
            var dialogResult = await TemplateDialogCoordinatorService.ShowAsync(dialog, xamlRoot, ct);
            if (dialogResult == ContentDialogResult.None
                || dialog.SelectedTemplate == null
                || dialog.RequestedAction == TemplateSubmissionDialogAction.None)
            {
                return;
            }

            switch (dialog.RequestedAction)
            {
                case TemplateSubmissionDialogAction.ExportPackage:
                    await ExportTemplateSubmissionPackageAsync(dialog.SelectedTemplate, dialog.SelectedGameName, xamlRoot, ct);
                    break;
                case TemplateSubmissionDialogAction.SubmitToGitHub:
                    await SubmitOfficialTemplateAsync(dialog.SelectedTemplate, dialog.SelectedGameName, xamlRoot, ct);
                    break;
            }
        }

        private static void ApplySubmissionMetadata(ConfigTemplate template, string gameName, bool clearSteamAppId)
        {
            template.GameName = gameName?.Trim() ?? string.Empty;
            if (clearSteamAppId)
            {
                template.SteamAppId = null;
            }

            ConfigService.Save();
        }

        private static async Task ExportTemplateSubmissionPackageAsync(
            ConfigTemplate selected,
            string gameName,
            XamlRoot? xamlRoot,
            CancellationToken ct)
        {
            ApplySubmissionMetadata(selected, gameName, clearSteamAppId: true);

            var validation = TemplateService.ValidateTemplateForOfficialSharing(selected);
            if (!validation.Success)
            {
                var validationMessage = validation.Errors.Count > 0 ? string.Join(Environment.NewLine, validation.Errors) : validation.Message;
                LogService.LogWarning(validationMessage, nameof(TemplateSubmissionWorkflowService));
                NotificationService.ShowError(validationMessage);
                return;
            }

            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("FolderRewind Template", new List<string> { TemplateService.ShareFileExtension });
            picker.SuggestedFileName = $"FolderRewind_submission_{SanitizeFileName(selected.Name)}";
            MainWindowService.InitializePicker(picker);

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var ok = TemplateService.ExportTemplateSubmissionPackage(selected.Id, file.Path, out var summary, out var message);
            if (!ok)
            {
                LogService.LogWarning(message, nameof(TemplateSubmissionWorkflowService));
                NotificationService.ShowError(message);
                return;
            }

            var package = new DataPackage();
            package.SetText(summary);
            Clipboard.SetContent(package);

            NotificationService.ShowSuccess(message);

            var successDialog = new ContentDialog
            {
                Title = I18n.GetString("Template_Submission_SummaryTitle"),
                Content = new TextBox
                {
                    Text = summary,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    IsReadOnly = true,
                    MinHeight = 240
                },
                CloseButtonText = I18n.GetString("Common_Ok")
            };

            await TemplateDialogCoordinatorService.ShowAsync(successDialog, xamlRoot, ct);
        }

        private static async Task SubmitOfficialTemplateAsync(
            ConfigTemplate selected,
            string gameName,
            XamlRoot? xamlRoot,
            CancellationToken ct)
        {
            ApplySubmissionMetadata(selected, gameName, clearSteamAppId: false);

            var authState = await GitHubOAuthService.GetAuthenticationStateAsync(true, ct);
            if (!authState.IsAuthenticated)
            {
                var authResult = await GitHubOAuthService.SignInAsync(xamlRoot, ct);
                LogService.Log(authResult.Message, authResult.Success ? LogLevel.Info : LogLevel.Warning);
                if (!authResult.Success)
                {
                    NotificationService.ShowError(authResult.Message);
                    return;
                }

                NotificationService.ShowSuccess(authResult.Message);
            }

            var statusText = new TextBlock
            {
                Text = I18n.GetString("GitHubSubmit_Progress_Starting"),
                TextWrapping = TextWrapping.Wrap
            };

            var progressDialog = new ContentDialog
            {
                Title = I18n.GetString("TemplateSubmissionDialog_SubmitToGitHub"),
                Content = new StackPanel
                {
                    Spacing = 12,
                    MinWidth = 420,
                    Children =
                    {
                        new ProgressRing { IsActive = true, Width = 48, Height = 48 },
                        statusText
                    }
                },
                CloseButtonText = I18n.GetString("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            GitHubTemplateSubmissionService.SubmissionResult? submitResult = null;
            Exception? submitException = null;
            var completedByWorkflow = false;
            var progress = new Progress<string>(message => statusText.Text = message);

            var submitTask = Task.Run(async () =>
            {
                try
                {
                    submitResult = await GitHubTemplateSubmissionService.SubmitTemplateAsync(selected, progress, cts.Token);
                }
                catch (Exception ex)
                {
                    submitException = ex;
                    LogService.LogError(I18n.Format("GitHubSubmit_FailedWithReason", ex.Message), nameof(TemplateSubmissionWorkflowService), ex);
                }
                finally
                {
                    completedByWorkflow = true;
                    await TemplateDialogCoordinatorService.HideAsync(progressDialog);
                }
            }, CancellationToken.None);

            var progressResult = await TemplateDialogCoordinatorService.ShowAsync(progressDialog, xamlRoot, ct);
            var canceledByUser = progressResult == ContentDialogResult.None && !completedByWorkflow;
            if (canceledByUser)
            {
                cts.Cancel();
            }

            await submitTask;
            if (canceledByUser)
            {
                NotificationService.ShowWarning(I18n.GetString("GitHubSubmit_Canceled"));
                return;
            }

            if (submitException != null)
            {
                NotificationService.ShowError(I18n.Format("GitHubSubmit_FailedWithReason", submitException.Message));
                return;
            }

            if (submitResult == null)
            {
                NotificationService.ShowError(I18n.GetString("GitHubSubmit_UnknownFailure"));
                return;
            }

            if (!submitResult.Success)
            {
                LogService.LogWarning(submitResult.Message, nameof(TemplateSubmissionWorkflowService));
                NotificationService.ShowError(submitResult.Message);
                return;
            }

            NotificationService.ShowSuccess(submitResult.Message);

            if (string.IsNullOrWhiteSpace(submitResult.PullRequestUrl))
            {
                return;
            }

            var resultDialog = new ContentDialog
            {
                Title = I18n.GetString("GitHubSubmit_ResultTitle"),
                Content = new TextBox
                {
                    Text = I18n.Format("GitHubSubmit_ResultContent", submitResult.ShareCode, submitResult.PullRequestUrl),
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    IsReadOnly = true,
                    MinHeight = 180
                },
                PrimaryButtonText = I18n.GetString("GitHubSubmit_OpenPullRequest"),
                CloseButtonText = I18n.GetString("Common_Ok")
            };

            var result = await TemplateDialogCoordinatorService.ShowAsync(resultDialog, xamlRoot, ct);
            if (result == ContentDialogResult.Primary)
            {
                _ = Launcher.LaunchUriAsync(new Uri(submitResult.PullRequestUrl));
            }
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "template";
            }

            var sanitized = name;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "template" : sanitized;
        }
    }
}
