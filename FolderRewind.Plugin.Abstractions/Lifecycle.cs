namespace FolderRewind.Plugin.Abstractions;

public interface IFolderRewindPlugin
{
    ValueTask<PluginActivationResult> ActivateAsync(
        IPluginActivationContext context,
        CancellationToken cancellationToken);

    ValueTask DeactivateAsync(CancellationToken cancellationToken);
}

public interface IPluginActivationContext
{
    PluginId PluginId { get; }
    PluginSettingsSnapshot Settings { get; }
    IReadOnlyList<ConfigSnapshot> Configs { get; }
    void RegisterCapability<TCapability>(TCapability capability)
        where TCapability : class, IPluginCapability;
}

public sealed record PluginInvocationContext(
    PluginId PluginId,
    IPluginHostServices HostServices,
    CancellationToken OperationCancellation,
    CancellationToken PluginLifetime);

public interface IPluginCapability;

public interface IPluginHostServices
{
    IReadOnlyConfigQueryService Configs { get; }
    IBackupRequestService Backups { get; }
    IRestoreRequestService Restores { get; }
    IHistoryQueryService History { get; }
    IPluginNotificationService Notifications { get; }
    IKnotLinkHostService KnotLink { get; }
    IPluginDataStore DataStore { get; }
    IPluginTemporaryStorage TemporaryStorage { get; }
    IPluginLogger Logger { get; }
}

public interface IReadOnlyConfigQueryService
{
    ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ConfigSnapshot>> QueryAsync(ConfigKindRef? kind, CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<ConfigSnapshot>>(Array.Empty<ConfigSnapshot>());
}

public interface IBackupRequestService
{
    ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken);

    ValueTask<OperationOutcome> RequestAsync(
        string configId,
        Guid? folderId,
        BackupRequestOptions options,
        CancellationToken cancellationToken)
        => RequestAsync(configId, folderId, cancellationToken);
}

public sealed record BackupRequestOptions
{
    /// <summary>Per-request history comment. The Host remains responsible for validation and persistence.</summary>
    public string Comment { get; init; } = string.Empty;

    public static BackupRequestOptions Default { get; } = new();
}

public interface IRestoreRequestService
{
    ValueTask<OperationOutcome> RequestAsync(string configId, Guid folderId, string versionId, CancellationToken cancellationToken);
}

public interface IHistoryQueryService
{
    ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(string configId, Guid? folderId, CancellationToken cancellationToken);
}

public sealed record HistoryVersionSnapshot(
    string VersionId,
    Guid SourceId,
    string PathHint,
    string ArchiveFileName,
    DateTimeOffset CreatedAt,
    OperationOutcome Outcome);

public interface IPluginNotificationService
{
    ValueTask ShowAsync(string title, string message, DiagnosticSeverity severity, CancellationToken cancellationToken);
}

public interface IKnotLinkHostService
{
    bool IsAvailable { get; }
    ValueTask SendAsync(string eventName, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken);
}

public interface IPluginDataStore
{
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken);
}

public interface IPluginTemporaryStorage
{
    ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken);
}

public interface IPluginLogger
{
    void Log(DiagnosticSeverity severity, string message, Exception? exception = null);
}
