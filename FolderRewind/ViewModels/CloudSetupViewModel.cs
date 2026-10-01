using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Cloud;
using FolderRewind.History.Storage;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed class CloudSetupViewModel : ViewModelBase, IDisposable
{
    private BackupConfig? _config;
    private OwnedRcloneConnection? _owned;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _durableOperation;
    private TaskCompletionSource<bool>? _requestFinished;
    private long _revision;
    private string _executable = "", _configPath = "", _remote = "", _message = "", _working = "";
    private bool _busy, _saved;
    private HistoryRuntime? _analysis;
    private string? _analysisRevision;
    private string? _inspectionEvidence;
    private int _nextDirectoryOffset = -1;
    public bool CanBrowseMore => !IsBusy && _nextDirectoryOffset >= 0;
    private bool _restoring, _disposed;
    private long _diagnosticRevision;
    public ObservableCollection<OnboardingDiagnosticItem> Diagnostics { get; } = [];
    private void RecordCheck(string code, OnboardingCheckState state, string message)
    {
        if (_disposed) return;
        _diagnosticRevision = _revision;
        var context = new OnboardingRepairContext(_config?.Id ?? "", "", _config is null ? "" : NativeHistoryConfigLease.Signature(_config), DateTimeOffset.UtcNow);
        while (Diagnostics.Count >= OnboardingOperationBudgets.RecentDiagnostics) Diagnostics.RemoveAt(0);
        Diagnostics.Add(new(new(code, state, message, context.CheckedAtUtc, []), context));
    }
    public bool IsDiagnosticCurrent(OnboardingDiagnosticItem item)
    {
        var current = _config is null ? null : ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(c => c.Id == _config.Id);
        return !_disposed && _diagnosticRevision == _revision && Diagnostics.Contains(item)
            && (_config is null || current is not null && OnboardingRepairPolicy.IsCurrent(item.Context, current.Id, "", NativeHistoryConfigLease.Signature(current)));
    }
    public bool IsProject => _config is not null;
    public bool IsRecovery => _config is null;
    public ObservableCollection<string> RecoveryRepositories { get; } = [];
    public int SelectedRepositoryIndex { get; set; } = -1;
    private readonly List<RcloneRemoteOption> _remotes = [];
    private readonly List<VersionId> _versions = [];
    public ObservableCollection<string> Remotes { get; } = [];
    public ObservableCollection<string> Directories { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanBrowseMore)); } }
    public bool CanEdit => !IsBusy;
    public string Message { get => _message; set => SetProperty(ref _message, value); }
    public string Executable { get => _executable; set { if (SetProperty(ref _executable, value)) Invalidate(); } }
    public string ConfigPath { get => _configPath; set { if (SetProperty(ref _configPath, value)) { _inspectionEvidence = null; Invalidate(); } } }
    public string WorkingDirectory { get => _working; set { if (SetProperty(ref _working, value)) Invalidate(); } }
    public string RemoteRoot { get => _remote; set { if (SetProperty(ref _remote, value)) Invalidate(); } }
    public int SelectedVersionIndex { get; set; } = -1;
    private int _selectedRemoteIndex = -1;
    public int SelectedRemoteIndex
    {
        get => _selectedRemoteIndex;
        set { SetProperty(ref _selectedRemoteIndex, value); if (value >= 0 && value < _remotes.Count) RemoteRoot = _remotes[value].Name + ":"; }
    }
    public void Initialize(string id)
    {
        _config = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => c.Id == id);
        if (!string.IsNullOrEmpty(id) && _config is null)
        {
            _disposed = true;
            IsBusy = true;
            throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        }
        if (_config is null)
        {
            Executable = ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath;
            RemoteRoot = "";
            Message = I18n.GetString("CloudRecovery_TemporaryContext");
            OnPropertyChanged(nameof(IsProject)); OnPropertyChanged(nameof(IsRecovery));
            return;
        }
        _executable = CloudSyncService.GetEffectiveExecutablePath(_config);
        _configPath = _config.Cloud.RcloneConfigPath;
        _working = _config.Cloud.WorkingDirectory;
        _remote = _config.Cloud.RemoteBasePath;
        _saved = !string.IsNullOrWhiteSpace(_configPath);
        if (_saved) { using var context = CreateContext(); _inspectionEvidence = context.RevisionEvidence; }
        Message = I18n.GetString("CloudSetup_InitialState");
        OnPropertyChanged(nameof(Executable)); OnPropertyChanged(nameof(ConfigPath)); OnPropertyChanged(nameof(RemoteRoot)); OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(IsProject)); OnPropertyChanged(nameof(IsRecovery));
    }
    private void Invalidate() { _revision++; _request?.Cancel(); _saved = false; _analysisRevision = null; _nextDirectoryOffset = -1; OnPropertyChanged(nameof(CanBrowseMore)); Directories.Clear(); Diagnostics.Clear(); Message = I18n.GetString("CloudSetup_ConnectionChanged"); }
    private RcloneExecutionContext CreateContext()
    {
        var context = new RcloneExecutionContext(Executable, ConfigPath, WorkingDirectory, RemoteRoot,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderRewind", "credentials", "rclone-tasks"));
        if (_inspectionEvidence is not null && context.RevisionEvidence != _inspectionEvidence)
        {
            context.Dispose();
            throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        }
        _inspectionEvidence ??= context.RevisionEvidence;
        return context;
    }
    public async Task RunCheckAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        _requestFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _request?.Dispose(); _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try { await action(_request.Token); }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Message = I18n.GetString("Common_Canceled"); }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) { Message = CloudCommandSecurity.Redact(ex.Message); RecordCheck("cloud.access-unconfirmed", OnboardingCheckState.NeedsInput, Message); } }
        finally { IsBusy = false; _requestFinished?.TrySetResult(true); }
    }
    public void LoadRemotes()
    {
        var catalog = RcloneConnectionService.InspectRemotes(ConfigPath);
        Invalidate();
        _inspectionEvidence = catalog.RevisionEvidence;
        _selectedRemoteIndex = -1; OnPropertyChanged(nameof(SelectedRemoteIndex));
        _remotes.Clear(); _remotes.AddRange(catalog.Remotes);
        Remotes.Clear(); foreach (var remote in _remotes) Remotes.Add(remote.Name + " (" + remote.Backend + ")");
    }
    public async Task CreateWebDavAsync(string url, string user, string password, CancellationToken token, bool allowInsecure = false)
    {
        var connection = await RcloneConnectionService.CreateWebDavAsync(Executable, url, user, password, allowInsecure, token);
        if (_lifetime.IsCancellationRequested) { connection.Dispose(); return; }
        _owned?.Dispose(); _owned = connection;
        ConfigPath = connection.ConfigPath; RemoteRoot = connection.RemoteRoot;
        LoadRemotes(); Message = I18n.GetString("CloudSetup_CredentialsCreated");
    }
    public Task BrowseAsync(CancellationToken token) => BrowseCoreAsync(0, token);
    public Task BrowseMoreAsync(CancellationToken token) => _nextDirectoryOffset >= 0 ? BrowseCoreAsync(_nextDirectoryOffset, token) : Task.CompletedTask;
    private async Task BrowseCoreAsync(int offset, CancellationToken token)
    {
        var revision = _revision;
        using var context = CreateContext();
        var result = await RcloneConnectionService.BrowseAsync(context, token, offset);
        if (revision != _revision || _lifetime.IsCancellationRequested) return;
        Directories.Clear(); foreach (var name in result.Names) Directories.Add(name);
        _nextDirectoryOffset = result.NextOffset; OnPropertyChanged(nameof(CanBrowseMore));
        Message = I18n.GetString(result.NextOffset >= 0 ? "CloudSetup_DirectoryPage" : result.Truncated ? "CloudSetup_DirectoryBudgetReached" : "CloudSetup_ReadVerified");
        RecordCheck("cloud.access-verified", OnboardingCheckState.Ready, Message);
    }
    public async Task VerifyWriteAsync(CancellationToken token)
    {
        var revision = _revision;
        using var context = CreateContext();
        var result = await RcloneConnectionService.VerifyWriteAsync(context, token);
        if (result.RetainedObject is not null) NotificationService.ShowWarning(I18n.Format("CloudSetup_ProbeRetained", result.RetainedObject));
        if (revision != _revision || _lifetime.IsCancellationRequested) return;
        Message = result.Diagnostic + (result.RetainedObject is null ? "" : "\n" + I18n.Format("CloudSetup_ProbeRetained", result.RetainedObject));
        RecordCheck(result.ReadWriteVerified ? "cloud.access-verified" : "cloud.access-unconfirmed", result.ReadWriteVerified ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput, Message);
    }
    public async Task SaveAsync(CancellationToken token)
    {
        if (_config is null) return;
        using var context = CreateContext();
        context.RequireUnchanged();
        var draft = new ConfigSettingsDraftService(_config);
        draft.Draft.Cloud.ExecutablePath = Executable;
        draft.Draft.Cloud.RcloneConfigPath = ConfigPath;
        draft.Draft.Cloud.WorkingDirectory = WorkingDirectory;
        draft.Draft.Cloud.RemoteBasePath = RemoteRoot;
        draft.Draft.Cloud.CommandMode = CloudCommandMode.Rclone;
        token.ThrowIfCancellationRequested();
        ConfigSaveResult result;
        try { result = await draft.CommitAsync(); }
        catch (ConfigEditRollbackException) { _owned?.Commit(); _owned = null; throw; }
        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
        _owned?.Commit(); _owned = null;
        _config = ConfigService.CurrentConfig.BackupConfigs.Single(c => c.Id == _config.Id);
        context.RequireUnchanged();
        _inspectionEvidence = context.RevisionEvidence;
        _saved = true;
        Message = I18n.GetString("CloudSetup_SavedNoCopy");
    }
    public async Task LoadVersionsAsync(CancellationToken token)
    {
        if (_config is null) return;
        var runtime = await NativeHistoryCoreGateway.EnsureReadyAsync(_config, token);
        var versions = await runtime.Query.GetAllVersionsAsync(token);
        if (_lifetime.IsCancellationRequested) return;
        Versions.Clear(); _versions.Clear();
        foreach (var version in versions.OrderByDescending(v => v.CreatedAtUtc))
        {
            _versions.Add(version.VersionId);
            Versions.Add(version.SourceDescriptorSnapshot.DisplayName + " · " + UserDisplayFormatter.LongDateTime(version.CreatedAtUtc.LocalDateTime) + " · " + version.VersionId);
        }
    }
    public async Task UploadAsync()
    {
        if (_disposed || IsBusy || _config is null) return;
        if (!_saved) { Message = I18n.GetString("CloudSetup_SaveFirst"); return; }
        if (SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count) { Message = I18n.GetString("CloudSetup_SelectVersion"); return; }
        IsBusy = true;
        _durableOperation = new();
        try
        {
            using var context = CreateContext();
            using var connection = new RcloneExecutionScope(context, owns: false);
            var result = await CloudSyncService.UploadVersionClosureAsync(_config, _versions[SelectedVersionIndex], _durableOperation.Token);
            Message = I18n.GetString(result.Canceled ? "Common_Canceled" : result.Complete ? "CloudSetup_UploadComplete" : "CloudSetup_UploadIncomplete")
                + "\n" + string.Join("\n", result.Items.Select(i => i.RepresentationId + ": " + i.State + " " + CloudCommandSecurity.Redact(i.Diagnostic)))
                + "\n" + string.Join(", ", result.Missing) + "\n" + CloudCommandSecurity.Redact(result.Metadata?.Diagnostic);
            RecordCheck(result.Complete ? "cloud.copy-complete" : "cloud.copy-incomplete", result.Complete ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput, Message);
        }
        catch (OperationCanceledException) { Message = I18n.GetString("Common_Canceled"); }
        catch (Exception ex) { Message = CloudCommandSecurity.Redact(ex.Message); RecordCheck("cloud.copy-incomplete", OnboardingCheckState.NeedsInput, Message); }
        finally { IsBusy = false; _durableOperation.Dispose(); _durableOperation = null; }
    }
    public void EnterDirectory(string name)
    {
        if (IsBusy || name.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0 || name is "." or "..") return;
        RemoteRoot = RemoteRoot.TrimEnd('/') + "/" + name;
    }
    public void CancelCheck() { _request?.Cancel(); _durableOperation?.Cancel(); }
    public async Task DiscoverRepositoriesAsync(CancellationToken token)
    {
        var revision = _revision;
        using var context = CreateContext();
        var output = await RcloneConnectionService.RunAsync(context.CreateStartInfo(["lsf", context.RemoteRoot.TrimEnd('/') + "/_folderrewind/history", "--dirs-only"]), OnboardingOperationBudgets.RemoteBrowse, token);
        if (revision != _revision || _disposed) return;
        var folders = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('/')).ToArray();
        RecoveryRepositories.Clear();
        foreach (var folder in folders.Take(OnboardingOperationBudgets.RemoteBatch))
            if (folder.IndexOfAny(['/', '\\', ':']) < 0 && folder is not "." and not "..") RecoveryRepositories.Add(folder);
        Message = I18n.GetString(folders.Length > OnboardingOperationBudgets.RemoteBatch ? "CloudSetup_DirectoriesTruncated" : "CloudRecovery_SelectRepository");
    }
    public async Task AnalyzeRepositoryAsync(CancellationToken token)
    {
        if (SelectedRepositoryIndex < 0 || SelectedRepositoryIndex >= RecoveryRepositories.Count) throw new InvalidOperationException(I18n.GetString("CloudRecovery_SelectRepository"));
        var revision = _revision;
        using var context = CreateContext();
        var folder = RecoveryRepositories[SelectedRepositoryIndex];
        var json = await RcloneConnectionService.RunAsync(context.CreateStartInfo(["cat", context.RemoteRoot.TrimEnd('/') + "/_folderrewind/history/" + folder + "/repository.json"]), OnboardingOperationBudgets.RemoteBrowse, token);
        var descriptor = HistoryRepositoryDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        if (HistoryRepositoryPaths.EncodeConfigPathSegment(descriptor.ConfigId) != folder) throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var runtime = new HistoryRuntime(new FileHistoryRepository(descriptor.ConfigId,
            new HistoryRepositoryPaths(System.IO.Path.Combine(ConfigService.ConfigDirectory, "onboarding", "cloud-recovery", Guid.NewGuid().ToString("N")))));
        try
        {
            await runtime.InitializeAsync(token);
            var result = await new HistoryMetadataSyncService(runtime, new RcloneNativeHistoryTransport(context)).PullAsync(token);
            if (!result.Succeeded) throw new InvalidOperationException(CloudCommandSecurity.Redact(result.Diagnostic));
            if (revision != _revision || _disposed) { await runtime.DisposeAsync(); return; }
            if (_analysis is not null) await _analysis.DisposeAsync();
            _analysis = runtime; _analysisRevision = context.RevisionEvidence;
            Versions.Clear(); _versions.Clear();
            foreach (var version in (await runtime.Query.GetAllVersionsAsync(token)).OrderByDescending(v => v.CreatedAtUtc))
            { _versions.Add(version.VersionId); Versions.Add(version.SourceDescriptorSnapshot.DisplayName + " · " + UserDisplayFormatter.LongDateTime(version.CreatedAtUtc.LocalDateTime) + " · " + version.VersionId); }
            Message = I18n.Format("CloudRecovery_HistoryLoaded", descriptor.ConfigId, result.DownloadedPacks);
        }
        catch { if (!ReferenceEquals(_analysis, runtime)) await runtime.DisposeAsync(); throw; }
    }
    public sealed record RecoveryConfirmation(HistoryRecoveryPreview Preview, long Revision, string ConnectionRevision);
    public async Task<RecoveryConfirmation> PreviewRecoveryAsync(CancellationToken token)
    {
        if (_analysis is null || SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count)
            throw new InvalidOperationException(I18n.GetString("CloudSetup_SelectVersion"));
        var revision = _revision;
        var selected = _versions[SelectedVersionIndex];
        using var context = CreateContext();
        if (_analysisRevision != context.RevisionEvidence) throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        var preview = await CloudReadOnlyRecoveryService.PreviewAsync(_analysis, selected, token);
        if (_disposed || revision != _revision || SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count
            || _versions[SelectedVersionIndex] != selected) throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        return new(preview, revision, context.RevisionEvidence);
    }

    public static string DescribeRecovery(HistoryRecoveryPreview preview)
    {
        var boundary = preview.Version.EffectiveSourceBoundary;
        var text = I18n.Format("CloudRecovery_PayloadPreview", preview.Version.SourceDescriptorSnapshot.DisplayName,
            preview.Version.VersionId, UserDisplayFormatter.LongDateTime(preview.Version.CreatedAtUtc.LocalDateTime),
            I18n.GetString(preview.RequiredFidelity == MaterializationFidelity.Partial ? "CloudRecovery_Partial" : "CloudRecovery_Exact"),
            preview.MissingPayloads.Length, preview.DownloadBytes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "?",
            string.Join(", ", preview.MissingPayloads.Select(p => p.RepresentationId)),
            string.Join(", ", boundary.ScopeRules), string.Join(", ", boundary.FilterRules));
        if (preview.RequiredFidelity == MaterializationFidelity.Partial) text += "\n\n" + I18n.GetString("Export_PartialNotice");
        if (!preview.CanPrepare) text += "\n\n" + I18n.GetString("CloudRecovery_PreviewBlocked");
        return text;
    }

    public async Task RestoreAnalyzedAsync(string destination, string? password, RecoveryConfirmation confirmation)
    {
        if (_disposed || IsBusy || _analysis is null || SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count) return;
        using var context = CreateContext();
        if (_analysisRevision != context.RevisionEvidence || confirmation.ConnectionRevision != context.RevisionEvidence
            || confirmation.Revision != _revision || confirmation.Preview.Version.VersionId != _versions[SelectedVersionIndex]
            || !confirmation.Preview.CanPrepare) throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        IsBusy = true; _restoring = true;
        _durableOperation = new();
        var task = new BackupTask { FolderName = I18n.GetString("CloudRecovery_Task"), IsIndeterminate = true, Status = I18n.GetString("CloudRecovery_Downloading") };
        BackupService.ActiveTasks.Add(task);
        try
        {
            await CloudReadOnlyRecoveryService.RestoreToNewLocationAsync(_analysis, context, confirmation.Preview.Version.VersionId,
                destination, password, _durableOperation.Token, confirmation.Preview.Assessment.Selected!.RepresentationId,
                (id, complete) => EnqueueOnUiThread(() =>
                {
                    if (!task.IsCompleted) task.Status = I18n.Format(complete ? "CloudRecovery_PayloadVerified" : "CloudRecovery_PayloadDownloading", id);
                }));
            task.IsSuccess = true; task.Status = I18n.GetString("CloudRecovery_Completed");
            Message = I18n.Format("Export_Completed", destination);
        }
        catch (OperationCanceledException) { task.IsSuccess = false; task.Status = I18n.GetString("Common_Canceled"); Message = task.Status; }
        catch (Exception ex) { task.IsSuccess = false; task.ErrorMessage = CloudCommandSecurity.Redact(ex.Message); Message = task.ErrorMessage; }
        finally
        {
            task.IsCompleted = true; task.IsIndeterminate = false; _restoring = false; IsBusy = false;
            _durableOperation.Dispose(); _durableOperation = null;
            if (_disposed) await CleanupAsync();
        }
    }
    private async Task CleanupAsync()
    {
        if (_requestFinished is { } pending) await pending.Task;
        _owned?.Dispose(); _owned = null;
        if (_analysis is not null) { await _analysis.DisposeAsync(); _analysis = null; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _request?.Cancel();
        if (!_restoring) TaskObserver.Observe(CleanupAsync(), nameof(CloudSetupViewModel));
    }
}
