using Microsoft.UI.Xaml.Controls;

namespace FolderRewind.Services;

/// <summary>Dialog content/options. Creation, theme, root ownership and serialization remain in AppDialogService.</summary>
internal sealed class AppDialogRequest
{
    public object? Title { get; init; }
    public object? Content { get; init; }
    public string PrimaryButtonText { get; init; } = string.Empty;
    public string SecondaryButtonText { get; init; } = string.Empty;
    public string CloseButtonText { get; init; } = string.Empty;
    public ContentDialogButton DefaultButton { get; init; } = ContentDialogButton.Close;
}
