using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services
{
    internal static class TemplateDialogCoordinatorService
    {
        // WinUI ContentDialog 不能并发 Show；统一串行化能避免随机抛错和焦点抢占。
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
                    // 弹窗一定要在 UI 线程、且绑定到当前窗口的 XamlRoot。
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
