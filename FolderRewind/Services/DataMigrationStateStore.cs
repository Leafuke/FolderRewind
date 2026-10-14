using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed record DataMigrationProjectState(int FormatVersion, HistoryTransferProject Project,
    string SourceFileSha256, string ConfigImportId, bool RecoveryCompleted = false, string CompletedConfigSignature = "", string BundlePath = "");

internal static class DataMigrationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static string PathFor(string directory, HistoryConfigId id)
        => Path.Combine(HistoryRepositoryPaths.ForConfigDirectory(directory, id).LocalStateRoot, "migration-recovery.json");
    internal static DataMigrationProjectState? Load(string directory, HistoryConfigId id)
    {
        var path = PathFor(directory, id); if (!File.Exists(path)) return null;
        var state = JsonSerializer.Deserialize<DataMigrationProjectState>(File.ReadAllBytes(path), JsonOptions) ?? throw new InvalidDataException("Migration recovery state is empty.");
        if (state.FormatVersion != 1 || state.Project.ConfigId != id || state.ConfigImportId is null
            || state.ConfigImportId.Length > 0 && !Guid.TryParseExact(state.ConfigImportId, "N", out _))
            throw new InvalidDataException("Migration recovery state is incompatible.");
        return state;
    }
    internal static bool IsActive(string directory, DataMigrationProjectState state)
        => state.ConfigImportId.Length == 0 || new ConfigImportTransactionStore(Path.Combine(directory, "config.json")).PendingToken(state.Project.ConfigId.Value) == state.ConfigImportId;
    internal static void Save(string directory, DataMigrationProjectState state)
        => AtomicFileService.Write(PathFor(directory, state.Project.ConfigId), stream => JsonSerializer.Serialize(stream, state, JsonOptions));
}
