using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

internal sealed class ActivationHostServices : IPluginHostServices
{
    public ActivationHostServices(IPluginHostServices inner)
    {
        Configs = inner.Configs;
        Backups = inner.Backups;
        Restores = inner.Restores;
        History = inner.History;
        Notifications = inner.Notifications;
        KnotLink = inner.KnotLink;
        TemporaryStorage = inner.TemporaryStorage;
        Logger = inner.Logger;
    }

    public IReadOnlyConfigQueryService Configs { get; }
    public IBackupRequestService Backups { get; }
    public IRestoreRequestService Restores { get; }
    public IHistoryQueryService History { get; }
    public IPluginNotificationService Notifications { get; }
    public IKnotLinkHostService KnotLink { get; }
    public IPluginDataStore DataStore { get; } = new ActivationDataStore();
    public IPluginTemporaryStorage TemporaryStorage { get; }
    public IPluginLogger Logger { get; }

    private sealed class ActivationDataStore : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Plugin DataStore is unavailable until activation commits.");

        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Plugin DataStore is unavailable until activation commits.");
    }
}
