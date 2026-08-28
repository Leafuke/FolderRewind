using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

public sealed class PluginHostServiceAccessException : InvalidOperationException
{
    public const string DiagnosticCode = "host_service.not_declared";

    public PluginHostServiceAccessException(HostServiceKind service)
        : base($"{DiagnosticCode}: Manifest does not declare Host service '{service}'.")
    {
        Service = service;
    }

    public HostServiceKind Service { get; }
}

public sealed class DeclaredPluginHostServices : IPluginHostServices
{
    public DeclaredPluginHostServices(
        IPluginHostServices inner,
        IEnumerable<HostServiceKind> declaredServices)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(declaredServices);
        var declared = declaredServices.ToHashSet();
        Configs = declared.Contains(HostServiceKind.ConfigQuery)
            ? inner.Configs
            : new RejectedConfigQuery();
        Backups = declared.Contains(HostServiceKind.BackupRequest)
            ? inner.Backups
            : new RejectedBackupRequests();
        Restores = declared.Contains(HostServiceKind.RestoreRequest)
            ? inner.Restores
            : new RejectedRestoreRequests();
        History = declared.Contains(HostServiceKind.HistoryQuery)
            ? inner.History
            : new RejectedHistoryQuery();
        Notifications = declared.Contains(HostServiceKind.Notification)
            ? inner.Notifications
            : new RejectedNotifications();
        KnotLink = declared.Contains(HostServiceKind.KnotLink)
            ? inner.KnotLink
            : new RejectedKnotLink();
        DataStore = declared.Contains(HostServiceKind.DataStore)
            ? inner.DataStore
            : new RejectedDataStore();
        TemporaryStorage = declared.Contains(HostServiceKind.TemporaryStorage)
            ? inner.TemporaryStorage
            : new RejectedTemporaryStorage();
        Logger = declared.Contains(HostServiceKind.Logging)
            ? inner.Logger
            : new RejectedLogger();
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

    private static PluginHostServiceAccessException Rejected(HostServiceKind service) => new(service);

    private sealed class RejectedConfigQuery : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.ConfigQuery);

        public ValueTask<IReadOnlyList<ConfigSnapshot>> QueryAsync(
            ConfigKindRef? kind,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.ConfigQuery);
    }

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
        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string versionId,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.RestoreRequest);
    }

    private sealed class RejectedHistoryQuery : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
            => throw Rejected(HostServiceKind.HistoryQuery);
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

    private sealed class RejectedLogger : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null)
            => throw Rejected(HostServiceKind.Logging);
    }
}
