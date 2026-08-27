using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.LocalState;

public sealed class HistoryWorkspaceStore : IDisposable
{
    public const long MissingRevision = -1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryConfigId _configId;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public HistoryWorkspaceStore(HistoryConfigId configId, string path)
    {
        _configId = configId;
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    public Task<DeviceLocalStateLoadResult<HistoryWorkspace>> LoadAsync(
        CancellationToken cancellationToken = default)
        => WithGateAsync(() => Task.FromResult(LoadInsideGate()), cancellationToken);

    public Task SaveAsync(
        HistoryWorkspace workspace,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => WithGateAsync(
            () => SaveInsideGateAsync(workspace, expectedRevision, cancellationToken),
            cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private DeviceLocalStateLoadResult<HistoryWorkspace> LoadInsideGate()
    {
        if (!File.Exists(_path))
        {
            return new(DeviceLocalStateStatus.Missing, null, "Workspace state is missing; disk state is unknown.");
        }

        try
        {
            var bytes = File.ReadAllBytes(_path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.GetProperty("formatVersion").GetInt32() != HistoryWorkspace.CurrentFormatVersion)
            {
                return new(DeviceLocalStateStatus.Corrupt, null, "Workspace format is unsupported.");
            }

            var workspace = JsonSerializer.Deserialize<HistoryWorkspace>(bytes, JsonOptions)
                ?? throw new JsonException("Workspace document is null.");
            Validate(workspace);
            return new(DeviceLocalStateStatus.Valid, workspace, string.Empty);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DeviceLocalStateStatus.Inaccessible, null, ex.Message);
        }
        catch (IOException ex)
        {
            return new(DeviceLocalStateStatus.Inaccessible, null, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            return new(DeviceLocalStateStatus.Corrupt, null, ex.Message);
        }
    }

    private async Task SaveInsideGateAsync(
        HistoryWorkspace workspace,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Validate(workspace);
        var current = LoadInsideGate();
        if (current.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
        {
            throw new DeviceLocalStateConflictException(
                "Workspace cannot be overwritten until its recovery state is resolved.");
        }

        var currentRevision = current.Value?.StateRevision ?? MissingRevision;
        var desiredBytes = JsonSerializer.SerializeToUtf8Bytes(workspace, JsonOptions);
        if (currentRevision == workspace.StateRevision
            && File.Exists(_path)
            && File.ReadAllBytes(_path).AsSpan().SequenceEqual(desiredBytes))
        {
            return;
        }

        if (currentRevision != expectedRevision || workspace.StateRevision != checked(expectedRevision + 1))
        {
            throw new DeviceLocalStateConflictException(
                $"Workspace revision changed: expected {expectedRevision}, actual {currentRevision}, desired {workspace.StateRevision}.");
        }

        await AtomicFileService.WriteAsync(
            _path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, workspace, JsonOptions, token),
            cancellationToken).ConfigureAwait(false);
    }

    private void Validate(HistoryWorkspace workspace)
    {
        if (workspace.ConfigId != _configId)
        {
            throw new InvalidDataException("Workspace Config identity does not match its repository.");
        }

        if (workspace.SourceBaselines.GroupBy(item => item.SourceId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Workspace contains duplicate Source baselines.");
        }
    }

    private async Task<T> WithGateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithGateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class LocalReplicaCatalogStore : IDisposable
{
    public const long MissingRevision = -1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly HistoryConfigId _configId;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LocalReplicaCatalogStore(HistoryConfigId configId, string path)
    {
        _configId = configId;
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    public async Task<DeviceLocalStateLoadResult<LocalReplicaCatalog>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadInsideGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        LocalReplicaCatalog catalog,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(catalog);
            Validate(catalog);
            var current = LoadInsideGate();
            if (current.Status is DeviceLocalStateStatus.Corrupt or DeviceLocalStateStatus.Inaccessible)
            {
                throw new DeviceLocalStateConflictException(
                    "Local Replica Catalog cannot be overwritten until recovery is resolved.");
            }

            var currentRevision = current.Value?.CatalogRevision ?? MissingRevision;
            var desiredBytes = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
            if (currentRevision == catalog.CatalogRevision
                && File.Exists(_path)
                && File.ReadAllBytes(_path).AsSpan().SequenceEqual(desiredBytes))
            {
                return;
            }

            if (currentRevision != expectedRevision || catalog.CatalogRevision != checked(expectedRevision + 1))
            {
                throw new DeviceLocalStateConflictException(
                    $"Catalog revision changed: expected {expectedRevision}, actual {currentRevision}, desired {catalog.CatalogRevision}.");
            }

            await AtomicFileService.WriteAsync(
                _path,
                (stream, token) => JsonSerializer.SerializeAsync(stream, catalog, JsonOptions, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private DeviceLocalStateLoadResult<LocalReplicaCatalog> LoadInsideGate()
    {
        if (!File.Exists(_path))
        {
            return new(DeviceLocalStateStatus.Missing, null, "Local Replica Catalog is missing; payload locations are unknown.");
        }

        try
        {
            var bytes = File.ReadAllBytes(_path);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.GetProperty("formatVersion").GetInt32() != LocalReplicaCatalog.CurrentFormatVersion)
            {
                return new(DeviceLocalStateStatus.Corrupt, null, "Local Replica Catalog format is unsupported.");
            }

            var catalog = JsonSerializer.Deserialize<LocalReplicaCatalog>(bytes, JsonOptions)
                ?? throw new JsonException("Local Replica Catalog is null.");
            Validate(catalog);
            return new(DeviceLocalStateStatus.Valid, catalog, string.Empty);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new(DeviceLocalStateStatus.Inaccessible, null, ex.Message);
        }
        catch (IOException ex)
        {
            return new(DeviceLocalStateStatus.Inaccessible, null, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or KeyNotFoundException)
        {
            return new(DeviceLocalStateStatus.Corrupt, null, ex.Message);
        }
    }

    private void Validate(LocalReplicaCatalog catalog)
    {
        if (catalog.ConfigId != _configId)
        {
            throw new InvalidDataException("Local Replica Catalog Config identity does not match its repository.");
        }

        if (catalog.Entries.GroupBy(item => item.LocalReplicaId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Local Replica Catalog contains duplicate LocalReplicaId values.");
        }
    }
}
