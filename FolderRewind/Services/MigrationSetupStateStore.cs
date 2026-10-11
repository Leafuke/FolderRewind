using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System.IO;

namespace FolderRewind.Services;

internal static class MigrationSetupStateStore
{
    internal static bool IsPending(string configDirectory, string configId)
    {
        var token = new ConfigImportTransactionStore(Path.Combine(configDirectory, "config.json")).PendingToken(configId);
        var confirmed = ConfirmationPath(configDirectory, configId);
        if (token is not null && (!File.Exists(confirmed) || File.ReadAllText(confirmed) != token)) return true;
        var paths = HistoryRepositoryPaths.ForConfigDirectory(configDirectory, new HistoryConfigId(configId));
        return new HistoryMigrationStateStore(new HistoryConfigId(configId), paths.LocalStateRoot).Load()?.InitializationPending == true;
    }
    internal static void Confirm(string configDirectory, string configId)
    {
        var token = new ConfigImportTransactionStore(Path.Combine(configDirectory, "config.json")).PendingToken(configId);
        if (token is not null) AtomicFileService.Write(ConfirmationPath(configDirectory, configId), stream =>
        {
            using var writer = new StreamWriter(stream, leaveOpen: true); writer.Write(token); writer.Flush();
        });
    }
    private static string ConfirmationPath(string directory, string configId) => Path.Combine(directory, "migration", "projects",
        HistoryRepositoryPaths.EncodeConfigPathSegment(new HistoryConfigId(configId)) + ".confirmed");
}
