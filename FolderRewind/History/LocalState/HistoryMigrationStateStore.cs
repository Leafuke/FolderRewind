using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.LocalState;

internal sealed record HistoryMigrationState(int FormatVersion, HistoryConfigId ConfigId,
    bool InitializationPending, ImmutableArray<HistoryLocalStateIntent> Intents, string ImportId = "");

/// <summary>Only an explicit transfer marks missing local state as normal migration setup.</summary>
internal sealed class HistoryMigrationStateStore(HistoryConfigId configId, string localStateRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string StatePath => Path.Combine(localStateRoot, "migration.json");
    internal HistoryMigrationState? Load()
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<HistoryMigrationState>(File.ReadAllBytes(StatePath), JsonOptions)
            ?? throw new InvalidDataException("Migration state is empty.");
        if (state.FormatVersion != 1 || state.ConfigId != configId || state.Intents.IsDefault || state.ImportId is null
            || (state.ImportId.Length > 0 && !Guid.TryParseExact(state.ImportId, "N", out _)))
            throw new InvalidDataException("Migration state is incompatible.");
        return state;
    }
    internal void MarkImported(bool initializationPending = true)
    {
        if (Load() is null) Save(new(1, configId, initializationPending, [], initializationPending ? Guid.NewGuid().ToString("N") : ""));
    }
    internal void RequireConfirmation()
    {
        if (Load() is { } state) Save(state with { InitializationPending = true });
    }
    internal void RequireRecovered()
    {
        if (Load() is { InitializationPending: true, Intents.Length: > 0 })
            throw new DeviceLocalStateConflictException("Migration local-state initialization requires recovery.");
    }
    internal void Prepare(ImmutableArray<HistoryLocalStateIntent> intents) => Save(new(1, configId, true, intents, PermissionToken()));
    internal async Task RecoverAsync(HistoryLocalStateJournalRecovery recovery, CancellationToken token)
    {
        var state = Load();
        if (state is not { InitializationPending: true, Intents.Length: > 0 }) return;
        var envelope = HistoryTransactionJournal.Prepared(HistoryTransactionId.New(), PackId.New(), state.Intents);
        await recovery.ApplyCommittedStateAsync(envelope, token).ConfigureAwait(false);
        Save(state with { InitializationPending = false, Intents = [], ImportId = PermissionToken() });
    }
    internal void ConfirmInitialized()
    {
        if (Load() is { } state) Save(state with { InitializationPending = false, Intents = [], ImportId = PermissionToken() });
    }
    internal string PermissionToken()
    {
        var state = Load();
        if (state is null) return "";
        if (state.ImportId.Length > 0) return state.ImportId;
        if (!state.InitializationPending) return "";
        // Stable compatibility identity survives replay of an older pending migration.
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("migration:" + configId.Value));
        return new Guid(hash.AsSpan(0, 16)).ToString("N");
    }
    private void Save(HistoryMigrationState state) => AtomicFileService.Write(StatePath,
        stream => JsonSerializer.Serialize(stream, state, JsonOptions));
}
