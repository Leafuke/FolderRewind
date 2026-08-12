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
    IReadOnlyList<ConfigKindDeclaration> ConfigKinds,
    string SettingsSchema,
    IReadOnlyList<HostServiceKind> RequestedHostServices);

public sealed record ConfigKindDeclaration(
    ConfigKindRef Kind,
    string DisplayName,
    BackupFallbackPolicy BackupFallback,
    RestoreCoordinationPolicy RestoreCoordination);

public enum BackupFallbackPolicy
{
    Block = 0,
    RawWithWarnings = 1
}

public enum RestoreCoordinationPolicy
{
    None = 0,
    Required = 1
}
