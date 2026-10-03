using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

internal interface IMiniWindowOperations
{
    Task<OperationOutcome> BackupAsync(string comment, CancellationToken token);
    bool HasChanges();
    void OpenFolder();
}

internal sealed class MiniWindowOperations(MiniWindowContext context) : IMiniWindowOperations
{
    public bool HasChanges() => FolderWatcherService.HasChanges(context.Folder.Path);
    public void OpenFolder()
    {
        if (!ShellPathService.TryOpenPath(context.Folder.Path, out var error))
            throw new System.IO.IOException(error ?? I18n.GetString("Common_Failed"));
    }
    public async Task<OperationOutcome> BackupAsync(string comment, CancellationToken token)
    {
        var path = context.Folder.Path;
        var revision = FolderWatcherService.GetChangeRevision(path);
        var result = await BackupService.BackupFolderForPluginAsync(context.Config, context.Folder,
            BackupInvocationOptions.ForManual(comment), token);
        if (result.Outcome is OperationOutcome.Success or OperationOutcome.SuccessWithWarnings or OperationOutcome.NoChanges)
            FolderWatcherService.ResetChangesThrough(path, revision);
        return result.Outcome;
    }
}

internal sealed class MiniWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MiniWindowContext _context;
    private readonly MiniBackupController _backup;
    public IAsyncRelayCommand<string> BackupCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand ToggleExpandDirectionCommand { get; }
    public MiniWindowVisualState VisualState => _backup.State;
    public string FolderDisplayName => _context.Folder.DisplayName;
    public MiniExpandDirection ExpandDirection => _context.ExpandDirection;
    public string HotkeyComment => I18n.GetString("MiniWindow_BackupComment_Hotkey");
    public string ExpandDirectionText => I18n.GetString(ExpandDirection == MiniExpandDirection.Right ? "MiniWindow_Menu_ExpandLeft" : "MiniWindow_Menu_ExpandRight");
    public string ExpandDirectionGlyph => ExpandDirection == MiniExpandDirection.Right ? "\uE76B" : "\uE76C";
    public string Tooltip => I18n.Format("MiniWindow_Tip_Format", FolderDisplayName, _context.Folder.Path,
        _context.Folder.LastBackupTimeDisplay, I18n.GetString(VisualState switch
        {
            MiniWindowVisualState.Changed => "MiniWindow_Tip_Changed",
            MiniWindowVisualState.BackingUp => "MiniWindow_Tip_BackingUp",
            MiniWindowVisualState.BackupDone => "MiniWindow_Tip_Done",
            MiniWindowVisualState.BackupFailed => "MiniWindow_Tip_Failed",
            _ => "MiniWindow_Tip_Normal"
        }));

    public MiniWindowViewModel(MiniWindowContext context) : this(context, new MiniWindowOperations(context)) { }
    internal MiniWindowViewModel(MiniWindowContext context, IMiniWindowOperations operations, TimeProvider? time = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _backup = new(operations.BackupAsync, operations.HasChanges, ReportError, time);
        BackupCommand = new AsyncRelayCommand<string>((comment, token) => _backup.BackupAsync(comment ?? string.Empty, token), _ => _backup.CanExecute);
        OpenFolderCommand = new RelayCommand(() => { try { operations.OpenFolder(); } catch (Exception ex) { ReportError(ex); } });
        ToggleExpandDirectionCommand = new RelayCommand(() =>
        {
            // Direction is an existing per-window runtime preference, not a new JSON field.
            _context.ExpandDirection = ExpandDirection == MiniExpandDirection.Right ? MiniExpandDirection.Left : MiniExpandDirection.Right;
            OnPropertyChanged(nameof(ExpandDirection));
            OnPropertyChanged(nameof(ExpandDirectionText));
            OnPropertyChanged(nameof(ExpandDirectionGlyph));
        });
        _backup.Changed += OnBackupChanged;
    }

    private void OnBackupChanged()
    {
        OnPropertyChanged(nameof(VisualState)); OnPropertyChanged(nameof(Tooltip));
        BackupCommand.NotifyCanExecuteChanged();
    }
    public void RefreshWatchState() => _backup.RefreshWatchState();
    internal MiniWindowLayoutResult GetExpandedLayout(MiniWindowPixelPoint anchor, MiniWindowPixelRect workArea, double scale)
        => MiniWindowLayoutPolicy.GetExpandedBounds(anchor, workArea, scale,
            ExpandDirection == MiniExpandDirection.Left ? MiniWindowLayoutDirection.Left : MiniWindowLayoutDirection.Right);
    private static void ReportError(Exception ex)
    {
        LogService.LogError(I18n.Format("MiniWindow_Log_BackupFailed", ex.Message), nameof(MiniWindowViewModel), ex);
        NotificationService.ShowError(ex.Message);
    }
    public void Dispose() { _backup.Changed -= OnBackupChanged; _backup.Dispose(); }
}
