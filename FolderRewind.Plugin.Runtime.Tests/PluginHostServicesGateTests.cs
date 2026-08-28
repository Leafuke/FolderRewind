using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Activation;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginHostServicesGateTests
{
    private static readonly HostServiceKind[] FormalServices =
    [
        HostServiceKind.ConfigQuery,
        HostServiceKind.BackupRequest,
        HostServiceKind.RestoreRequest,
        HostServiceKind.HistoryQuery,
        HostServiceKind.Notification,
        HostServiceKind.KnotLink,
        HostServiceKind.DataStore,
        HostServiceKind.TemporaryStorage,
        HostServiceKind.Logging
    ];

    [TestMethod]
    public async Task DeclaredServicesDelegateAndUndeclaredServicesRejectWithStableCode()
    {
        foreach (var service in FormalServices)
        {
            var inner = new RecordingHostServices();
            var declared = new DeclaredPluginHostServices(inner, [service]);
            await InvokeAsync(service, declared);
            Assert.AreEqual(1, inner.Calls[service], $"Declared service {service} was not delegated.");

            var rejected = new DeclaredPluginHostServices(new RecordingHostServices(), []);
            var error = await Assert.ThrowsExactlyAsync<PluginHostServiceAccessException>(
                () => InvokeAsync(service, rejected));
            Assert.AreEqual(service, error.Service);
            StringAssert.Contains(error.Message, PluginHostServiceAccessException.DiagnosticCode);
        }
    }

    [TestMethod]
    public void UndeclaredKnotLinkReportsUnavailableWithoutCallingTheHost()
    {
        var inner = new RecordingHostServices();
        var services = new DeclaredPluginHostServices(inner, []);

        Assert.IsFalse(services.KnotLink.IsAvailable);
        Assert.AreEqual(0, inner.Calls[HostServiceKind.KnotLink]);
    }

    private static async Task InvokeAsync(HostServiceKind service, IPluginHostServices host)
    {
        switch (service)
        {
            case HostServiceKind.ConfigQuery:
                await host.Configs.FindAsync("config", CancellationToken.None);
                break;
            case HostServiceKind.BackupRequest:
                await host.Backups.RequestAsync("config", null, CancellationToken.None);
                break;
            case HostServiceKind.RestoreRequest:
                await host.Restores.RequestAsync("config", Guid.NewGuid(), "history", CancellationToken.None);
                break;
            case HostServiceKind.HistoryQuery:
                await host.History.QueryAsync("config", null, CancellationToken.None);
                break;
            case HostServiceKind.Notification:
                await host.Notifications.ShowAsync("title", "message", DiagnosticSeverity.Information, CancellationToken.None);
                break;
            case HostServiceKind.KnotLink:
                _ = host.KnotLink.IsAvailable;
                await host.KnotLink.SendAsync("event", new Dictionary<string, string>(), CancellationToken.None);
                break;
            case HostServiceKind.DataStore:
                await using (var stream = await host.DataStore.OpenReadAsync("state", CancellationToken.None)) { }
                break;
            case HostServiceKind.TemporaryStorage:
                await host.TemporaryStorage.CreateDirectoryAsync(CancellationToken.None);
                break;
            case HostServiceKind.Logging:
                host.Logger.Log(DiagnosticSeverity.Information, "message");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(service));
        }
    }

    private sealed class RecordingHostServices : IPluginHostServices
    {
        public RecordingHostServices()
        {
            foreach (var service in FormalServices) Calls[service] = 0;
            Configs = new ConfigQuery(this);
            Backups = new BackupRequests(this);
            Restores = new RestoreRequests(this);
            History = new HistoryQuery(this);
            Notifications = new Notifications(this);
            KnotLink = new KnotLink(this);
            DataStore = new DataStore(this);
            TemporaryStorage = new TemporaryStorage(this);
            Logger = new Logger(this);
        }

        public Dictionary<HostServiceKind, int> Calls { get; } = new();
        public IReadOnlyConfigQueryService Configs { get; }
        public IBackupRequestService Backups { get; }
        public IRestoreRequestService Restores { get; }
        public IHistoryQueryService History { get; }
        public IPluginNotificationService Notifications { get; }
        public IKnotLinkHostService KnotLink { get; }
        public IPluginDataStore DataStore { get; }
        public IPluginTemporaryStorage TemporaryStorage { get; }
        public IPluginLogger Logger { get; }
        public void Hit(HostServiceKind service) => Calls[service]++;
    }

    private sealed class ConfigQuery(RecordingHostServices owner) : IReadOnlyConfigQueryService
    {
        public ValueTask<ConfigSnapshot?> FindAsync(string configId, CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.ConfigQuery);
            return ValueTask.FromResult<ConfigSnapshot?>(null);
        }
    }

    private sealed class BackupRequests(RecordingHostServices owner) : IBackupRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(string configId, Guid? folderId, CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.BackupRequest);
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class RestoreRequests(RecordingHostServices owner) : IRestoreRequestService
    {
        public ValueTask<OperationOutcome> RequestAsync(
            string configId,
            Guid folderId,
            string versionId,
            CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.RestoreRequest);
            return ValueTask.FromResult(OperationOutcome.Success);
        }
    }

    private sealed class HistoryQuery(RecordingHostServices owner) : IHistoryQueryService
    {
        public ValueTask<IReadOnlyList<HistoryVersionSnapshot>> QueryAsync(
            string configId,
            Guid? folderId,
            CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.HistoryQuery);
            return ValueTask.FromResult<IReadOnlyList<HistoryVersionSnapshot>>([]);
        }
    }

    private sealed class Notifications(RecordingHostServices owner) : IPluginNotificationService
    {
        public ValueTask ShowAsync(
            string title,
            string message,
            DiagnosticSeverity severity,
            CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.Notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class KnotLink(RecordingHostServices owner) : IKnotLinkHostService
    {
        public bool IsAvailable
        {
            get
            {
                owner.Hit(HostServiceKind.KnotLink);
                return true;
            }
        }

        public ValueTask SendAsync(
            string eventName,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class DataStore(RecordingHostServices owner) : IPluginDataStore
    {
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.DataStore);
            return ValueTask.FromResult<Stream>(new MemoryStream());
        }

        public ValueTask<Stream> OpenWriteAsync(string relativePath, CancellationToken cancellationToken)
            => OpenReadAsync(relativePath, cancellationToken);
    }

    private sealed class TemporaryStorage(RecordingHostServices owner) : IPluginTemporaryStorage
    {
        public ValueTask<string> CreateDirectoryAsync(CancellationToken cancellationToken)
        {
            owner.Hit(HostServiceKind.TemporaryStorage);
            return ValueTask.FromResult("temporary");
        }
    }

    private sealed class Logger(RecordingHostServices owner) : IPluginLogger
    {
        public void Log(DiagnosticSeverity severity, string message, Exception? exception = null)
            => owner.Hit(HostServiceKind.Logging);
    }
}
