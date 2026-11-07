using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FolderRewind.Services;

internal sealed partial class RcloneCredentialRefresh
{
    private const int RevisionBlockCapacity = 1000;
    private const int RevisionDocumentLimit = 128 * 1024;
    private static bool IsRevision(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static CredentialRevisionLedger? ReadLedger(string source)
    {
        var path = LedgerPath(source);
        if (!File.Exists(path)) return null;
        try
        {
            var ledger = JsonSerializer.Deserialize(ReadRevisionDocument(path, source), CredentialRefreshJsonContext.Default.CredentialRevisionLedger)
                ?? throw EvidenceInvalid(source);
            if (ledger.Version is not (1 or 2) || !IsRevision(ledger.CurrentRevision) || !IsRevision(ledger.TargetRevision)
                || !ValidRevisions(ledger.Revisions) || ledger.Revisions[^1] != ledger.CurrentRevision
                || ledger.PreviousBlock is not null && (!IsRevision(ledger.PreviousBlock) || ledger.Version == 1))
                throw EvidenceInvalid(source);
            return ledger;
        }
        catch (JsonException) { throw EvidenceInvalid(source); }
    }

    private static bool ValidRevisions(string[]? revisions)
        => revisions is { Length: > 0 and <= RevisionBlockCapacity } && revisions.All(IsRevision);

    private static byte[] ReadRevisionDocument(string path, string source)
    {
        RejectReparsePoints(path); RejectHardLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > RevisionDocumentLimit) throw EvidenceInvalid(source);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw EvidenceInvalid(source);
        return bytes;
    }

    internal static bool AcceptsFrozenRevision(string source, string frozenRevision, byte[] current)
    {
        var revision = Hash(current);
        if (revision == frozenRevision) return true;
        var ledger = ReadLedger(source);
        return ledger is not null && ledger.CurrentRevision == revision && ledger.TargetRevision == TargetRevision(current)
            && ContainsRevision(source, ledger, frozenRevision);
    }

    private static bool ContainsRevision(string source, CredentialRevisionLedger ledger, string revision)
    {
        if (!IsRevision(revision)) return false;
        if (ledger.Revisions.Contains(revision, StringComparer.Ordinal)) return true;
        var previous = ledger.PreviousBlock;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (previous is not null)
        {
            if (!IsRevision(previous) || !visited.Add(previous)) throw EvidenceInvalid(source);
            var path = RevisionBlockPath(source, previous);
            RejectReparsePoints(Path.GetDirectoryName(path)!);
            var bytes = ReadRevisionDocument(path, source);
            if (Hash(bytes) != previous) throw EvidenceInvalid(source);
            CredentialRevisionBlock block;
            try { block = JsonSerializer.Deserialize(bytes, CredentialRefreshJsonContext.Default.CredentialRevisionBlock) ?? throw EvidenceInvalid(source); }
            catch (JsonException) { throw EvidenceInvalid(source); }
            if (block.Version != 1 || block.TargetRevision != ledger.TargetRevision || !ValidRevisions(block.Revisions)
                || block.PreviousBlock is not null && !IsRevision(block.PreviousBlock)) throw EvidenceInvalid(source);
            if (block.Revisions.Contains(revision, StringComparer.Ordinal)) return true;
            previous = block.PreviousBlock;
        }
        return false;
    }

    private static void RequireTrustedSource(string source, byte[] bytes, bool requireCurrent = true)
    {
        var revision = Hash(bytes); var ledger = ReadLedger(source);
        if (ledger is null)
        {
            SaveLedger(source, new(1, revision, TargetRevision(bytes), [revision])); return;
        }
        if (ledger.TargetRevision != TargetRevision(bytes) || !ContainsRevision(source, ledger, revision)
            || requireCurrent && ledger.CurrentRevision != revision) throw Conflict();
    }

    private static string RevisionBlockPath(string source, string hash)
        => Path.Combine(source + ".folderrewind-revision-blocks", hash + ".json");

    // Prepare durable capacity before changing credentials. Immutable blocks retain every old frozen proof,
    // including the origin and persisted jobs; no Host-wide reference scan or arbitrary history truncation.
    private static CredentialRevisionLedger PrepareTrustedRefresh(string source, byte[] original, byte[] refreshed)
    {
        var ledger = ReadLedger(source) ?? throw Conflict();
        var revision = Hash(refreshed);
        if (ledger.CurrentRevision == revision) return ledger;
        if (ledger.CurrentRevision != Hash(original) || ledger.TargetRevision != TargetRevision(refreshed)) throw Conflict();
        if (ledger.Revisions.Length < RevisionBlockCapacity)
            return ledger with { CurrentRevision = revision, Revisions = [.. ledger.Revisions, revision] };

        var block = new CredentialRevisionBlock(1, ledger.TargetRevision, ledger.Revisions, ledger.PreviousBlock);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(block, CredentialRefreshJsonContext.Default.CredentialRevisionBlock);
        var hash = Hash(bytes); var path = RevisionBlockPath(source, hash);
        RcloneExecutionContext.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
        RejectReparsePoints(path);
        if (File.Exists(path))
        {
            if (!ReadRevisionDocument(path, source).AsSpan().SequenceEqual(bytes)) throw EvidenceInvalid(source);
        }
        else
        {
            AtomicFileService.Write(path, stream => stream.Write(bytes));
            RcloneExecutionContext.ProtectConfigurationFile(path);
        }
        return new(2, revision, ledger.TargetRevision, [revision], hash);
    }

    private static void RecordTrustedRefresh(string source, byte[] original, byte[] refreshed)
        => SaveLedger(source, PrepareTrustedRefresh(source, original, refreshed));

    private static void SaveLedger(string source, CredentialRevisionLedger ledger)
    {
        AtomicFileService.Write(LedgerPath(source), s => JsonSerializer.Serialize(s, ledger, CredentialRefreshJsonContext.Default.CredentialRevisionLedger));
        RcloneExecutionContext.ProtectConfigurationFile(LedgerPath(source));
    }
}

internal sealed record CredentialRevisionBlock(int Version, string TargetRevision, string[] Revisions, string? PreviousBlock);
