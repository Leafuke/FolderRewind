using FolderRewind.Models;
using FolderRewind.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;

namespace FolderRewind.Views.Settings;

public sealed partial class OpenListEnvironmentControl : UserControl
{
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _bringIntoViewPending;
    private OpenListRuntimeSettings _savedSettings = new();

    public OpenListEnvironmentControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _lifetime.Cancel();
    }

    public void ShowRepairTarget()
    {
        EnvironmentExpander.IsExpanded = true;
        if (IsLoaded) EnvironmentExpander.StartBringIntoView();
        else _bringIntoViewPending = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); }
        _savedSettings = ConfigService.CurrentConfig.GlobalSettings.OpenListRuntime ?? new();
        Executable.Text = _savedSettings.ExecutablePath;
        ServiceUri.Text = _savedSettings.ServiceBaseUri;
        if (_bringIntoViewPending)
        {
            _bringIntoViewPending = false;
            DispatcherQueue.TryEnqueue(() => { if (IsLoaded) EnvironmentExpander.StartBringIntoView(); });
        }
    }

    private void OnEnvironmentCardSizeChanged(object sender, SizeChangedEventArgs e) => SettingsCardLayout.Apply(sender, e);

    private void OnActionsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ManualGuide is null || sender is not Grid grid) return;
        var narrow = e.NewSize.Width < 520;
        Grid.SetColumn(ManualGuide, narrow ? 0 : 1);
        Grid.SetRow(ManualGuide, narrow ? 1 : 0);
        Grid.SetColumnSpan(ManualGuide, narrow ? 3 : 1);
        grid.RowSpacing = narrow ? 8 : 0;
    }

    private void OnDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (Result is not null) Result.Visibility = Visibility.Collapsed;
    }

    // 移除尚未用于启动的高级字段入口，但保留已经保存的目录配置。
    private OpenListRuntimeSettings Draft() => new()
    {
        ExecutablePath = Executable.Text.Trim(),
        ServiceBaseUri = ServiceUri.Text.Trim(),
        WorkingDirectory = _savedSettings.WorkingDirectory,
        DataDirectory = _savedSettings.DataDirectory,
        ConfigFilePath = _savedSettings.ConfigFilePath
    };

    private void ShowResult(string message)
    {
        Result.Text = message;
        Result.Visibility = Visibility.Visible;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        try
        {
            var draft = Draft();
            await OpenListRuntimeService.SaveAsync(draft);
            _savedSettings = draft;
            if (!_lifetime.IsCancellationRequested) ShowResult(I18n.GetString("OpenList_SavedManualOnly"));
        }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) ShowResult(CloudCommandSecurity.Redact(ex.Message)); }
        finally { _busy = false; IsEnabled = true; }
    }

    private async void OnPickExecutable(object sender, RoutedEventArgs e)
    {
        var path = await MainWindowService.PickFilePathAsync("", "FolderRewind.OpenList.Executable", [".exe"], MainWindowService.SuggestedPickerLocation.ComputerFolder);
        if (!_lifetime.IsCancellationRequested && path is not null) Executable.Text = path;
    }

    private void OnOpenManagement(object sender, RoutedEventArgs e)
    {
        try { var uri = RcloneConnectionService.ValidateWebDavUrl(ServiceUri.Text.Trim()); ShellPathService.TryOpenPath(uri.AbsoluteUri, out _); }
        catch (Exception ex) { ShowResult(CloudCommandSecurity.Redact(ex.Message)); }
    }
}
