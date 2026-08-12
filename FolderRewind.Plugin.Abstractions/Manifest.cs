namespace FolderRewind.Plugin.Abstractions;

public enum HostServiceKind
{
    ConfigQuery = 0,
    BackupRequest = 1,
    RestoreRequest = 2,
    HistoryQuery = 3,
    Notification = 4,
    KnotLink = 5,
    DataStore = 6,
    TemporaryStorage = 7,
    Logging = 8
}

public sealed record PluginManifestContract(
    PluginId PluginId,
    string Version,
    PluginApiVersion RequiredApi,
    string EntryAssembly,
    string EntryType,
    IReadOnlyList<HostServiceKind> RequestedHostServices);
