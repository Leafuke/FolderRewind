using FolderRewind.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class OfficialTemplateImportService
    {
        internal sealed class ImportOfficialTemplateResult
        {
            public bool Success { get; init; }
            public bool Canceled { get; init; }
            public string Message { get; init; } = string.Empty;
            public ConfigTemplate? ImportedTemplate { get; init; }
            public RemoteTemplateIndexItem? IndexItem { get; init; }
        }

        public static async Task<ImportOfficialTemplateResult> ImportTemplateAsync(
            XamlRoot xamlRoot,
            RemoteTemplateIndexItem item,
            CancellationToken ct = default)
        {
            var downloadResult = await OfficialTemplateService.DownloadTemplateAsync(item, ct);
            if (!downloadResult.Success || string.IsNullOrWhiteSpace(downloadResult.LocalPath))
            {
                return new ImportOfficialTemplateResult
                {
                    Success = false,
                    Message = downloadResult.Message,
                    IndexItem = item
                };
            }

            var inspection = TemplateService.InspectImportTemplate(downloadResult.LocalPath);
            if (!inspection.Success)
            {
                return new ImportOfficialTemplateResult
                {
                    Success = false,
                    Message = inspection.Message,
                    IndexItem = item
                };
            }

            var strategy = TemplateService.TemplateImportConflictStrategy.KeepBoth;
            if (inspection.HasConflict)
            {
                var conflictDialog = new ContentDialog
                {
                    Title = I18n.GetString("Template_Import_ConflictTitle"),
                    Content = I18n.Format(
                        "Template_Import_ConflictContent",
                        inspection.Template?.Name ?? string.Empty,
                        inspection.ConflictTemplateName),
                    PrimaryButtonText = I18n.GetString("Template_Import_ConflictReplace"),
                    SecondaryButtonText = I18n.GetString("Template_Import_ConflictKeepBoth"),
                    CloseButtonText = I18n.GetString("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = xamlRoot
                };
                ThemeService.ApplyThemeToDialog(conflictDialog);

                var result = await conflictDialog.ShowAsync();
                if (result == ContentDialogResult.None)
                {
                    return new ImportOfficialTemplateResult
                    {
                        Canceled = true,
                        IndexItem = item
                    };
                }

                strategy = result == ContentDialogResult.Primary
                    ? TemplateService.TemplateImportConflictStrategy.ReplaceExisting
                    : TemplateService.TemplateImportConflictStrategy.KeepBoth;
            }

            // 设置页和主页都走同一条导入链路，后续修正冲突逻辑时才不会出现行为漂移。
            var ok = TemplateService.ImportTemplate(downloadResult.LocalPath, strategy, out var message, out var importedTemplate);
            return new ImportOfficialTemplateResult
            {
                Success = ok,
                Message = message,
                ImportedTemplate = importedTemplate,
                IndexItem = item
            };
        }
    }
}
