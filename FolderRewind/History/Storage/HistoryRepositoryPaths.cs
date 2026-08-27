using FolderRewind.History.Domain;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.History.Storage;

public sealed class HistoryRepositoryPaths
{
    public HistoryRepositoryPaths(string repositoryRoot)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot)));
        DescriptorPath = Path.Combine(RepositoryRoot, "repository.json");
        PacksRoot = Path.Combine(RepositoryRoot, "packs");
        IndexRoot = Path.Combine(RepositoryRoot, "index");
        LocalStateRoot = Path.Combine(RepositoryRoot, "local-state");
        TransactionsRoot = Path.Combine(RepositoryRoot, "transactions");
        QuarantineRoot = Path.Combine(RepositoryRoot, "quarantine");
    }

    public string RepositoryRoot { get; }
    public string DescriptorPath { get; }
    public string PacksRoot { get; }
    public string IndexRoot { get; }
    public string LocalStateRoot { get; }
    public string TransactionsRoot { get; }
    public string QuarantineRoot { get; }

    public static HistoryRepositoryPaths ForConfigDirectory(string configDirectory, HistoryConfigId configId)
        => new(Path.Combine(
            Path.GetFullPath(configDirectory ?? throw new ArgumentNullException(nameof(configDirectory))),
            "history",
            EncodeConfigPathSegment(configId)));

    public static string EncodeConfigPathSegment(HistoryConfigId configId)
    {
        if (Guid.TryParse(configId.Value, out var guid) && guid != Guid.Empty)
        {
            return guid.ToString("N", CultureInfo.InvariantCulture);
        }

        return "c-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configId.Value)))
            .ToLowerInvariant();
    }

    public string GetPackPath(PackId packId)
    {
        var id = packId.ToString();
        return Path.Combine(PacksRoot, id[..2], id + ".frpack");
    }

    public string GetTransactionDirectory(HistoryTransactionId transactionId)
        => Path.Combine(TransactionsRoot, transactionId.ToString());

    public string GetJournalPath(HistoryTransactionId transactionId)
        => Path.Combine(GetTransactionDirectory(transactionId), "journal.json");

    public string GetQuarantinePath(PackId? packId)
    {
        var id = packId?.ToString() ?? "unknown";
        return Path.Combine(
            QuarantineRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{id}-{Guid.NewGuid():N}.frpack");
    }

    public static string CreateReplicaObjectKey(ReplicaId replicaId)
        => $"replicas/{replicaId}/payload";

    public static bool IsSafeRepositoryRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    public void CreateDirectories()
    {
        Directory.CreateDirectory(RepositoryRoot);
        Directory.CreateDirectory(PacksRoot);
        Directory.CreateDirectory(IndexRoot);
        Directory.CreateDirectory(LocalStateRoot);
        Directory.CreateDirectory(TransactionsRoot);
        Directory.CreateDirectory(QuarantineRoot);
    }
}

