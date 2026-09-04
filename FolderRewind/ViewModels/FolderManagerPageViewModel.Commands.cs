using CommunityToolkit.Mvvm.Input;
using FolderRewind.Models;
using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class FolderManagerPageViewModel
{
    private readonly IFolderManagerInteractionService _interaction;
    private readonly AsyncCommandLifetime _commands;
    private readonly List<IRelayCommand> _allCommands = new();
    public bool IsBusy => _commands.IsBusy;
    internal sealed record DescriptionEdit(ManagedFolder Folder, string Text);
    public IAsyncRelayCommand<ManagedFolder> RemoveFolderCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> ToggleFavoriteCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> PinFolderCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> OpenFolderCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> OpenMiniWindowCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> ShowDetailsCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> RenameFolderCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> EditSourceScopeCommand { get; }
    public IAsyncRelayCommand<ManagedFolder> ChangeIconCommand { get; }
    public IAsyncRelayCommand AddSingleFolderCommand { get; }
    public IAsyncRelayCommand AddSubFoldersCommand { get; }
    public IAsyncRelayCommand PluginDiscoverCommand { get; }
    public IAsyncRelayCommand BackupConfigCommand { get; }
    public IAsyncRelayCommand BackupSelectedCommand { get; }
    public IAsyncRelayCommand HotkeyBackupCommand { get; }
    public IAsyncRelayCommand ConfigSettingsCommand { get; }
    internal IAsyncRelayCommand<DescriptionEdit> SaveDescriptionCommand { get; }

    public FolderManagerPageViewModel() : this(new FolderManagerInteractionService(MainWindowService.GetXamlRoot)) { }

    internal FolderManagerPageViewModel(IFolderManagerInteractionService interaction)
    {
        _interaction = interaction;
        _commands = new(ex =>
        {
            LogService.LogError("Folder operation failed.", nameof(FolderManagerPageViewModel), ex);
            _interaction.Notify(ex.Message, error: true);
        });
        _commands.StateChanged += () =>
        {
            OnPropertyChanged(nameof(IsBusy));
            foreach (var command in _allCommands) command.NotifyCanExecuteChanged();
        };
        RemoveFolderCommand = ItemCommand(RemoveFolderCoreAsync);
        ToggleFavoriteCommand = ItemCommand(ToggleFavoriteCoreAsync);
        PinFolderCommand = ItemCommand(PinFolderCoreAsync);
        OpenFolderCommand = ItemCommand(OpenFolderCoreAsync);
        OpenMiniWindowCommand = ItemCommand(OpenMiniWindowCoreAsync);
        ShowDetailsCommand = ItemCommand(ShowDetailsCoreAsync);
        RenameFolderCommand = ItemCommand(RenameFolderCoreAsync);
        EditSourceScopeCommand = ItemCommand(EditSourceScopeCoreAsync);
        ChangeIconCommand = ItemCommand(ChangeIconCoreAsync);
        AddSingleFolderCommand = PageCommand(AddSingleFolderCoreAsync);
        AddSubFoldersCommand = PageCommand(AddSubFoldersCoreAsync);
        PluginDiscoverCommand = PageCommand(PluginDiscoverCoreAsync);
        BackupConfigCommand = PageCommand(BackupConfigCoreAsync);
        BackupSelectedCommand = PageCommand(BackupSelectedCoreAsync);
        HotkeyBackupCommand = PageCommand(HotkeyBackupCoreAsync);
        ConfigSettingsCommand = PageCommand(ConfigSettingsCoreAsync);
        SaveDescriptionCommand = new AsyncRelayCommand<DescriptionEdit>((edit, token) =>
            _commands.RunAsync(async _ =>
            {
                if (edit is null || CurrentConfig?.SourceFolders.Contains(edit.Folder) != true || edit.Folder.Description == edit.Text) return;
                var before = edit.Folder.Description;
                await ConfigEditTransaction.ApplyAsync(() => edit.Folder.Description = edit.Text,
                    () => edit.Folder.Description = before, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
            }, token), edit => edit is not null && _commands.CanExecute);
        _allCommands.Add(SaveDescriptionCommand);
    }

    private IAsyncRelayCommand<ManagedFolder> ItemCommand(Func<ManagedFolder, CancellationToken, Task> action)
    {
        var command = new AsyncRelayCommand<ManagedFolder>((folder, token) =>
            _commands.RunAsync(inner => folder is not null && CurrentConfig?.SourceFolders.Contains(folder) == true
                ? action(folder, inner) : Task.CompletedTask, token),
            folder => folder is not null && _commands.CanExecute);
        _allCommands.Add(command);
        return command;
    }

    private IAsyncRelayCommand PageCommand(Func<CancellationToken, Task> action)
    {
        var command = new AsyncRelayCommand(token => _commands.RunAsync(inner =>
            CurrentConfig is not null ? action(inner) : Task.CompletedTask, token), () => _commands.CanExecute);
        _allCommands.Add(command);
        return command;
    }

    private Task EditFoldersAsync(Action edit, CancellationToken token)
    {
        var config = CurrentConfig ?? throw new InvalidOperationException(I18n.GetString("FolderManager_InvalidContext"));
        var before = config.SourceFolders.ToArray();
        var selected = _selectedFolder;
        return ConfigEditTransaction.ApplyAsync(edit, () =>
        {
            config.SourceFolders.Clear();
            foreach (var folder in before) config.SourceFolders.Add(folder);
            if (ReferenceEquals(CurrentConfig, config)) SetSelectedFolder(selected, false);
        }, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
    }

    private Task RemoveFolderCoreAsync(ManagedFolder folder, CancellationToken token)
        => EditFoldersAsync(() => RemoveFolder(folder), token);

    private Task ToggleFavoriteCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        var previous = folder.IsFavorite;
        return ConfigEditTransaction.ApplyAsync(() => folder.IsFavorite = !previous, () => folder.IsFavorite = previous,
            () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
    }

    private Task PinFolderCoreAsync(ManagedFolder folder, CancellationToken token)
        => EditFoldersAsync(() => PinFolderToTop(folder), token);

    private Task OpenFolderCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        if (!TryOpenFolder(folder)) throw new IOException(I18n.GetString("Common_Failed"));
        return Task.CompletedTask;
    }

    private Task OpenMiniWindowCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        TryOpenMiniWindow(folder);
        return Task.CompletedTask;
    }

    private Task ShowDetailsCoreAsync(ManagedFolder folder, CancellationToken token)
        => _interaction.ShowDetailsAsync(CurrentConfig!, folder, token);

    private Task ConfigSettingsCoreAsync(CancellationToken token)
        => _interaction.ShowConfigSettingsAsync(CurrentConfig!, token);

    private async Task RenameFolderCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        var name = await _interaction.RequestRenameAsync(folder, token);
        token.ThrowIfCancellationRequested();
        if (name is null) return;
        var preview = FolderRenameService.PreviewRename(folder, name);
        if (!preview.IsValid) throw new InvalidOperationException(preview.Message);
        var result = await FolderRenameService.RenameAsync(folder, name, token);
        if (!result.Success) throw new IOException(result.Message);
        token.ThrowIfCancellationRequested();
        SetPendingFolderPath(result.NewPath);
        SetSelectedFolder(null, false);
        RefreshCurrentFoldersView();
        PendingFolderSelectionRequested?.Invoke();
        _interaction.Notify(result.Message);
    }

    private async Task EditSourceScopeCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        var before = folder.SourceScope;
        var scope = await _interaction.EditScopeAsync(CurrentConfig!, folder, token);
        token.ThrowIfCancellationRequested();
        if (scope is null) return;
        await FolderScopeEditController.ApplyAsync(before, scope, BackupSourceRootSafetyPolicy.IsBroadRoot(folder.Path),
            (broad, ct) => ConfirmAsync(broad ? "SourceScopeEditor_BroadRootTitle" : "SourceScopeEditor_ExpandTitle",
                broad ? "SourceScopeEditor_BroadRootContent" : "SourceScopeEditor_ExpandContent", ct, true),
            value => folder.SourceScope = value, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"), token);
    }

    private async Task ChangeIconCoreAsync(ManagedFolder folder, CancellationToken token)
    {
        var source = await _interaction.PickIconAsync(token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source)) return;
        var destination = Path.Combine(folder.Path, "icon.png");
        var bytes = await File.ReadAllBytesAsync(source, token);
        var original = File.Exists(destination) ? await File.ReadAllBytesAsync(destination, token) : null;
        var previousPath = folder.CoverImagePath;
        token.ThrowIfCancellationRequested();
        try
        {
            await AtomicFileService.WriteAsync(destination, (stream, ct) => stream.WriteAsync(bytes.AsMemory(), ct).AsTask(), token);
            await ConfigEditTransaction.ApplyAsync(() =>
            {
                folder.CoverImagePath = string.Empty;
                folder.CoverImagePath = destination;
            }, () => folder.CoverImagePath = previousPath, () => ConfigService.SaveAsync(), I18n.GetString("Common_Failed"));
        }
        catch
        {
            if (original is not null) await AtomicFileService.WriteAsync(destination, (stream, ct) => stream.WriteAsync(original.AsMemory(), ct).AsTask());
            else if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }

    private async Task AddSingleFolderCoreAsync(CancellationToken token)
    {
        var path = await PickFolderAsync("FolderManager_AddSingleFolderPickerTitle", "AddSingle", token);
        if (string.IsNullOrWhiteSpace(path)) return;
        var status = AddFolderResult.Invalid;
        ManagedFolder? added = null;
        await EditFoldersAsync(() => status = AddFolder(path, FolderNameConflictService.ResolveDisplayName(null, path), out added), token);
        if (status == AddFolderResult.DuplicateDisplayName)
            await _interaction.MessageAsync(I18n.GetString("FolderManager_DuplicateDisplayName_Title"),
                I18n.Format("FolderManager_DuplicateDisplayName_Content", FolderNameConflictService.ResolveDisplayName(null, path), CurrentConfig?.Name ?? string.Empty), token);
        if (status == AddFolderResult.UnsafePathOverlap) await ShowUnsafeAsync(new[] { path }, token);
        if (added is not null) await SuggestPluginAsync(new[] { added }, token);
    }

    private async Task AddSubFoldersCoreAsync(CancellationToken token)
    {
        var path = await PickFolderAsync("FolderManager_AddSubFoldersPickerTitle", "AddSubFolders", token);
        if (string.IsNullOrWhiteSpace(path)) return;
        // Directory enumeration can block on removable/network volumes.
        var paths = await Task.Run(() => Directory.GetDirectories(path), token);
        token.ThrowIfCancellationRequested();
        var candidates = BuildPluginDiscoverCandidates(paths.Select(p => new ManagedFolder { Path = p, DisplayName = Path.GetFileName(p) }));
        await ImportCandidatesAsync(candidates, token);
    }

    private async Task PluginDiscoverCoreAsync(CancellationToken token)
    {
        var config = CurrentConfig!;
        PluginService.Initialize();
        var path = await PickFolderAsync("FolderManager_PluginDiscoverPickerTitle", "PluginDiscover", token);
        if (string.IsNullOrWhiteSpace(path)) return;
        var discovered = await FolderRewind.Services.Plugins.V3.PluginV3DiscoveryService.DiscoverFoldersAsync(config, path);
        token.ThrowIfCancellationRequested();
        if (discovered is null || discovered.Count == 0)
        {
            await MessageAsync("FolderManager_PluginDiscover_NoResultTitle", "FolderManager_PluginDiscover_NoResultContent", token);
            return;
        }
        var candidates = BuildPluginDiscoverCandidates(discovered);
        if (candidates.ToAdd.Count > 0 && !await _interaction.ConfirmAsync(
            I18n.GetString("FolderManager_PluginDiscover_ConfirmTitle"),
            I18n.Format("FolderManager_PluginDiscover_ConfirmContent", candidates.ToAdd.Count), token)) return;
        token.ThrowIfCancellationRequested();
        if (candidates.ToAdd.Count == 0 && candidates.UnsafePaths.Count == 0 && candidates.DuplicateDisplayNames.Count == 0)
            await MessageAsync("FolderManager_PluginDiscover_NoNewTitle", "FolderManager_PluginDiscover_NoNewContent", token);
        await ImportCandidatesAsync(candidates, token);
    }

    private async Task ImportCandidatesAsync(PluginDiscoverCandidatesResult candidates, CancellationToken token)
    {
        if (candidates.ToAdd.Count > 0)
            await EditFoldersAsync(() => AddDiscoveredFolders(candidates.ToAdd), token);
        token.ThrowIfCancellationRequested();
        await SuggestPluginAsync(candidates.ToAdd, token);
        if (candidates.DuplicateDisplayNames.Count > 0)
            await _interaction.MessageAsync(I18n.GetString("FolderManager_DuplicateDisplayName_Title"),
                I18n.Format("FolderManager_DuplicateDisplayName_BatchContent", CurrentConfig?.Name ?? string.Empty,
                    string.Join(Environment.NewLine, candidates.DuplicateDisplayNames.Distinct().Order().Select(n => $"- {n}"))), token);
        await ShowUnsafeAsync(candidates.UnsafePaths, token);
    }

    private async Task SuggestPluginAsync(IEnumerable<ManagedFolder> folders, CancellationToken token)
    {
        if (!TryMarkNeedMineRewindSuggestion(folders)) return;
        if (await ConfirmAsync("FolderManager_MineRewindHint_Title", "FolderManager_MineRewindHint_Content", token))
            NavigationService.NavigateTo("Settings", NavigationService.SettingsMinecraftPresetTarget);
    }

    private Task ShowUnsafeAsync(IEnumerable<string> paths, CancellationToken token)
        => paths.Any() ? _interaction.MessageAsync(I18n.GetString("Common_Failed"),
            I18n.Format("FolderManager_SourceDestinationOverlap_Content", string.Join(Environment.NewLine, paths.Distinct().Select(p => $"- {p}"))), token)
            : Task.CompletedTask;

    private async Task<string?> PickFolderAsync(string title, string key, CancellationToken token)
    {
        var path = await _interaction.PickFolderAsync(I18n.GetString(title), "FolderRewind.FolderManager." + key, token);
        token.ThrowIfCancellationRequested();
        return path;
    }

    private Task MessageAsync(string title, string message, CancellationToken token)
        => _interaction.MessageAsync(I18n.GetString(title), I18n.GetString(message), token);
    private Task<bool> ConfirmAsync(string title, string message, CancellationToken token, bool destructive = false)
        => _interaction.ConfirmAsync(I18n.GetString(title), I18n.GetString(message), token, destructive);

    private async Task BackupConfigCoreAsync(CancellationToken token)
    {
        await BackupCurrentConfigAsync();
        if (!token.IsCancellationRequested) BackupComment = string.Empty;
    }

    private async Task BackupSelectedCoreAsync(CancellationToken token)
    {
        await BackupSelectedFolderAsync();
        if (!token.IsCancellationRequested) BackupComment = string.Empty;
    }

    private async Task HotkeyBackupCoreAsync(CancellationToken token)
    {
        await BackupSelectedFolderAsync(BackupInvocationOptions.ForPluginHotkey().WithComment(BuildHotkeyBackupComment()));
        if (!token.IsCancellationRequested) BackupComment = string.Empty;
    }
}
