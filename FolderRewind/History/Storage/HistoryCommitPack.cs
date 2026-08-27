using FolderRewind.History.Domain;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace FolderRewind.History.Storage;

public static class HistoryObjectKinds
{
    public const string SourceVersion = "sourceVersion";
    public const string ConfigurationCheckpoint = "configurationCheckpoint";
    public const string VersionRepresentation = "versionRepresentation";
    public const string StorageReplica = "storageReplica";
    public const string ReplicaLifecycleUpdate = "replicaLifecycleUpdate";
    public const string BranchUpdate = "branchUpdate";
    public const string BackupRun = "backupRun";
    public const string HistoryAnnotationUpdate = "historyAnnotationUpdate";
    public const string MaterializationPolicyUpdate = "materializationPolicyUpdate";

    public static bool IsKnown(string kind)
        => kind is SourceVersion
            or ConfigurationCheckpoint
            or VersionRepresentation
            or StorageReplica
            or ReplicaLifecycleUpdate
            or BranchUpdate
            or BackupRun
            or HistoryAnnotationUpdate
            or MaterializationPolicyUpdate;
}

public readonly record struct HistoryObjectKey(string Kind, string Id);

public sealed record HistoryPackObject
{
    public HistoryPackObject(
        string kind,
        int schemaVersion,
        string id,
        string payloadHash,
        byte[] canonicalPayload)
    {
        Kind = string.IsNullOrWhiteSpace(kind)
            ? throw new ArgumentException("Object kind cannot be empty.", nameof(kind))
            : kind;
        SchemaVersion = schemaVersion > 0
            ? schemaVersion
            : throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("Object id cannot be empty.", nameof(id))
            : id;
        PayloadHash = payloadHash?.ToLowerInvariant()
            ?? throw new ArgumentNullException(nameof(payloadHash));
        CanonicalPayload = canonicalPayload is null
            ? throw new ArgumentNullException(nameof(canonicalPayload))
            : (byte[])canonicalPayload.Clone();
    }

    public string Kind { get; }
    public int SchemaVersion { get; }
    public string Id { get; }
    public string PayloadHash { get; }
    public byte[] CanonicalPayload { get; }
    public HistoryObjectKey Key => new(Kind, Id);
}

public sealed record HistoryCommitPack
{
    public const int CurrentFormatVersion = 1;

    public HistoryCommitPack(
        PackId packId,
        HistoryTransactionId transactionId,
        DateTimeOffset createdAtUtc,
        IEnumerable<HistoryPackObject> objects)
    {
        PackId = packId;
        TransactionId = transactionId;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Objects = objects is null ? [] : [.. objects];
        if (Objects.IsEmpty)
        {
            throw new ArgumentException("A history commit pack must contain at least one object.", nameof(objects));
        }
    }

    public int FormatVersion => CurrentFormatVersion;
    public PackId PackId { get; }
    public HistoryTransactionId TransactionId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public ImmutableArray<HistoryPackObject> Objects { get; }
}

public sealed record HistoryPackReadResult(
    HistoryCommitPack Pack,
    byte[] OriginalBytes,
    bool ContainsUnsupportedObjectSchema);

public enum HistoryPackInstallDisposition
{
    Installed = 0,
    Duplicate = 1
}

public sealed record HistoryPackInstallResult(
    PackId PackId,
    HistoryPackInstallDisposition Disposition,
    bool CompatibilityBlocked);

public class HistoryRepositoryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class HistoryIntegrityConflictException(string message)
    : HistoryRepositoryException(message);

public sealed class HistoryPackCompatibilityException(string message)
    : HistoryRepositoryException(message);

public sealed class HistoryRepositoryValidationException(string message)
    : HistoryRepositoryException(message);
