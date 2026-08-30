using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.Models;
using FolderRewind.History.Application;
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
        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
            => RequestAsync(configId, folderId, BackupRequestOptions.Default, cancellationToken);

        public async ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            BackupRequestOptions options,
            CancellationToken cancellationToken)
        {
            if (NativeHostMutationContext.IsNestedMutationBlocked)
                return OperationOutcome.Blocked;
            ArgumentNullException.ThrowIfNull(options);
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value =>
                string.Equals(value.Id, configId, StringComparison.OrdinalIgnoreCase));
            if (config is null) return OperationOutcome.Blocked;
            var folders = folderId.HasValue
                ? config.SourceFolders.Where(folder => Guid.TryParse(folder.Id, out var id) && id == folderId.Value).ToArray()
                : config.SourceFolders.ToArray();
            if (folders.Length == 0) return OperationOutcome.Blocked;
            var result = await BackupService.BackupConfigurationForPluginAsync(
                config,
                folders,
                // 注释属于本次请求的 History 元数据，不能写入全局配置或跨请求复用。
                BackupInvocationOptions.ForInternal().WithComment(options.Comment),
                cancellationToken).ConfigureAwait(false);
            return result.Outcome;
        }
    }

    private sealed class RestoreRequests : IRestoreRequestService
    {
        public async ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string versionId,
            CancellationToken cancellationToken)
        {
            if (NativeHostMutationContext.IsNestedMutationBlocked)
                return OperationOutcome.Blocked;
            cancellationToken.ThrowIfCancellationRequested();
            var config = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(value =>
                string.Equals(value.Id, configId, StringComparison.OrdinalIgnoreCase));
            var folder = config?.SourceFolders.FirstOrDefault(value =>
                Guid.TryParse(value.Id, out var id) && id == folderId);
            if (config is null || folder is null) return OperationOutcome.Blocked;
            FolderRewind.History.Domain.VersionId parsed;
            try { parsed = FolderRewind.History.Domain.VersionId.Parse(versionId); }
            catch (FormatException) { return OperationOutcome.Blocked; }
            var result = await NativeHistoryApplicationService.RestoreVersionAsync(
                config,
                folder,
                parsed,
                BackupService.RestoreMode.Clean,
                cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? OperationOutcome.Success : OperationOutcome.Failed;
        }
    }

    private sealed class HistoryQuery : IHistoryQueryService
    {
        public async ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = NativeHistoryCoreGateway.GetRequiredRuntime(configId);
            var versions = await runtime.Query.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
            var sourceId = folderId is { } id && id != Guid.Empty
                ? new FolderRewind.History.Domain.SourceId(id)
                : (FolderRewind.History.Domain.SourceId?)null;
            var values = versions
                .Where(value => sourceId is null || value.SourceId == sourceId.Value)
                .Select(value => new HistoryVersionSnapshot(
                    value.VersionId.ToString(),
                    value.SourceId.Value,
                    value.SourceDescriptorSnapshot.PathHint,
                    string.Empty,
                    value.CreatedAtUtc,
                    OperationOutcome.Success))
                .ToArray();
            return values;
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
        var hadPreviousSettings = settings.TypedSettings.TryGetValue(commit.PluginId.Value, out var previousSettings);
        var stateRollbacks = new List<StateRollback>();
        try
        {
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
                var hadPreviousState = container.TryGetValue(patch.StateOwnerId.Value, out var previousState);
                stateRollbacks.Add(new StateRollback(
                    config,
                    container,
                    patch.StateOwnerId.Value,
                    hadPreviousState,
                    previousState,
                    config.ConfigRevision));
                container[patch.StateOwnerId.Value] = new ProviderStatePayload
                {
                    SchemaVersion = patch.SchemaVersion,
                    Data = patch.Data.Clone()
                };
                config.ConfigRevision = Guid.NewGuid().ToString("N");
            }

            var save = ConfigService.SaveWithResult();
            if (!save.Success)
                throw new IOException(save.ErrorMessage ?? "Plugin activation state could not be saved.");
            return ValueTask.CompletedTask;
        }
        catch
        {
            // 激活事务失败时恢复内存镜像，确保旧 session 与旧持久化状态仍然一致。
            if (hadPreviousSettings && previousSettings is not null)
                settings.TypedSettings[commit.PluginId.Value] = previousSettings;
            else
                settings.TypedSettings.Remove(commit.PluginId.Value);
            foreach (var rollback in stateRollbacks.AsEnumerable().Reverse())
            {
                if (rollback.HadPreviousState && rollback.PreviousState is not null)
                    rollback.Container[rollback.StateOwnerId] = rollback.PreviousState;
                else
                    rollback.Container.Remove(rollback.StateOwnerId);
                rollback.Config.ConfigRevision = rollback.ConfigRevision;
            }
            throw;
        }
    }

    private sealed record StateRollback(
        BackupConfig Config,
        Dictionary<string, ProviderStatePayload> Container,
        string StateOwnerId,
        bool HadPreviousState,
        ProviderStatePayload? PreviousState,
        string ConfigRevision);
}
