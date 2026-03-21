using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class TemplateDialogCoordinatorService
    {
        private static readonly SemaphoreSlim DialogGate = new(1, 1);

        public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, XamlRoot? fallbackXamlRoot = null, CancellationToken ct = default)
        {
            if (dialog == null)
            {
                throw new ArgumentNullException(nameof(dialog));
            }

            await DialogGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await UiDispatcherService.RunOnUiAsync(async () =>
                {
                    dialog.XamlRoot ??= fallbackXamlRoot ?? MainWindowService.GetXamlRoot();
                    ThemeService.ApplyThemeToDialog(dialog);
                    return await dialog.ShowAsync();
                }).ConfigureAwait(false);
            }
            finally
            {
                DialogGate.Release();
            }
        }

        public static Task HideAsync(ContentDialog? dialog)
        {
            if (dialog == null)
            {
                return Task.CompletedTask;
            }

            return UiDispatcherService.RunOnUiAsync(() =>
            {
                try
                {
                    dialog.Hide();
                }
                catch
                {
                }
            });
        }
    }
}
