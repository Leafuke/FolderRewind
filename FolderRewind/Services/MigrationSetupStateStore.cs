using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using System;
using System.IO;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed record MigrationPermission(int FormatVersion, string ImportId, bool Backup, bool Cloud);

internal static class MigrationSetupStateStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static bool IsPending(string directory, string id) => IsBackupPending(directory, id);
    internal static bool IsBackupPending(string directory, string id) => IsPendingSafely(directory, id, false);
    internal static bool IsCloudPending(string directory, string id) => IsPendingSafely(directory, id, true);
    private static bool IsPendingSafely(string directory, string id, bool cloud)
    {
        try { return IsCapabilityPending(directory, id, cloud); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return true; } // A damaged project record must not stop other projects' scheduler snapshots.
    }
    internal static void Validate(string directory, string id)
    {
        if (Token(directory, id) is not null) _ = Read(directory, id);
    }
    private static bool IsCapabilityPending(string directory, string id, bool cloud)
    {
        var token = Token(directory, id);
        if (token is not null)
        {
            var permissions = Read(directory, id);
            if (permissions is not null)
            {
                if (permissions.ImportId != token || !(cloud ? permissions.Cloud : permissions.Backup)) return true;
            }
            else
            {
                // Preserve confirmations issued by the first migration implementation.
                var confirmed = PathFor(directory, id, ".confirmed");
                if (!File.Exists(confirmed) || File.ReadAllText(confirmed) != token) return true;
            }
        }
        var paths = HistoryRepositoryPaths.ForConfigDirectory(directory, new HistoryConfigId(id));
        return new HistoryMigrationStateStore(new HistoryConfigId(id), paths.LocalStateRoot).Load()?.InitializationPending == true;
    }
    internal static void Confirm(string directory, string id) => Grant(directory, id, true, true);
    internal static void Grant(string directory, string id, bool backup, bool cloud)
    {
        lock (Sync)
        {
            var token = Token(directory, id);
            if (token is null) return;
            var previous = Read(directory, id);
            if (previous is null)
            {
                var legacy = PathFor(directory, id, ".confirmed");
                var confirmed = File.Exists(legacy) && File.ReadAllText(legacy) == token;
                previous = new(2, token, confirmed, confirmed);
            }
            if (previous.ImportId != token) previous = new(2, token, false, false);
            Write(directory, id, previous with { Backup = previous.Backup || backup, Cloud = previous.Cloud || cloud });
        }
    }
    internal static void Revoke(string directory, string id, bool backup, bool cloud)
    {
        lock (Sync)
        {
            var token = Token(directory, id);
            if (token is null) return;
            var previous = Read(directory, id) ?? new(2, token, !IsBackupPending(directory, id), !IsCloudPending(directory, id));
            if (previous.ImportId != token) previous = new(2, token, false, false);
            Write(directory, id, previous with { Backup = previous.Backup && !backup, Cloud = previous.Cloud && !cloud });
        }
    }
    internal static bool HasRequirement(string directory, string id) => Token(directory, id) is not null;
    private static string? Token(string directory, string id)
    {
        var configToken = new ConfigImportTransactionStore(Path.Combine(directory, "config.json")).PendingToken(id);
        if (configToken is not null) return configToken;
        var paths = HistoryRepositoryPaths.ForConfigDirectory(directory, new HistoryConfigId(id));
        var historyToken = new HistoryMigrationStateStore(new(id), paths.LocalStateRoot).PermissionToken();
        return historyToken.Length > 0 ? historyToken : null;
    }
    private static MigrationPermission? Read(string directory, string id)
    {
        var path = PathFor(directory, id, ".permissions.json");
        if (!File.Exists(path)) return null;
        var value = JsonSerializer.Deserialize<MigrationPermission>(File.ReadAllBytes(path), Options);
        if (value is null || value.FormatVersion != 2 || !Guid.TryParseExact(value.ImportId, "N", out _))
            throw new InvalidDataException("Invalid migration permission record.");
        return value;
    }
    private static void Write(string directory, string id, MigrationPermission value)
        => AtomicFileService.Write(PathFor(directory, id, ".permissions.json"), stream => JsonSerializer.Serialize(stream, value, Options));
    private static string PathFor(string directory, string id, string suffix) => Path.Combine(directory, "migration", "projects",
        HistoryRepositoryPaths.EncodeConfigPathSegment(new HistoryConfigId(id)) + suffix);
}
