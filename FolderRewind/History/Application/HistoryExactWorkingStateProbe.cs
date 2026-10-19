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

public enum HistoryWorkingStateComparisonStatus { Equal, Different, Unknown }
public sealed record HistoryWorkingStateComparison(HistoryWorkingStateComparisonStatus Status, string Reason)
{
    public bool IsEqual => Status == HistoryWorkingStateComparisonStatus.Equal;
}

public sealed class HistoryExactWorkingStateProbe(HistoryRuntime history, HistoryRestoreService restore)
    : IHistoryWorkingStateProbe
{
    public async Task<bool> IsExactAsync(HistoryRestoreSourceBinding binding, WorkspaceSourceBaseline baseline, CancellationToken cancellationToken)
        => (await CompareAsync(binding, baseline, cancellationToken).ConfigureAwait(false)).IsEqual;

    public Task<HistoryWorkingStateComparison> CompareAsync(HistoryRestoreSourceBinding binding, WorkspaceSourceBaseline baseline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return baseline.Relation != WorkspaceBaselineRelation.Exact || baseline.RequiresFullCapture || baseline.BaseVersionId is not { } id
            ? Task.FromResult(new HistoryWorkingStateComparison(HistoryWorkingStateComparisonStatus.Unknown, "BaselineUnavailable"))
            : CompareVersionAsync(binding, id, token);
    }

    public async Task<bool> MatchesVersionAsync(HistoryRestoreSourceBinding binding, VersionId id, CancellationToken cancellationToken)
        => (await CompareVersionAsync(binding, id, cancellationToken).ConfigureAwait(false)).IsEqual;

    private async Task<HistoryWorkingStateComparison> CompareVersionAsync(HistoryRestoreSourceBinding binding, VersionId id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var version = await history.Query.GetVersionAsync(id, token).ConfigureAwait(false);
            if (version is null || version.SourceId != binding.SourceId || version.ConfigId != history.ConfigId
                || version.EffectiveSourceBoundaryFingerprint != binding.Boundary.Fingerprint)
                return new(HistoryWorkingStateComparisonStatus.Unknown, "BaselineBoundaryMismatch");
            return await restore.CompareReadOnlyAsync(version, binding, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(HistoryWorkingStateComparisonStatus.Unknown, ex.Message); }
    }

    internal static async Task<bool> MatchesPreparedAsync(HistoryRestoreService.PreparedRestoreSource prepared,
        HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("working-state-hash");
        if (!Directory.Exists(binding.TargetDirectory)) return false;
        var expected = await ReadTreeAsync(prepared.StagingDirectory, binding, token).ConfigureAwait(false);
        var actual = await ReadTreeAsync(binding.TargetDirectory, binding, token).ConfigureAwait(false);
        return expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var digest) && pair.Value == digest);
    }

    private static async Task<Dictionary<string, string>> ReadTreeAsync(
        string root, HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var directory = new Storage.WindowsPhysicalDirectoryLease(root);
        foreach (var entry in new Storage.HistoryRestoreTreeScope(binding).Enumerate(directory.PhysicalPath))
        {
            token.ThrowIfCancellationRequested();
            if (entry.Directory) { result.Add(entry.RelativePath + "/", "directory"); continue; }
            await using var stream = new FileStream(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            result.Add(entry.RelativePath, Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
        }
        return result;
    }
}
