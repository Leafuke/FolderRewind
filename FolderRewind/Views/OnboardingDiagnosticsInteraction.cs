using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Views;

internal static class OnboardingDiagnosticsInteraction
{
    internal static async Task ExportAsync(IEnumerable<OnboardingDiagnosticItem> items, XamlRoot root, CancellationToken token)
    {
        var snapshot = items.ToArray();
        if (snapshot.Length == 0) return;
        var summary = OnboardingRepairPolicy.ExportSummary(snapshot.Select(i => (i.Check.Code, i.Check.State.ToString(), i.Check.CheckedAtUtc)));
        if (!await AppDialogService.Default.ConfirmAsync(I18n.GetString("Diagnostics_ExportTitle"), I18n.GetString("Diagnostics_ExportFields") + "\n\n" + summary,
            I18n.GetString("Common_Confirm"), root)) return;
        token.ThrowIfCancellationRequested();
        var path = await MainWindowService.PickSaveFilePathAsync("", "FolderRewind.Onboarding.Diagnostics",
            new Dictionary<string, IReadOnlyList<string>> { ["Text"] = new[] { ".txt" } }, "FolderRewind-checks");
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(path)) await File.WriteAllTextAsync(path, summary, token);
    }
}
