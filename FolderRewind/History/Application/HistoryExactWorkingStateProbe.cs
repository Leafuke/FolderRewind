using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryExactWorkingStateProbe(HistoryRuntime history, HistoryRestoreService restore)
    : IHistoryWorkingStateProbe
{
    public async Task<bool> IsExactAsync(
        HistoryRestoreSourceBinding binding,
        WorkspaceSourceBaseline baseline,
        CancellationToken cancellationToken)
    {
        if (baseline.Relation != WorkspaceBaselineRelation.Exact
            || baseline.BaseVersionId is not { } id
            || !Directory.Exists(binding.TargetDirectory)) return false;
        var version = await history.Query.GetVersionAsync(id, cancellationToken).ConfigureAwait(false);
        if (version is null || version.SourceId != binding.SourceId
            || version.ConfigId != history.ConfigId
            || version.EffectiveSourceBoundaryFingerprint != binding.Boundary.Fingerprint) return false;
        HistoryRestoreService.PreparedRestoreSource? prepared = null;
        try
        {
            prepared = await restore.PrepareSourceAsync(version, binding, MaterializationFidelity.Exact,
                HistoryRestoreApplyMode.Clean, cancellationToken).ConfigureAwait(false);
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding);
            var expected = await ReadTreeAsync(prepared.StagingDirectory, include, cancellationToken).ConfigureAwait(false);
            var actual = await ReadTreeAsync(binding.TargetDirectory, include, cancellationToken).ConfigureAwait(false);
            return expected.Count == actual.Count
                && expected.All(pair => actual.TryGetValue(pair.Key, out var digest) && pair.Value == digest);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            if (prepared is not null)
                HistoryRestoreTransactionJournalStore.CleanupStaging([prepared.StagingDirectory]);
        }
    }

    private static async Task<Dictionary<string, string>> ReadTreeAsync(
        string root, Func<string, bool> include, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Exact working-state probe cannot follow links.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Exact working-state probe cannot follow links.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (!include(relative)) continue;
                await using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 65536, true);
                result.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
            }
        }
        return result;
    }
}
