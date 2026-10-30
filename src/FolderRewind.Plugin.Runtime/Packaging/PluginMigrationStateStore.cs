using System.Text.Json;
using System.Text.Json.Serialization;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Packaging;

public enum PluginMigrationStatus
{
    InProgress = 0,
    Completed = 1,
    RecoveryRequired = 2,
    Suppressed = 3
}

public sealed record PluginMigrationState(
    int SchemaVersion,
    PluginId PluginId,
    PluginMigrationStatus Status,
    string Phase,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool? PreservedEnabledIntent = null,
    string InstalledVersion = "",
    string QuarantinePath = "",
    string DiagnosticCode = "",
    string DiagnosticMessage = "");

public sealed class PluginMigrationStateStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PluginMigrationStateStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string GetStatePath(PluginId pluginId)
        => Path.Combine(_root, pluginId.Value + ".v2-v3.json");

    public async ValueTask<PluginMigrationState?> ReadAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        var path = GetStatePath(pluginId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<PluginMigrationState>(stream, _json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(
        PluginMigrationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = GetStatePath(state.PluginId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, _json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            var readBack = await ReadAsync(state.PluginId, cancellationToken).ConfigureAwait(false);
            if (readBack is null || readBack != state)
                throw new InvalidDataException("Plugin migration state read-back verification failed.");
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
            }
            throw;
        }
    }

    public ValueTask DeleteAsync(
        PluginId pluginId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetStatePath(pluginId);
        if (File.Exists(path)) File.Delete(path);
        return ValueTask.CompletedTask;
    }
}
