using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Services.Plugins.V3;

internal sealed class PluginV3HostServices : IPluginHostServices
{
    public PluginV3HostServices(PluginId pluginId, string dataRoot, string temporaryRoot)
    {
        Configs = new ConfigQuery();
        Backups = new BackupRequests();
        Restores = new RestoreRequests();
        History = new HistoryQuery();
        Notifications = new NotificationsService();
        KnotLink = new KnotLinkServiceAdapter();
        DataStore = new FileDataStore(Path.Combine(dataRoot, pluginId.Value));
        TemporaryStorage = new TemporaryStorageService(Path.Combine(temporaryRoot, pluginId.Value));
        Logger = new LoggerService(pluginId);
    }

    public IReadOnlyConfigQueryService Configs { get; }
    public IBackupRequestService Backups { get; }
    public IRestoreRequestService Restores { get; }
    public IHistoryQueryService History { get; }
    public IPluginNotificationService Notifications { get; }
    public IKnotLinkHostService KnotLink { get; }
    public IPluginDataStore DataStore { get; }
    public IPluginTemporaryStorage TemporaryStorage { get; }
    public IPluginLogger Logger { get; }

    private sealed class ConfigQuery : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value =>
                string.Equals(value.Id, configId, StringComparison.OrdinalIgnoreCase));
            return ValueTask.FromResult(config is null ? null : PluginV3ModelMapper.ToSnapshot(config));
        }

        public ValueTask<IReadOnlyList<ConfigSnapshot>> QueryAsync(
            ConfigKindRef? kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = ConfigService.CurrentConfig.BackupConfigs
                .Where(value => !kind.HasValue || PluginV3ModelMapper.ToKind(value) == kind.Value)
                .Select(PluginV3ModelMapper.ToSnapshot)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<ConfigSnapshot>>(values);
        }
    }

    private sealed class BackupRequests : IBackupRequestService
    {
        public async ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
        {
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value =>
                string.Equals(value.Id, configId, StringComparison.OrdinalIgnoreCase));
            if (config is null) return OperationOutcome.Blocked;
            var folders = folderId.HasValue
                ? config.SourceFolders.Where(folder => Guid.TryParse(folder.Id, out var id) && id == folderId.Value).ToArray()
                : config.SourceFolders.ToArray();
            if (folders.Length == 0) return OperationOutcome.Blocked;
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var success = await BackupService.BackupFolderAsync(
                    config,
                    folder,
                    BackupInvocationOptions.ForInternal());
                if (!success) return OperationOutcome.NoChanges;
            }
            return OperationOutcome.Success;
        }
    }

    private sealed class RestoreRequests : IRestoreRequestService
    {
        public async ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string historyItemId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value =>
                string.Equals(value.Id, configId, StringComparison.OrdinalIgnoreCase));
            var folder = config?.SourceFolders.FirstOrDefault(value =>
                Guid.TryParse(value.Id, out var id) && id == folderId);
            var history = HistoryService.TryGetEntryById(historyItemId);
            if (config is null || folder is null || history is null) return OperationOutcome.Blocked;
            var success = await BackupService.RestoreBackupAsync(
                config,
                folder,
                history,
                BackupService.RestoreMode.Overwrite);
            return success ? OperationOutcome.Success : OperationOutcome.Failed;
        }
    }

    private sealed class HistoryQuery : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryItemSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = HistoryService.GetEntriesForConfig(configId)
                .Where(value => !folderId.HasValue || value.FolderId == folderId)
                .Select(value => new HistoryItemSnapshot(
                    value.Id,
                    value.FolderId,
                    value.FolderPath,
                    value.FileName,
                    value.Timestamp,
                    value.Outcome switch
                    {
                        PersistedOperationOutcome.SuccessWithWarnings => OperationOutcome.SuccessWithWarnings,
                        PersistedOperationOutcome.NoChanges => OperationOutcome.NoChanges,
                        PersistedOperationOutcome.Canceled => OperationOutcome.Canceled,
                        PersistedOperationOutcome.Blocked => OperationOutcome.Blocked,
                        PersistedOperationOutcome.Failed => OperationOutcome.Failed,
                        _ => OperationOutcome.Success
                    }))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<HistoryItemSnapshot>>(values);
        }
    }

    private sealed class NotificationsService : IPluginNotificationService
    {
        public ValueTask ShowAsync(
            string title,
            string message,
            DiagnosticSeverity severity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.IsNullOrWhiteSpace(title) ? message : $"{title}: {message}";
            if (severity == DiagnosticSeverity.Error) NotificationService.ShowError(text);
            else if (severity == DiagnosticSeverity.Warning) NotificationService.ShowWarning(text);
            else NotificationService.ShowInfo(text);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class KnotLinkServiceAdapter : IKnotLinkHostService
    {
        public bool IsAvailable => Services.KnotLinkService.IsSenderRunning;

        public async ValueTask SendAsync(
            string eventName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sent = await Services.KnotLinkService.TryBroadcastEventAsync(
                context: null,
                eventName,
                arguments.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
            if (!sent) throw new InvalidOperationException("KnotLink did not accept the plugin event.");
        }
    }

    private sealed class FileDataStore : IPluginDataStore
    {
        private readonly string _root;
        public FileDataStore(string root) => _root = Path.GetFullPath(root);

        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => ValueTask.FromResult<Stream>(new FileStream(
                Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true));

        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return ValueTask.FromResult<Stream>(new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true));
        }

        private string Resolve(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Plugin data paths must be non-empty and relative.");
            var path = Path.GetFullPath(Path.Combine(_root, relativePath));
            var prefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Plugin data path escapes its owner directory.");
            return path;
        }
    }

    private sealed class TemporaryStorageService : IPluginTemporaryStorage
    {
        private readonly string _root;
        public TemporaryStorageService(string root) => _root = Path.GetFullPath(root);
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return ValueTask.FromResult(path);
        }
    }

    private sealed class LoggerService(PluginId pluginId) : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null)
            => LogService.Log(
                $"[PluginV3:{pluginId}] {message}{(exception is null ? string.Empty : " " + exception)}",
                severity == DiagnosticSeverity.Error
                    ? LogLevel.Error
                    : severity == DiagnosticSeverity.Warning ? LogLevel.Warning : LogLevel.Info);
    }
}

internal sealed class PluginV3ActivationStore : IPluginActivationStore
{
    public ValueTask CommitAsync(PluginActivationCommit commit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = ConfigService.CurrentConfig.GlobalSettings.Plugins;
        settings.TypedSettings[commit.PluginId.Value] = commit.Settings.Values
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        foreach (var patch in commit.ProviderStatePatches)
        {
            var config = ConfigService.CurrentConfig.BackupConfigs.Single(value =>
                string.Equals(value.Id, patch.Location.ConfigId, StringComparison.OrdinalIgnoreCase));
            var container = patch.Location.FolderId.HasValue
                ? config.SourceFolders.Single(folder =>
                    Guid.TryParse(folder.Id, out var id) && id == patch.Location.FolderId.Value).ProviderStates
                : config.ProviderStates;
            container[patch.StateOwnerId.Value] = new ProviderStatePayload
            {
                SchemaVersion = patch.SchemaVersion,
                Data = patch.Data.Clone()
            };
            config.ConfigRevision = Guid.NewGuid().ToString("N");
        }
        ConfigService.Save();
        return ValueTask.CompletedTask;
    }
}
