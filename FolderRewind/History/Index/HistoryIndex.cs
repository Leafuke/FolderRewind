using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace FolderRewind.History.Index;

public enum ReplicaAvailabilityObservation
{
    Unknown = 0,
    Available = 1,
    Missing = 2,
    Inaccessible = 3
}

public enum ReplicaIntegrityObservation
{
    Unknown = 0,
    Verified = 1,
    Corrupt = 2
}

public sealed record HistoryReplicaObservation(
    string ReplicaKey,
    ReplicaAvailabilityObservation Availability,
    ReplicaIntegrityObservation Integrity,
    DateTimeOffset ObservedAtUtc,
    string Evidence);

public sealed class HistoryIndex : IDisposable
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private const string Schema = """
        PRAGMA foreign_keys = OFF;
        CREATE TABLE IndexedPacks(PackId TEXT PRIMARY KEY, PayloadSha256 TEXT NOT NULL);
        CREATE TABLE Objects(Kind TEXT NOT NULL, ObjectId TEXT NOT NULL, SchemaVersion INTEGER NOT NULL, PayloadHash TEXT NOT NULL, PackId TEXT NOT NULL, IsSupported INTEGER NOT NULL, PayloadJson TEXT NOT NULL, PRIMARY KEY(Kind, ObjectId));
        CREATE TABLE Versions(VersionId TEXT PRIMARY KEY, ConfigId TEXT NOT NULL, SourceId TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, CaptureScope INTEGER NOT NULL, Outcome INTEGER NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE VersionParents(VersionId TEXT NOT NULL, ParentVersionId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(VersionId, Ordinal));
        CREATE TABLE Representations(RepresentationId TEXT PRIMARY KEY, VersionId TEXT NOT NULL, Kind INTEGER NOT NULL, Format TEXT NOT NULL, Fidelity INTEGER NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE RepresentationDependencies(RepresentationId TEXT NOT NULL, DependencyRepresentationId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(RepresentationId, Ordinal));
        CREATE TABLE Checkpoints(CheckpointId TEXT PRIMARY KEY, ConfigId TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, CreatedByRunId TEXT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE CheckpointSources(CheckpointId TEXT NOT NULL, SourceId TEXT NOT NULL, VersionId TEXT NULL, Disposition INTEGER NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(CheckpointId, SourceId));
        CREATE TABLE Runs(RunId TEXT PRIMARY KEY, ConfigId TEXT NOT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NOT NULL, Outcome INTEGER NOT NULL, ResultCheckpointId TEXT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE BranchUpdates(UpdateId TEXT PRIMARY KEY, BranchId TEXT NOT NULL, Name TEXT NOT NULL, TargetCheckpointId TEXT NULL, IsDeleted INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, Reason INTEGER NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE BranchUpdateParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
        CREATE TABLE BranchTips(BranchId TEXT NOT NULL, UpdateId TEXT NOT NULL, PRIMARY KEY(BranchId, UpdateId));
        CREATE TABLE Annotations(UpdateId TEXT PRIMARY KEY, TargetKind INTEGER NOT NULL, TargetId TEXT NOT NULL, AnnotationKind INTEGER NOT NULL, Value TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE AnnotationParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
        CREATE TABLE SharedReplicas(ReplicaId TEXT PRIMARY KEY, RepresentationId TEXT NOT NULL, ProviderKind INTEGER NOT NULL, ObjectKey TEXT NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE ReplicaLifecycle(UpdateId TEXT PRIMARY KEY, ReplicaId TEXT NOT NULL, State INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE ReplicaLifecycleParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
        CREATE TABLE MaterializationPolicies(UpdateId TEXT PRIMARY KEY, VersionId TEXT NOT NULL, State INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE MaterializationPolicyParents(UpdateId TEXT NOT NULL, ParentUpdateId TEXT NOT NULL, Ordinal INTEGER NOT NULL, PRIMARY KEY(UpdateId, Ordinal));
        CREATE TABLE MigrationRecords(RecordId TEXT PRIMARY KEY, PayloadJson TEXT NOT NULL);
        CREATE TABLE SafetySnapshots(SnapshotId TEXT PRIMARY KEY, CheckpointId TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL, Reason INTEGER NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE SafetySnapshotReleases(ReleaseId TEXT PRIMARY KEY, SnapshotId TEXT NOT NULL, ReleasedAtUtc TEXT NOT NULL, PayloadJson TEXT NOT NULL);
        CREATE TABLE ReplicaObservations(ReplicaKey TEXT PRIMARY KEY, Availability INTEGER NOT NULL, Integrity INTEGER NOT NULL, ObservedAtUtc TEXT NOT NULL, Evidence TEXT NOT NULL);
        CREATE INDEX IX_Versions_Source ON Versions(SourceId, CreatedAtUtc);
        CREATE INDEX IX_Representations_Version ON Representations(VersionId);
        CREATE INDEX IX_Checkpoints_Created ON Checkpoints(CreatedAtUtc);
        CREATE INDEX IX_BranchUpdates_Branch ON BranchUpdates(BranchId, CreatedAtUtc);
        """;

    private readonly string _indexPath;
    private readonly HistoryPackCodec _codec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public HistoryIndex(string indexPath, HistoryPackCodec? codec = null)
    {
        _indexPath = Path.GetFullPath(indexPath ?? throw new ArgumentNullException(nameof(indexPath)));
        _codec = codec ?? new HistoryPackCodec();
    }

    public string IndexPath => _indexPath;

    public async Task RebuildAsync(
        IReadOnlyList<HistoryPackReadResult> packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = string.Empty;
        try
        {
            var directory = Path.GetDirectoryName(_indexPath)
                ?? throw new InvalidOperationException("History index has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".history-index.{Guid.NewGuid():N}.tmp");
            BuildDatabase(temporaryPath, packs, cancellationToken);

            if (File.Exists(_indexPath))
            {
                File.Replace(temporaryPath, _indexPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _indexPath);
            }
        }
        finally
        {
            try
            {
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }

            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BranchUpdateId>> GetBranchTipsAsync(
        BranchId branchId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT UpdateId FROM BranchTips WHERE BranchId = $branchId ORDER BY UpdateId";
            command.Parameters.AddWithValue("$branchId", branchId.ToString());
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<BranchUpdateId>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(BranchUpdateId.Parse(reader.GetString(0)));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetObjectCountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Objects";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<SourceVersion>> GetVersionsForSourceAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<SourceVersion>(
            "SELECT PayloadJson FROM Versions WHERE SourceId = $source ORDER BY CreatedAtUtc DESC, VersionId DESC",
            [("$source", sourceId.ToString())],
            cancellationToken);

    public Task<IReadOnlyList<SourceVersion>> GetAllVersionsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<SourceVersion>(
            "SELECT PayloadJson FROM Versions ORDER BY CreatedAtUtc DESC, VersionId DESC",
            [],
            cancellationToken);

    public async Task<SourceVersion?> GetVersionAsync(
        VersionId versionId,
        CancellationToken cancellationToken = default)
        => (await ReadPayloadsAsync<SourceVersion>(
            "SELECT PayloadJson FROM Versions WHERE VersionId = $id",
            [("$id", versionId.ToString())],
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public async Task<ConfigurationCheckpoint?> GetCheckpointAsync(
        CheckpointId checkpointId,
        CancellationToken cancellationToken = default)
        => (await ReadPayloadsAsync<ConfigurationCheckpoint>(
            "SELECT PayloadJson FROM Checkpoints WHERE CheckpointId = $id",
            [("$id", checkpointId.ToString())],
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<ConfigurationCheckpoint>> GetAllCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<ConfigurationCheckpoint>(
            "SELECT PayloadJson FROM Checkpoints ORDER BY CreatedAtUtc DESC, CheckpointId DESC",
            [],
            cancellationToken);

    public Task<IReadOnlyList<BranchUpdate>> GetBranchTipsWithFactsAsync(
        BranchId branchId,
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<BranchUpdate>(
            """
            SELECT updateRow.PayloadJson
            FROM BranchTips tip
            JOIN BranchUpdates updateRow ON updateRow.UpdateId = tip.UpdateId
            WHERE tip.BranchId = $branchId
            ORDER BY updateRow.CreatedAtUtc DESC, updateRow.UpdateId DESC
            """,
            [("$branchId", branchId.ToString())],
            cancellationToken);

    public Task<IReadOnlyList<BranchUpdate>> GetAllBranchUpdatesAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<BranchUpdate>(
            "SELECT PayloadJson FROM BranchUpdates ORDER BY CreatedAtUtc, UpdateId",
            [],
            cancellationToken);

    public async Task<BranchUpdate?> GetBranchUpdateAsync(
        BranchUpdateId updateId,
        CancellationToken cancellationToken = default)
        => (await ReadPayloadsAsync<BranchUpdate>(
            "SELECT PayloadJson FROM BranchUpdates WHERE UpdateId = $id",
            [("$id", updateId.ToString())],
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<BackupRun>> GetRunsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<BackupRun>(
            "SELECT PayloadJson FROM Runs ORDER BY CompletedAtUtc DESC, RunId DESC",
            [],
            cancellationToken);

    public async Task<BackupRun?> GetRunAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
        => (await ReadPayloadsAsync<BackupRun>(
            "SELECT PayloadJson FROM Runs WHERE RunId = $id",
            [("$id", runId.ToString())],
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<VersionRepresentation>> GetRepresentationsAsync(
        VersionId versionId,
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<VersionRepresentation>(
            "SELECT PayloadJson FROM Representations WHERE VersionId = $version ORDER BY RepresentationId",
            [("$version", versionId.ToString())],
            cancellationToken);

    public async Task<VersionRepresentation?> GetRepresentationAsync(
        RepresentationId representationId,
        CancellationToken cancellationToken = default)
        => (await ReadPayloadsAsync<VersionRepresentation>(
            "SELECT PayloadJson FROM Representations WHERE RepresentationId = $id",
            [("$id", representationId.ToString())],
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<VersionRepresentation>> GetAllRepresentationsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<VersionRepresentation>(
            "SELECT PayloadJson FROM Representations ORDER BY RepresentationId",
            [],
            cancellationToken);

    public async Task<IReadOnlyList<MaterializationPolicyUpdate>> GetMaterializationPolicyTipsAsync(
        VersionId versionId,
        CancellationToken cancellationToken = default)
    {
        var updates = await ReadPayloadsAsync<MaterializationPolicyUpdate>(
            "SELECT PayloadJson FROM MaterializationPolicies WHERE VersionId = $version ORDER BY CreatedAtUtc, UpdateId",
            [("$version", versionId.ToString())],
            cancellationToken).ConfigureAwait(false);
        var parentIds = updates.SelectMany(update => update.ParentUpdateIds).ToHashSet();
        return updates.Where(update => !parentIds.Contains(update.UpdateId)).ToImmutableArray();
    }

    public Task<IReadOnlyList<HistoryAnnotationUpdate>> GetAnnotationUpdatesAsync(
        HistoryAnnotationTarget target,
        HistoryAnnotationKind? kind = null,
        CancellationToken cancellationToken = default)
        => kind is { } annotationKind
            ? ReadPayloadsAsync<HistoryAnnotationUpdate>(
                "SELECT PayloadJson FROM Annotations WHERE TargetKind = $targetKind AND TargetId = $targetId AND AnnotationKind = $kind ORDER BY CreatedAtUtc, UpdateId",
                [
                    ("$targetKind", (int)target.Kind),
                    ("$targetId", target.TargetId.ToString("N")),
                    ("$kind", (int)annotationKind)
                ],
                cancellationToken)
            : ReadPayloadsAsync<HistoryAnnotationUpdate>(
                "SELECT PayloadJson FROM Annotations WHERE TargetKind = $targetKind AND TargetId = $targetId ORDER BY AnnotationKind, CreatedAtUtc, UpdateId",
                [
                    ("$targetKind", (int)target.Kind),
                    ("$targetId", target.TargetId.ToString("N"))
                ],
                cancellationToken);

    public Task<IReadOnlyList<HistoryAnnotationUpdate>> GetAllAnnotationUpdatesAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<HistoryAnnotationUpdate>(
            "SELECT PayloadJson FROM Annotations ORDER BY TargetKind, TargetId, AnnotationKind, CreatedAtUtc, UpdateId",
            [],
            cancellationToken);

    public Task<IReadOnlyList<LegacyMigrationRecord>> GetMigrationRecordsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<LegacyMigrationRecord>(
            "SELECT PayloadJson FROM MigrationRecords ORDER BY RecordId",
            [],
            cancellationToken);

    public Task<IReadOnlyList<SafetySnapshot>> GetSafetySnapshotsAsync(
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<SafetySnapshot>(
            "SELECT PayloadJson FROM SafetySnapshots ORDER BY CreatedAtUtc, SnapshotId",
            [],
            cancellationToken);

    public Task<IReadOnlyList<SafetySnapshotRelease>> GetSafetySnapshotReleasesAsync(
        SafetySnapshotId? snapshotId = null,
        CancellationToken cancellationToken = default)
        => snapshotId is { } id
            ? ReadPayloadsAsync<SafetySnapshotRelease>(
                "SELECT PayloadJson FROM SafetySnapshotReleases WHERE SnapshotId = $id ORDER BY ReleasedAtUtc, ReleaseId",
                [("$id", id.ToString())],
                cancellationToken)
            : ReadPayloadsAsync<SafetySnapshotRelease>(
                "SELECT PayloadJson FROM SafetySnapshotReleases ORDER BY ReleasedAtUtc, ReleaseId",
                [],
                cancellationToken);

    public Task<IReadOnlyList<StorageReplica>> GetStorageReplicasAsync(
        RepresentationId representationId,
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<StorageReplica>(
            "SELECT PayloadJson FROM SharedReplicas WHERE RepresentationId = $id ORDER BY ReplicaId",
            [("$id", representationId.ToString())],
            cancellationToken);

    public Task<IReadOnlyList<ReplicaLifecycleUpdate>> GetReplicaLifecycleUpdatesAsync(
        ReplicaId replicaId,
        CancellationToken cancellationToken = default)
        => ReadPayloadsAsync<ReplicaLifecycleUpdate>(
            "SELECT PayloadJson FROM ReplicaLifecycle WHERE ReplicaId = $id ORDER BY CreatedAtUtc, UpdateId",
            [("$id", replicaId.ToString())],
            cancellationToken);

    public async Task<int> GetIndexedPackCountAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM IndexedPacks";
            return Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertObservationAsync(
        HistoryReplicaObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReplicaObservations(ReplicaKey, Availability, Integrity, ObservedAtUtc, Evidence)
                VALUES($key, $availability, $integrity, $observed, $evidence)
                ON CONFLICT(ReplicaKey) DO UPDATE SET
                    Availability=excluded.Availability,
                    Integrity=excluded.Integrity,
                    ObservedAtUtc=excluded.ObservedAtUtc,
                    Evidence=excluded.Evidence
                """;
            command.Parameters.AddWithValue("$key", observation.ReplicaKey);
            command.Parameters.AddWithValue("$availability", (int)observation.Availability);
            command.Parameters.AddWithValue("$integrity", (int)observation.Integrity);
            command.Parameters.AddWithValue("$observed", Utc(observation.ObservedAtUtc));
            command.Parameters.AddWithValue("$evidence", observation.Evidence);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<T>> ReadPayloadsAsync<T>(
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken)
        where T : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenExisting();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), PayloadJsonOptions)
                    ?? throw new HistoryRepositoryValidationException(
                        $"History index returned a null {typeof(T).Name} payload."));
            }

            return results.ToImmutableArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void BuildDatabase(
        string path,
        IReadOnlyList<HistoryPackReadResult> packs,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var indexedObjects = new HashSet<HistoryObjectKey>();
        foreach (var pack in packs.OrderBy(item => item.Pack.PackId.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection, transaction,
                "INSERT INTO IndexedPacks(PackId, PayloadSha256) VALUES($id, $hash)",
                ("$id", pack.Pack.PackId.ToString()),
                ("$hash", HistoryPackCodec.ComputeSha256(pack.OriginalBytes)));
            foreach (var item in pack.Pack.Objects)
            {
                var payloadJson = Encoding.UTF8.GetString(item.CanonicalPayload);
                Execute(connection, transaction,
                    "INSERT OR IGNORE INTO Objects(Kind, ObjectId, SchemaVersion, PayloadHash, PackId, IsSupported, PayloadJson) VALUES($kind, $id, $schema, $hash, $pack, $supported, $payload)",
                    ("$kind", item.Kind), ("$id", item.Id), ("$schema", item.SchemaVersion),
                    ("$hash", item.PayloadHash), ("$pack", pack.Pack.PackId.ToString()),
                    ("$supported", HistoryObjectKinds.IsKnown(item.Kind) && item.SchemaVersion == 1 ? 1 : 0),
                    ("$payload", payloadJson));
                if (!indexedObjects.Add(item.Key)
                    || !HistoryObjectKinds.IsKnown(item.Kind)
                    || item.SchemaVersion != 1)
                {
                    continue;
                }

                IndexKnownObject(connection, transaction, _codec.DeserializeKnown(item), payloadJson);
            }
        }

        Execute(connection, transaction, """
            INSERT INTO BranchTips(BranchId, UpdateId)
            SELECT updateRow.BranchId, updateRow.UpdateId
            FROM BranchUpdates updateRow
            WHERE NOT EXISTS(
                SELECT 1 FROM BranchUpdateParents parentRow
                WHERE parentRow.ParentUpdateId = updateRow.UpdateId)
            """);
        transaction.Commit();
        using var verify = connection.CreateCommand();
        verify.CommandText = "PRAGMA integrity_check";
        var result = Convert.ToString(verify.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!StringComparer.Ordinal.Equals(result, "ok"))
        {
            throw new HistoryRepositoryValidationException($"Rebuilt History index failed integrity_check: {result}");
        }
    }

    private static void IndexKnownObject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        object value,
        string payloadJson)
    {
        switch (value)
        {
            case SourceVersion item:
                Execute(connection, transaction,
                    "INSERT INTO Versions VALUES($id,$config,$source,$created,$scope,$outcome,$payload)",
                    ("$id", item.VersionId.ToString()), ("$config", item.ConfigId.Value),
                    ("$source", item.SourceId.ToString()), ("$created", Utc(item.CreatedAtUtc)),
                    ("$scope", (int)item.CaptureScope), ("$outcome", (int)item.Outcome), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "VersionParents", "VersionId", item.VersionId.ToString(), "ParentVersionId", item.ParentVersionIds.Select(id => id.ToString()));
                break;
            case VersionRepresentation item:
                Execute(connection, transaction,
                    "INSERT INTO Representations VALUES($id,$version,$kind,$format,$strategy,$payload)",
                    ("$id", item.RepresentationId.ToString()), ("$version", item.VersionId.ToString()),
                    ("$kind", (int)item.Kind), ("$format", item.Format),
                    ("$strategy", (int)item.Fidelity), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "RepresentationDependencies", "RepresentationId", item.RepresentationId.ToString(), "DependencyRepresentationId", item.DependencyRepresentationIds.Select(id => id.ToString()));
                break;
            case ConfigurationCheckpoint item:
                Execute(connection, transaction,
                    "INSERT INTO Checkpoints VALUES($id,$config,$created,$run,$payload)",
                    ("$id", item.CheckpointId.ToString()), ("$config", item.ConfigId.Value),
                    ("$created", Utc(item.CreatedAtUtc)), ("$run", item.CreatedByRunId?.ToString()),
                    ("$payload", payloadJson));
                for (var index = 0; index < item.Sources.Length; index++)
                {
                    var source = item.Sources[index];
                    Execute(connection, transaction,
                        "INSERT INTO CheckpointSources VALUES($checkpoint,$source,$version,$disposition,$ordinal)",
                        ("$checkpoint", item.CheckpointId.ToString()), ("$source", source.SourceId.ToString()),
                        ("$version", source.VersionId?.ToString()), ("$disposition", (int)source.Disposition),
                        ("$ordinal", index));
                }
                break;
            case BackupRun item:
                Execute(connection, transaction,
                    "INSERT INTO Runs VALUES($id,$config,$started,$completed,$outcome,$checkpoint,$payload)",
                    ("$id", item.RunId.ToString()), ("$config", item.ConfigId.Value),
                    ("$started", Utc(item.StartedAtUtc)), ("$completed", Utc(item.CompletedAtUtc)),
                    ("$outcome", (int)item.Outcome), ("$checkpoint", item.ResultCheckpointId?.ToString()),
                    ("$payload", payloadJson));
                break;
            case BranchUpdate item:
                Execute(connection, transaction,
                    "INSERT INTO BranchUpdates VALUES($id,$branch,$name,$target,$deleted,$created,$reason,$payload)",
                    ("$id", item.UpdateId.ToString()), ("$branch", item.BranchId.ToString()),
                    ("$name", item.Name), ("$target", item.TargetCheckpointId?.ToString()),
                    ("$deleted", item.IsDeleted ? 1 : 0), ("$created", Utc(item.CreatedAtUtc)),
                    ("$reason", (int)item.Reason), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "BranchUpdateParents", "UpdateId", item.UpdateId.ToString(), "ParentUpdateId", item.ParentUpdateIds.Select(id => id.ToString()));
                break;
            case HistoryAnnotationUpdate item:
                Execute(connection, transaction,
                    "INSERT INTO Annotations VALUES($id,$targetKind,$target,$kind,$value,$created,$payload)",
                    ("$id", item.UpdateId.ToString()), ("$targetKind", (int)item.Target.Kind),
                    ("$target", item.Target.TargetId.ToString("N")), ("$kind", (int)item.AnnotationKind),
                    ("$value", item.Value), ("$created", Utc(item.CreatedAtUtc)), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "AnnotationParents", "UpdateId", item.UpdateId.ToString(), "ParentUpdateId", item.ParentUpdateIds.Select(id => id.ToString()));
                break;
            case StorageReplica item:
                Execute(connection, transaction,
                    "INSERT INTO SharedReplicas VALUES($id,$representation,$provider,$objectKey,$payload)",
                    ("$id", item.ReplicaId.ToString()), ("$representation", item.RepresentationId.ToString()),
                    ("$provider", (int)item.ProviderKind), ("$objectKey", item.ObjectKey), ("$payload", payloadJson));
                break;
            case ReplicaLifecycleUpdate item:
                Execute(connection, transaction,
                    "INSERT INTO ReplicaLifecycle VALUES($id,$replica,$state,$created,$payload)",
                    ("$id", item.UpdateId.ToString()), ("$replica", item.ReplicaId.ToString()),
                    ("$state", (int)item.State), ("$created", Utc(item.CreatedAtUtc)), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "ReplicaLifecycleParents", "UpdateId", item.UpdateId.ToString(), "ParentUpdateId", item.ParentUpdateIds.Select(id => id.ToString()));
                break;
            case MaterializationPolicyUpdate item:
                Execute(connection, transaction,
                    "INSERT INTO MaterializationPolicies VALUES($id,$version,$state,$created,$payload)",
                    ("$id", item.UpdateId.ToString()), ("$version", item.VersionId.ToString()),
                    ("$state", (int)item.State), ("$created", Utc(item.CreatedAtUtc)), ("$payload", payloadJson));
                InsertEdges(connection, transaction, "MaterializationPolicyParents", "UpdateId", item.UpdateId.ToString(), "ParentUpdateId", item.ParentUpdateIds.Select(id => id.ToString()));
                break;
            case LegacyMigrationRecord item:
                Execute(connection, transaction,
                    "INSERT INTO MigrationRecords VALUES($id,$payload)",
                    ("$id", item.RecordId.ToString()), ("$payload", payloadJson));
                break;
            case SafetySnapshot item:
                Execute(connection, transaction,
                    "INSERT INTO SafetySnapshots VALUES($id,$checkpoint,$created,$reason,$payload)",
                    ("$id", item.SnapshotId.ToString()), ("$checkpoint", item.CheckpointId.ToString()),
                    ("$created", Utc(item.CreatedAtUtc)), ("$reason", (int)item.Reason),
                    ("$payload", payloadJson));
                break;
            case SafetySnapshotRelease item:
                Execute(connection, transaction,
                    "INSERT INTO SafetySnapshotReleases VALUES($id,$snapshot,$released,$payload)",
                    ("$id", item.ReleaseId.ToString()), ("$snapshot", item.SnapshotId.ToString()),
                    ("$released", Utc(item.ReleasedAtUtc)), ("$payload", payloadJson));
                break;
        }
    }

    private static void InsertEdges(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string ownerColumn,
        string ownerId,
        string targetColumn,
        IEnumerable<string> targets)
    {
        var ordinal = 0;
        foreach (var target in targets)
        {
            Execute(connection, transaction,
                $"INSERT INTO {table}({ownerColumn},{targetColumn},Ordinal) VALUES($owner,$target,$ordinal)",
                ("$owner", ownerId), ("$target", target), ("$ordinal", ordinal++));
        }
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenExisting()
    {
        if (!File.Exists(_indexPath))
        {
            throw new FileNotFoundException("History index is missing and must be rebuilt.", _indexPath);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _indexPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Utc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
