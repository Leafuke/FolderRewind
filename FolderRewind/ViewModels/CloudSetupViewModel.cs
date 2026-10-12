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
    private string? _analysisFolder;
    private TaskCompletionSource<bool>? _restoreFinished;
    private string? _inspectionEvidence;
    private string? _readableEvidence, _writableEvidence;
    private int _nextDirectoryOffset = -1;
    public bool CanBrowseMore => !IsBusy && _nextDirectoryOffset >= 0;
    private bool _restoring, _disposed;
    private long _diagnosticRevision;
    public ObservableCollection<OnboardingDiagnosticItem> Diagnostics { get; } = [];
    private void RecordCheck(string code, OnboardingCheckState state, string message)
    {
        if (_disposed) return;
        MessageState = state;
        OnPropertyChanged(nameof(MessageState));
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
    private readonly List<string> _repositoryFolders = [];
    private int _selectedRepositoryIndex = -1;
    public int SelectedRepositoryIndex
    {
        get => _selectedRepositoryIndex;
        set
        {
            if (!SetProperty(ref _selectedRepositoryIndex, value)) return;
            // 仓库选择是恢复身份的一部分；立刻撤销旧预览，等待使用者结束后再释放运行时。
            _revision++;
            _request?.Cancel();
            InvalidateAnalysis();
        }
    }
    private readonly List<RcloneRemoteOption> _remotes = [];
    private readonly List<VersionId> _versions = [];
    public ObservableCollection<string> Remotes { get; } = [];
    public ObservableCollection<string> Directories { get; } = [];
    public ObservableCollection<string> Versions { get; } = [];
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanBrowseMore)); } }
    public bool CanEdit => !IsBusy;
    public OnboardingCheckState MessageState { get; private set; } = OnboardingCheckState.Unknown;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasDirectories => Directories.Count > 0;
    public string Message { get => _message; set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public string Executable { get => _executable; set { if (SetProperty(ref _executable, value)) Invalidate(); } }
    public string ConfigPath { get => _configPath; set { if (SetProperty(ref _configPath, value)) { _inspectionEvidence = null; Invalidate(); } } }
    public string WorkingDirectory { get => _working; set { if (SetProperty(ref _working, value)) Invalidate(); } }
    public string RemoteRoot { get => _remote; set { if (SetProperty(ref _remote, value)) Invalidate(); } }
    private int _selectedVersionIndex = -1;
    public int SelectedVersionIndex { get => _selectedVersionIndex; set => SetProperty(ref _selectedVersionIndex, value); }
    private int _selectedRemoteIndex = -1;
    public int SelectedRemoteIndex
    {
        get => _selectedRemoteIndex;
        set { SetProperty(ref _selectedRemoteIndex, value); if (value >= 0 && value < _remotes.Count) RemoteRoot = _remotes[value].Name + ":"; }
    }
    public async Task InitializeAsync(string id, CancellationToken token = default)
    {
        if (_disposed) return;
        IsBusy = true;
        _requestFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        try { await InitializeCoreAsync(id, linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) { _saved = false; ReportConnectionFailure(ex); } }
        finally { if (!_disposed) IsBusy = false; _requestFinished.TrySetResult(true); }
    }

    private async Task InitializeCoreAsync(string id, CancellationToken token)
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
        Message = I18n.GetString("CloudSetup_CredentialMigrating");
        var originalPath = _config.Cloud.RcloneConfigPath;
        await ConfigService.WaitForRcloneCredentialMigrationAsync(originalPath, token, allowUncertain: true);
        token.ThrowIfCancellationRequested();
        _config = ConfigService.CurrentConfig.BackupConfigs.SingleOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
        _executable = CloudSyncService.GetEffectiveExecutablePath(_config);
        _configPath = _config.Cloud.RcloneConfigPath;
        _working = _config.Cloud.WorkingDirectory;
        _remote = _config.Cloud.RemoteBasePath;
        _saved = !string.IsNullOrWhiteSpace(_configPath);
        Message = I18n.GetString("CloudSetup_InitialState");
        OnPropertyChanged(nameof(Executable)); OnPropertyChanged(nameof(ConfigPath)); OnPropertyChanged(nameof(RemoteRoot)); OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(IsProject)); OnPropertyChanged(nameof(IsRecovery));
        if (_saved) { using var context = CreateContext(); _inspectionEvidence = context.RevisionEvidence; }
        if (ConfigService.GetRcloneCredentialMigrationError(originalPath) is { } error)
        {
            _saved = false;
            ReportConnectionFailure(error);
        }
    }
    private void Invalidate()
    {
        MessageState = OnboardingCheckState.Unknown; OnPropertyChanged(nameof(MessageState));
        _revision++; _request?.Cancel(); _saved = false;
        _readableEvidence = null; _writableEvidence = null;
        InvalidateAnalysis();
        SelectedRepositoryIndex = -1; RecoveryRepositories.Clear(); _repositoryFolders.Clear();
        _nextDirectoryOffset = -1; OnPropertyChanged(nameof(CanBrowseMore));
        Directories.Clear(); OnPropertyChanged(nameof(HasDirectories)); Diagnostics.Clear();
        Message = I18n.GetString("CloudSetup_ConnectionChanged");
    }
    private void InvalidateAnalysis()
    {
        var previous = _analysis;
        _analysis = null; _analysisRevision = null; _analysisFolder = null;
        Versions.Clear(); _versions.Clear(); SelectedVersionIndex = -1;
        if (previous is not null)
            TaskObserver.Observe(DisposeAnalysisAsync(previous, _requestFinished?.Task, _restoreFinished?.Task), nameof(CloudSetupViewModel));
    }
    private static async Task DisposeAnalysisAsync(HistoryRuntime runtime, Task? request, Task? restore)
    {
        if (request is not null) await request;
        if (restore is not null) await restore;
        await runtime.DisposeAsync();
    }
    private RcloneExecutionContext CreateContext()
    {
        var context = new RcloneExecutionContext(Executable, ConfigPath, WorkingDirectory, RemoteRoot,
            ConfigService.RcloneTasksDirectory, CloudConnectionCatalog.IsManaged(ConfigPath));
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
        try
        {
            var originalPath = ConfigPath;
            var migratedPath = await ConfigService.WaitForRcloneCredentialMigrationAsync(originalPath, _request.Token, allowUncertain: true);
            _request.Token.ThrowIfCancellationRequested();
            if (originalPath == ConfigPath && migratedPath != originalPath)
            {
                ConfigPath = migratedPath;
                _request.Dispose();
                _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            }
            await action(_request.Token);
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) Message = I18n.GetString("Common_Canceled"); }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) ReportConnectionFailure(ex); }
        finally { IsBusy = false; _requestFinished?.TrySetResult(true); }
    }
    private void ReportConnectionFailure(Exception error)
    {
        var code = RcloneConnectionDiagnostics.Code(error);
        var message = CloudCommandSecurity.Redact(error.Message);
        RecordCheck(code, OnboardingCheckState.NeedsInput, message);
        Message = code == "cloud.access-unconfirmed" ? I18n.GetString("CloudSetup_CheckFailedShort") : message;
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
        var connection = await RcloneConnectionService.CreateWebDavAsync(Executable, ConfigService.RcloneConnectionsDirectory, url, user, password, allowInsecure, token);
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
        _inspectionEvidence = context.RevisionEvidence;
        _readableEvidence = context.TargetRevisionEvidence + "|" + RemoteRoot;
        Directories.Clear(); foreach (var name in result.Names) Directories.Add(name);
        OnPropertyChanged(nameof(HasDirectories));
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
        _inspectionEvidence = context.RevisionEvidence;
        _writableEvidence = result.ReadWriteVerified ? context.TargetRevisionEvidence + "|" + RemoteRoot : null;
        Message = I18n.GetString(result.ReadWriteVerified ? "CloudSetup_WriteVerifiedShort" : "CloudSetup_WriteFailedShort")
            + (result.RetainedObject is null ? "" : "\n" + I18n.GetString("CloudSetup_ProbeCleanup"));
        var details = CloudCommandSecurity.Redact(result.Diagnostic)
            + (result.RetainedObject is null ? "" : "\n" + I18n.Format("CloudSetup_ProbeRetained", result.RetainedObject));
        var code = result.Diagnostic.StartsWith("[cloud.snapshot-unavailable]", StringComparison.Ordinal) ? "cloud.snapshot-unavailable" : "cloud.access-unconfirmed";
        if (code == "cloud.snapshot-unavailable") Message = I18n.GetString("CloudSetup_SnapshotUnavailable");
        RecordCheck(result.ReadWriteVerified ? "cloud.access-verified" : code, result.ReadWriteVerified ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput, details);
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
        draft.Draft.Cloud.LegacyConversionRequired = false;
        draft.Draft.Cloud.ReadableEvidence = _readableEvidence ?? "";
        draft.Draft.Cloud.WritableEvidence = _writableEvidence ?? "";
        if (_writableEvidence is null) draft.Draft.Cloud.Enabled = false;
        token.ThrowIfCancellationRequested();
        ConfigSaveResult result;
        try { result = await draft.CommitAsync(); }
        catch (ConfigEditRollbackException ex)
        {
            _owned?.Commit(); _owned = null;
            throw RcloneConnectionDiagnostics.Failure("cloud.credentials-save-uncertain", "CloudSetup_CredentialSaveUncertain", ex);
        }
        if (!result.Success) throw RcloneConnectionDiagnostics.Failure("cloud.connection-save-failed", "CloudSetup_ConnectionSaveFailed", result.Exception);
        _owned?.Commit(); _owned = null;
        ConfigService.ConfirmRcloneCredentialPath(ConfigPath);
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
            Versions.Add(version.SourceDescriptorSnapshot.DisplayName + " · " + UserDisplayFormatter.LongDateTime(version.CreatedAtUtc.LocalDateTime));
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
            var details = string.Join("\n", result.Items.Select(i => i.RepresentationId + ": " + i.State + " " + CloudCommandSecurity.Redact(i.Diagnostic)))
                + "\n" + string.Join(", ", result.Missing) + "\n" + CloudCommandSecurity.Redact(result.Metadata?.Diagnostic);
            Message = I18n.GetString(result.Canceled ? "Common_Canceled" : result.Complete ? "CloudSetup_UploadComplete" : "CloudSetup_UploadIncomplete");
            RecordCheck(result.Complete ? "cloud.copy-complete" : "cloud.copy-incomplete", result.Complete ? OnboardingCheckState.Ready : OnboardingCheckState.NeedsInput, details);
        }
        catch (OperationCanceledException) { Message = I18n.GetString("Common_Canceled"); }
        catch (Exception ex) { ReportConnectionFailure(ex); }
        finally { IsBusy = false; _durableOperation.Dispose(); _durableOperation = null; }
    }
    public void EnterDirectory(string name)
    {
        if (IsBusy || name.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0 || name is "." or "..") return;
        RemoteRoot = CloudRemotePath.Append(RemoteRoot, name);
    }
    public void CancelCheck() { _request?.Cancel(); _durableOperation?.Cancel(); }
    public async Task DiscoverRepositoriesAsync(CancellationToken token)
    {
        var revision = _revision;
        using var context = CreateContext();
        var output = await RcloneConnectionService.RunAsync(context.CreateStartInfo(["lsf", CloudRemotePath.Append(context.RemoteRoot, "_folderrewind/history"), "--dirs-only"]), OnboardingOperationBudgets.RemoteBrowse, token);
        if (revision != _revision || _disposed) return;
        var folders = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('/')).ToArray();
        // 即使发现列表内容相同，也必须重新确认选定仓库及分析代次。
        SelectedRepositoryIndex = -1;
        _revision++; InvalidateAnalysis();
        RecoveryRepositories.Clear(); _repositoryFolders.Clear();
        foreach (var folder in folders.Take(OnboardingOperationBudgets.RemoteBatch))
            if (folder.IndexOfAny(['/', '\\', ':']) < 0 && folder is not "." and not "..")
            {
                _repositoryFolders.Add(folder);
                RecoveryRepositories.Add(I18n.Format("CloudRecovery_RepositoryOption", _repositoryFolders.Count));
            }
        Message = I18n.GetString(folders.Length > OnboardingOperationBudgets.RemoteBatch ? "CloudSetup_DirectoriesTruncated" : "CloudRecovery_SelectRepository");
    }
    public async Task AnalyzeRepositoryAsync(CancellationToken token)
    {
        if (SelectedRepositoryIndex < 0 || SelectedRepositoryIndex >= RecoveryRepositories.Count) throw new InvalidOperationException(I18n.GetString("CloudRecovery_SelectRepository"));
        var revision = _revision;
        using var context = CreateContext();
        var folder = _repositoryFolders[SelectedRepositoryIndex];
        var json = await RcloneConnectionService.RunAsync(context.CreateStartInfo(["cat", CloudRemotePath.Append(context.RemoteRoot, "_folderrewind/history", folder, "repository.json")]), OnboardingOperationBudgets.RemoteBrowse, token);
        var descriptor = HistoryRepositoryDescriptor.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        if (HistoryRepositoryPaths.EncodeConfigPathSegment(descriptor.ConfigId) != folder) throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        var runtime = new HistoryRuntime(new FileHistoryRepository(descriptor.ConfigId,
            new HistoryRepositoryPaths(System.IO.Path.Combine(ConfigService.ConfigDirectory, "onboarding", "cloud-recovery", Guid.NewGuid().ToString("N")))));
        try
        {
            await runtime.InitializeAsync(token);
            var result = await new HistoryMetadataSyncService(runtime, new RcloneNativeHistoryTransport(context)).PullAsync(token);
            if (!result.Succeeded) throw new InvalidOperationException(CloudCommandSecurity.Redact(result.Diagnostic));
            var versions = await runtime.Query.GetAllVersionsAsync(token);
            if (revision != _revision || _disposed) { await runtime.DisposeAsync(); return; }
            if (_analysis is not null) await _analysis.DisposeAsync();
            _analysis = runtime; _analysisRevision = context.RevisionEvidence; _analysisFolder = folder;
            Versions.Clear(); _versions.Clear();
            foreach (var version in versions.OrderByDescending(v => v.CreatedAtUtc))
            { _versions.Add(version.VersionId); Versions.Add(version.SourceDescriptorSnapshot.DisplayName + " · " + UserDisplayFormatter.LongDateTime(version.CreatedAtUtc.LocalDateTime)); }
            Message = I18n.Format("CloudRecovery_HistorySummary", Versions.Count) + "\n" + descriptor.ConfigId;
        }
        catch { if (!ReferenceEquals(_analysis, runtime)) await runtime.DisposeAsync(); throw; }
    }
    public sealed record RecoveryConfirmation(HistoryRecoveryPreview Preview, long Revision, string ConnectionRevision,
        HistoryConfigId ConfigId, string RepositoryFolder);
    public async Task<RecoveryConfirmation> PreviewRecoveryAsync(CancellationToken token)
    {
        if (_analysis is null || SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count)
            throw new InvalidOperationException(I18n.GetString("CloudSetup_SelectVersion"));
        var revision = _revision;
        var runtime = _analysis;
        var folder = _analysisFolder;
        var selected = _versions[SelectedVersionIndex];
        using var context = CreateContext();
        if (_analysisRevision != context.RevisionEvidence) throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        var preview = await CloudReadOnlyRecoveryService.PreviewAsync(runtime, selected, token);
        if (_disposed || revision != _revision || SelectedVersionIndex < 0 || SelectedVersionIndex >= _versions.Count
            || _versions[SelectedVersionIndex] != selected) throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        if (!ReferenceEquals(_analysis, runtime) || folder is null || SelectedRepositoryIndex < 0
            || SelectedRepositoryIndex >= _repositoryFolders.Count || _repositoryFolders[SelectedRepositoryIndex] != folder)
            throw new InvalidOperationException(I18n.GetString("Export_IdentityChanged"));
        return new(preview, revision, context.RevisionEvidence, runtime.ConfigId, folder);
    }

    public static string DescribeRecovery(HistoryRecoveryPreview preview)
    {
        var text = I18n.Format("CloudRecovery_ReadablePreview", preview.Version.SourceDescriptorSnapshot.DisplayName,
            UserDisplayFormatter.LongDateTime(preview.Version.CreatedAtUtc.LocalDateTime), preview.MissingPayloads.Length);
        if (preview.DownloadBytes is { } bytes)
            text += "\n" + I18n.Format("CloudRecovery_DownloadSize", UserDisplayFormatter.Number(bytes / 1024d / 1024d, 1));
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
            || confirmation.ConfigId != _analysis.ConfigId || confirmation.RepositoryFolder != _analysisFolder
            || SelectedRepositoryIndex < 0 || SelectedRepositoryIndex >= _repositoryFolders.Count
            || _repositoryFolders[SelectedRepositoryIndex] != confirmation.RepositoryFolder
            || !confirmation.Preview.CanPrepare) throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        IsBusy = true; _restoring = true;
        var runtime = _analysis;
        _restoreFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _durableOperation = new();
        var task = new BackupTask { FolderName = I18n.GetString("CloudRecovery_Task"), IsIndeterminate = true, Status = I18n.GetString("CloudRecovery_Downloading") };
        BackupService.ActiveTasks.Add(task);
        try
        {
            await CloudReadOnlyRecoveryService.RestoreToNewLocationAsync(runtime, context, confirmation.Preview.Version.VersionId,
                destination, password, _durableOperation.Token, confirmation.Preview.Assessment.Selected!.RepresentationId,
                (id, complete) => EnqueueOnUiThread(() =>
                {
                    if (!task.IsCompleted) task.Status = I18n.GetString(complete ? "CloudRecovery_Verifying" : "CloudRecovery_Downloading");
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
            _restoreFinished.TrySetResult(true);
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
