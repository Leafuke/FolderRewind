using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

internal sealed class ActivationHostServices : IPluginHostServices
{
    public ActivationHostServices(IPluginHostServices inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        // 激活仍处于未提交的 staging 阶段：只开放只读的 Config、History 与 Logger，
        // 其余 Host 服务使用独立的生命周期拒绝原因，避免留下不可回滚副作用。
        Configs = inner.Configs;
        History = inner.History;
        Logger = inner.Logger;
        Backups = new RejectedBackupRequests();
        Restores = new RejectedRestoreRequests();
        Notifications = new RejectedNotifications();
        KnotLink = new RejectedKnotLink();
        DataStore = new RejectedDataStore();
        TemporaryStorage = new RejectedTemporaryStorage();
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

    private static InvalidOperationException Rejected(HostServiceKind service)
        => new($"host_service.unavailable_during_activation: Host service '{service}' is unavailable until activation commits.");

    private sealed class RejectedBackupRequests : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.BackupRequest);

        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid? folderId,
            BackupRequestOptions options,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.BackupRequest);
    }

    private sealed class RejectedRestoreRequests : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestQuickAsync(
            string configId,
            Guid folderId,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.RestoreRequest);

        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string historyItemId,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.RestoreRequest);
    }

    private sealed class RejectedNotifications : IPluginNotificationService
    {
        public ValueTask ShowAsync(
            string title,
            string message,
            DiagnosticSeverity severity,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.Notification);
    }

    private sealed class RejectedKnotLink : IKnotLinkHostService
    {
        public bool IsAvailable => false;

        public ValueTask SendAsync(
            string eventName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.KnotLink);
    }

    private sealed class RejectedDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.DataStore);

        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.DataStore);
    }

    private sealed class RejectedTemporaryStorage : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.TemporaryStorage);
    }
}
