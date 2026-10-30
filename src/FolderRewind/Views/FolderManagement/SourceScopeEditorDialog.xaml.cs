using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace FolderRewind.Views;

public sealed partial class SourceScopeEditorDialog : ContentDialog, IDisposable
{
    public SourceScopeEditorDialogViewModel ViewModel { get; }

    public SourceScopeEditorDialog(BackupConfig config, ManagedFolder folder)
    {
        ViewModel = new SourceScopeEditorDialogViewModel(config, folder);
        InitializeComponent();
        Title = I18n.GetString("SourceScopeEditor_Title");
        PrimaryButtonText = I18n.GetString("Common_Save");
        CloseButtonText = I18n.GetString("Common_Cancel");
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = ViewModel.CanSave;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closed += OnClosed;
        ThemeService.ApplyThemeToDialog(this);
        _ = ViewModel.RefreshPreviewAsync();
    }

    public BackupSourceScope? ResultScope { get; private set; }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        IsPrimaryButtonEnabled = ViewModel.CanSave;

    private void OnAddRuleClick(object sender, RoutedEventArgs e) => ViewModel.AddRule();

    private void OnRemoveRuleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceScopeRuleItem item }) ViewModel.RemoveRule(item);
    }

    private async void OnRefreshPreviewClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshPreviewAsync();

    private void OnCancelPreviewClick(object sender, RoutedEventArgs e) => ViewModel.CancelPreview();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.TryCreateScope(out var scope, out _))
        {
            args.Cancel = true;
            return;
        }
        ResultScope = scope;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.CanSave))
        {
            IsPrimaryButtonEnabled = ViewModel.CanSave;
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();

    public void Dispose()
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        PrimaryButtonClick -= OnPrimaryButtonClick;
        Closed -= OnClosed;
        ViewModel.Dispose();
    }
}
