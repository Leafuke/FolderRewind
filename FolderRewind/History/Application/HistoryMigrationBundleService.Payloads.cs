using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

internal sealed record HistoryMigrationPayloadJournal(int FormatVersion, string TransferId,
    HistoryConfigId ConfigId, string BackupRoot, HistoryTransferProjectMigration Migration,
    ImmutableArray<string> StagedFiles, bool Installed = false);

public sealed partial class HistoryMigrationBundleService
{
    internal Action<string>? PayloadCheckpoint { get; set; }

    public async Task InstallPayloadsAsync(string path, HistoryMigrationBundlePreview preview, HistoryConfigId id,
        string configDirectory, string backupRoot, CancellationToken token = default)
    {
        var fresh = await PreviewAsync(path, token).ConfigureAwait(false);
        if (fresh.History.FileSha256 != preview.History.FileSha256) throw new IOException("Migration changed after preview.");
        if (!fresh.IncludesPayloads) return;
        var project = fresh.History.Projects.Single(p => p.ConfigId == id);
        var root = Path.GetFullPath(backupRoot);
        var session = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(fresh.TransferId + ":" + root.ToUpperInvariant())).AsSpan(0, 16)).ToString("N");
        var journalPath = PayloadJournalPath(configDirectory, id, session);
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id, token).ConfigureAwait(false);
        if (File.Exists(journalPath)) { await RecoverPayloadJournalAsync(journalPath, id, root, token).ConfigureAwait(false); return; }
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Migration backup root cannot be a filesystem link.");
        var migration = project.Migration!;
        var missing = new List<HistoryTransferPayloadFile>();
        foreach (var payload in migration.PayloadFiles)
        {
            var destination = HistoryReplicaRecoveryService.ResolveUnderRoot(root, payload.RelativePath);
            if (File.Exists(destination)) { await VerifyPayloadAsync(root, destination, payload, token).ConfigureAwait(false); continue; }
            if (Directory.Exists(destination)) throw new IOException("Migration payload collides with an existing directory.");
            missing.Add(payload);
        }
        long required = missing.Aggregate(0L, (total, file) => checked(total + file.Size));
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < required + 16L * 1024 * 1024) throw new IOException("Insufficient free space to stage migration payloads.");
        var staging = PayloadStagingRoot(root, session);
        if (Directory.Exists(staging)) CleanupPayloadStaging(root, staging);
        EnsureDirectory(root, staging);
        var decided = false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var file in missing)
            {
                var staged = HistoryReplicaRecoveryService.ResolveUnderRoot(staging, file.RelativePath);
                EnsureDirectory(root, Path.GetDirectoryName(staged)!);
                HistoryReplicaRecoveryService.RequireRegularPath(root, Path.GetDirectoryName(staged)!);
                await using var input = zip.GetEntry(file.EntryName)!.Open();
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, 65536, token).ConfigureAwait(false);
                await VerifyPayloadAsync(root, staged, file, token).ConfigureAwait(false);
            }
            var journal = new HistoryMigrationPayloadJournal(1, session, id, root, migration, [.. missing.Select(f => f.RelativePath)]);
            token.ThrowIfCancellationRequested();
            AtomicFileService.Write(journalPath, stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
            decided = true; PayloadCheckpoint?.Invoke("prepared");
            await RecoverPayloadJournalAsync(journalPath, id, root, CancellationToken.None).ConfigureAwait(false);
        }
        finally { if (!decided) CleanupPayloadStaging(root, staging); }
    }

    public async Task RecoverPayloadsAsync(string configDirectory, HistoryConfigId id, string backupRoot, CancellationToken token = default)
    {
        var directory = Path.Combine(HistoryRepositoryPaths.ForConfigDirectory(configDirectory, id).LocalStateRoot, "migration-payloads");
        if (!Directory.Exists(directory)) return;
        await using var operation = await NativeHistoryConfigurationOperationGate.EnterHistoryAsync(id, token).ConfigureAwait(false);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var header = JsonSerializer.Deserialize<HistoryMigrationPayloadJournal>(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Migration payload journal is empty.");
            if (Path.GetFullPath(header.BackupRoot).Equals(Path.GetFullPath(backupRoot), StringComparison.OrdinalIgnoreCase))
                await RecoverPayloadJournalAsync(path, id, Path.GetFullPath(backupRoot), token).ConfigureAwait(false);
        }
    }

    private async Task RecoverPayloadJournalAsync(string journalPath, HistoryConfigId expectedId, string expectedRoot, CancellationToken token)
    {
        var journal = JsonSerializer.Deserialize<HistoryMigrationPayloadJournal>(await File.ReadAllBytesAsync(journalPath, token).ConfigureAwait(false), JsonOptions)
            ?? throw new InvalidDataException("Migration payload journal is empty.");
        if (journal.FormatVersion != 1 || journal.ConfigId != expectedId || !Guid.TryParseExact(journal.TransferId, "N", out _)
            || !Path.GetFullPath(journal.BackupRoot).Equals(expectedRoot, StringComparison.OrdinalIgnoreCase) || journal.StagedFiles.IsDefault)
            throw new InvalidDataException("Migration payload journal no longer matches the target project.");
        if (journal.Installed) return;
        var root = expectedRoot; var staging = PayloadStagingRoot(root, journal.TransferId);
        foreach (var file in journal.Migration.PayloadFiles)
        {
            ValidateRelativePath(file.RelativePath);
            var destination = HistoryReplicaRecoveryService.ResolveUnderRoot(root, file.RelativePath);
            if (File.Exists(destination)) { await VerifyPayloadAsync(root, destination, file, token).ConfigureAwait(false); continue; }
            if (!journal.StagedFiles.Contains(file.RelativePath)) throw new IOException("A previously existing migration payload disappeared.");
            var staged = HistoryReplicaRecoveryService.ResolveUnderRoot(staging, file.RelativePath);
            await VerifyPayloadAsync(root, staged, file, token).ConfigureAwait(false);
            EnsureDirectory(root, Path.GetDirectoryName(destination)!);
            HistoryReplicaRecoveryService.RequireRegularPath(root, Path.GetDirectoryName(destination)!);
            File.Move(staged, destination, false); PayloadCheckpoint?.Invoke("payload-installed");
        }
        foreach (var directory in journal.Migration.PayloadDirectories.IsDefault ? [] : journal.Migration.PayloadDirectories)
        {
            ValidateRelativePath(directory); var path = HistoryReplicaRecoveryService.ResolveUnderRoot(root, directory);
            EnsureDirectory(root, path);
        }
        if (journal.Migration.ArtifactClosure is { } closure)
        {
            var payloads = closure.Artifacts.ToDictionary(a => a.ArtifactId, a =>
            {
                var path = HistoryReplicaRecoveryService.ResolveUnderRoot(root, a.ContentRelativePath);
                return new CloudArtifactPayload(path, File.Exists(path));
            });
            await new FileArtifactLedgerStore(root).ImportCloudClosureAsync(closure, payloads, token).ConfigureAwait(false);
        }
        AtomicFileService.Write(journalPath, stream => JsonSerializer.Serialize(stream, journal with { Installed = true }, JsonOptions));
        CleanupPayloadStaging(root, staging);
    }
    private static async Task VerifyPayloadAsync(string root, string path, HistoryTransferPayloadFile expected, CancellationToken token)
    {
        HistoryReplicaRecoveryService.RequireRegularPath(root, path);
        var facts = await HistoryReplicaRecoveryService.ReadFileFactsAsync(path, token).ConfigureAwait(false);
        if (facts.Size != expected.Size || facts.Hash != expected.StorageSha256) throw new IOException("Migration payload conflicts with existing bytes or failed integrity verification.");
    }
    private static string PayloadJournalPath(string directory, HistoryConfigId id, string transferId)
        => Path.Combine(HistoryRepositoryPaths.ForConfigDirectory(directory, id).LocalStateRoot, "migration-payloads", transferId + ".json");
    private static void EnsureDirectory(string root, string path)
    {
        var existing = path;
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("Migration target has no existing parent.");
        HistoryReplicaRecoveryService.RequireRegularPath(root, existing);
        Directory.CreateDirectory(path); HistoryReplicaRecoveryService.RequireRegularPath(root, path);
    }
    private static string PayloadStagingRoot(string root, string transferId)
        => Path.GetFullPath(Path.Combine(root, ".folderrewind", "migration-staging", transferId));
    private static void CleanupPayloadStaging(string root, string staging)
    {
        var managed = Path.GetFullPath(Path.Combine(root, ".folderrewind", "migration-staging"));
        if (!HistoryReplicaRecoveryService.IsUnderRoot(managed, staging)) throw new IOException("Migration cleanup target is outside its staging directory.");
        if (Directory.Exists(staging)) { HistoryReplicaRecoveryService.RequireRegularPath(root, staging); Directory.Delete(staging, true); }
    }
}
