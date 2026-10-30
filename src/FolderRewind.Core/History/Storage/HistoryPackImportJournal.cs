using FolderRewind.History.Domain;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace FolderRewind.History.Storage;

internal enum HistoryPackImportPhase { Prepared, CommitDecided, Complete }
internal sealed record HistoryPackImportEntry(string PackId, string Sha256);
internal sealed record HistoryPackImportJournal(int FormatVersion, HistoryConfigId ConfigId,
    HistoryPackImportPhase Phase, HistoryPackImportEntry[] Packs);

/// <summary>A durable union decision. Staged bytes remain owned until every final file is verified.</summary>
internal sealed class HistoryPackImportJournalStore(HistoryConfigId configId, HistoryRepositoryPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string Root => Path.Combine(paths.RepositoryRoot, "imports");
    internal Action<string, int>? FaultInjector { get; set; }

    internal void Install(IReadOnlyList<HistoryPackReadResult> packs, CancellationToken token)
    {
        if (packs.Count == 0) return;
        if (packs.Count == 1)
        {
            // A normal Commit Pack's file presence remains its commit decision;
            // its existing local-state journal must not be superseded by a union intent.
            token.ThrowIfCancellationRequested();
            var pack = packs[0];
            FileHistoryRepository.WriteCreateOnce(paths.GetPackPath(pack.Pack.PackId), pack.OriginalBytes);
            Verify(paths.GetPackPath(pack.Pack.PackId), new(pack.Pack.PackId.ToString(), HistoryPackCodec.ComputeSha256(pack.OriginalBytes)));
            return;
        }
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = new HistoryPackImportJournal(1, configId, HistoryPackImportPhase.Prepared,
            packs.Select(p => new HistoryPackImportEntry(p.Pack.PackId.ToString(),
                HistoryPackCodec.ComputeSha256(p.OriginalBytes))).ToArray());
        var decided = false;
        try
        {
            foreach (var pack in packs)
            {
                token.ThrowIfCancellationRequested();
                var staged = StagePath(directory, pack.Pack.PackId);
                AtomicFileService.Write(staged, stream => stream.Write(pack.OriginalBytes));
                Verify(staged, journal.Packs.Single(p => p.PackId == pack.Pack.PackId.ToString()));
            }
            Save(directory, journal);
            FaultInjector?.Invoke("Prepared", 0);
            token.ThrowIfCancellationRequested();
            journal = journal with { Phase = HistoryPackImportPhase.CommitDecided };
            Save(directory, journal);
            decided = true;
            FaultInjector?.Invoke("CommitDecided", 0);
            Complete(directory, journal);
        }
        finally
        {
            // Before a union decision there are no authoritative files to undo.
            if (!decided) Cleanup(directory);
        }
    }

    internal void Recover()
    {
        foreach (var directory in Directories())
        {
            var journalPath = Path.Combine(directory, "import.json");
            if (!File.Exists(journalPath)) { Cleanup(directory); continue; }
            var journal = Load(directory);
            if (journal.Phase == HistoryPackImportPhase.CommitDecided) Complete(directory, journal);
            else if (journal.Phase == HistoryPackImportPhase.Prepared) Cleanup(directory);
            else
            {
                try { Cleanup(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal void RequireRecovered()
    {
        foreach (var directory in Directories())
            if (File.Exists(Path.Combine(directory, "import.json"))
                && Load(directory).Phase == HistoryPackImportPhase.CommitDecided)
                throw new HistoryPackImportRecoveryRequiredException(
                    "A committed Pack union requires recovery before reading or changing the repository.");
    }

    private void Complete(string directory, HistoryPackImportJournal journal)
    {
        // Verify the entire retained input before installing any remaining files.
        foreach (var entry in journal.Packs) Verify(StagePath(directory, PackId.Parse(entry.PackId)), entry);
        var count = 0;
        foreach (var entry in journal.Packs)
        {
            var id = PackId.Parse(entry.PackId);
            var final = paths.GetPackPath(id);
            if (!File.Exists(final))
                FileHistoryRepository.WriteCreateOnce(final, File.ReadAllBytes(StagePath(directory, id)));
            Verify(final, entry);
            FaultInjector?.Invoke("PackInstalled", ++count);
        }
        Save(directory, journal with { Phase = HistoryPackImportPhase.Complete });
        try { Cleanup(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Complete journal is retained for later cleanup. */ }
    }

    private HistoryPackImportJournal Load(string directory)
    {
        var journal = JsonSerializer.Deserialize<HistoryPackImportJournal>(
            File.ReadAllBytes(Path.Combine(directory, "import.json")), JsonOptions)
            ?? throw new InvalidDataException("Pack import journal is empty.");
        if (journal.FormatVersion != 1 || journal.ConfigId != configId || !Enum.IsDefined(journal.Phase)
            || journal.Packs is null || journal.Packs.Length == 0
            || journal.Packs.Select(p => p.PackId).Distinct(StringComparer.Ordinal).Count() != journal.Packs.Length)
            throw new InvalidDataException("Pack import journal is incompatible or invalid.");
        foreach (var entry in journal.Packs) _ = PackId.Parse(entry.PackId);
        return journal;
    }

    private static void Verify(string path, HistoryPackImportEntry entry)
    {
        if (!File.Exists(path) || !StringComparer.Ordinal.Equals(
                HistoryPackCodec.ComputeSha256(File.ReadAllBytes(path)), entry.Sha256))
            throw new HistoryIntegrityConflictException($"Import Pack {entry.PackId} failed durable verification.");
    }
    private static string StagePath(string directory, PackId id) => Path.Combine(directory, id + ".frpack");
    private static void Save(string directory, HistoryPackImportJournal journal)
        => AtomicFileService.Write(Path.Combine(directory, "import.json"),
            stream => JsonSerializer.Serialize(stream, journal, JsonOptions));
    private IEnumerable<string> Directories()
    {
        if (!Directory.Exists(Root)) return [];
        var directories = Directory.GetDirectories(Root);
        foreach (var directory in directories)
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Import staging directory is not an owned transaction directory.");
        return directories;
    }
    private static void Cleanup(string directory)
    {
        // Only a direct, internally generated import directory is ever removed.
        if (!Directory.Exists(directory)) return;
        if (Directory.GetDirectories(directory).Length != 0)
            throw new InvalidDataException("Import staging directory contains unexpected subdirectories.");
        foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
}

public sealed class HistoryPackImportRecoveryRequiredException(string message) : InvalidOperationException(message);
