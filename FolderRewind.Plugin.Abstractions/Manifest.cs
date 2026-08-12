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

public enum PluginCapabilityKind
{
    Discovery = 0,
    ConfigReconciliation = 1,
    FilePolicy = 2,
    BackupScope = 3,
    BackupConsistency = 4,
    FolderMetadata = 5,
    RestoreCoordinator = 6,
    PluginCommand = 7,
    KnotLinkIntegration = 8,
    ProviderStateMigration = 9
}

public sealed record LocalizedText(
    string Default,
    IReadOnlyDictionary<string, string> Translations);

public sealed record PluginManifestContract(
    PluginId PluginId,
    string Version,
    PluginApiVersion RequiredApi,
    string EntryAssembly,
    string EntryType,
    LocalizedText Name,
    LocalizedText Description,
    IReadOnlyList<ConfigKindDeclaration> ConfigKinds,
    string SettingsSchema,
    IReadOnlyList<HostServiceKind> RequestedHostServices,
    IReadOnlyList<PluginCapabilityKind> Capabilities);

public sealed record ConfigKindDeclaration(
    ConfigKindRef Kind,
    LocalizedText DisplayName,
    LocalizedText Description,
    string Icon,
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
