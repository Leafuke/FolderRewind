using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryWorkingStateComparisonStatus { Equal, Different, Unknown }
public sealed record HistoryWorkingStateComparison(HistoryWorkingStateComparisonStatus Status, string Reason)
{
    public bool IsEqual => Status == HistoryWorkingStateComparisonStatus.Equal;
}

internal sealed class HistoryWorkingStateEvidence(HistoryWorkingStateComparison comparison, HistoryWorkingTreeReadLease? current = null) : IDisposable
{
    private bool _disposed;
    internal HistoryWorkingStateComparison Comparison { get; } = comparison;
    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Comparison.IsEqual || current is null) throw new InvalidOperationException("No stable equality evidence is held.");
        current.Validate();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; current?.Dispose(); }
}

public sealed class HistoryExactWorkingStateProbe(HistoryRuntime history, HistoryRestoreService restore)
    : IHistoryWorkingStateProbe
{
    public async Task<bool> IsExactAsync(HistoryRestoreSourceBinding binding, WorkspaceSourceBaseline baseline, CancellationToken cancellationToken)
        => (await CompareAsync(binding, baseline, cancellationToken).ConfigureAwait(false)).IsEqual;

    public async Task<HistoryWorkingStateComparison> CompareAsync(HistoryRestoreSourceBinding binding, WorkspaceSourceBaseline baseline, CancellationToken token)
    {
        using var evidence = await CompareWithEvidenceAsync(binding, baseline, token).ConfigureAwait(false);
        return evidence.Comparison;
    }

    internal async Task<HistoryWorkingStateEvidence> CompareWithEvidenceAsync(HistoryRestoreSourceBinding binding,
        WorkspaceSourceBaseline baseline, CancellationToken token, HistoryRestoreService.PreparedRestoreSource? reuse = null)
    {
        token.ThrowIfCancellationRequested();
        if (baseline.Relation != WorkspaceBaselineRelation.Exact || baseline.RequiresFullCapture || baseline.BaseVersionId is not { } id)
            return new(new(HistoryWorkingStateComparisonStatus.Unknown, "BaselineUnavailable"));
        return await CompareVersionWithEvidenceAsync(binding, id, token, reuse).ConfigureAwait(false);
    }

    public async Task<bool> MatchesVersionAsync(HistoryRestoreSourceBinding binding, VersionId id, CancellationToken cancellationToken)
        => (await CompareVersionAsync(binding, id, cancellationToken).ConfigureAwait(false)).IsEqual;

    private async Task<HistoryWorkingStateComparison> CompareVersionAsync(HistoryRestoreSourceBinding binding, VersionId id, CancellationToken token)
    {
        using var evidence = await CompareVersionWithEvidenceAsync(binding, id, token).ConfigureAwait(false);
        return evidence.Comparison;
    }

    private async Task<HistoryWorkingStateEvidence> CompareVersionWithEvidenceAsync(HistoryRestoreSourceBinding binding, VersionId id,
        CancellationToken token, HistoryRestoreService.PreparedRestoreSource? reuse = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var version = await history.Query.GetVersionAsync(id, token).ConfigureAwait(false);
            if (version is null || version.SourceId != binding.SourceId || version.ConfigId != history.ConfigId
                || version.EffectiveSourceBoundaryFingerprint != binding.Boundary.Fingerprint)
                return new(new(HistoryWorkingStateComparisonStatus.Unknown, "BaselineBoundaryMismatch"));
            Services.HistoryOperationDiagnostics.Current?.Count("baseline-comparisons");
            return reuse is { UserPreparationApplied: false, Fidelity: MaterializationFidelity.Exact } && reuse.Version.VersionId == id
                ? await ComparePreparedWithEvidenceAsync(reuse, binding, token).ConfigureAwait(false)
                : await restore.CompareReadOnlyWithEvidenceAsync(version, binding, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { return new(new(HistoryWorkingStateComparisonStatus.Unknown, ex.Message)); }
    }

    internal static async Task<HistoryWorkingStateEvidence> ComparePreparedWithEvidenceAsync(HistoryRestoreService.PreparedRestoreSource prepared,
        HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        if (!MetadataMatches(prepared, binding, token, out _)) return new(new(HistoryWorkingStateComparisonStatus.Different, string.Empty));
        var current = new HistoryWorkingTreeReadLease(binding);
        try
        {
            bool equal = await MatchesPreparedAsync(prepared, binding, token, current).ConfigureAwait(false);
            if (equal) return new(new(HistoryWorkingStateComparisonStatus.Equal, string.Empty), current);
            current.Dispose();
            return new(new(HistoryWorkingStateComparisonStatus.Different, string.Empty));
        }
        catch { current.Dispose(); throw; }
    }

    internal static async Task<bool> MatchesPreparedAsync(HistoryRestoreService.PreparedRestoreSource prepared,
        HistoryRestoreSourceBinding binding, CancellationToken token, HistoryWorkingTreeReadLease? protectedCurrent = null)
    {
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("working-state-compare");
        token.ThrowIfCancellationRequested();
        if (!MetadataMatches(prepared, binding, token, out var expected, protectedCurrent?.PhysicalRoot)) return false;
        using var ownedCurrent = protectedCurrent is null ? new HistoryWorkingTreeReadLease(binding) : null;
        var current = protectedCurrent ?? ownedCurrent!;
        using var baseline = new HistoryWorkingTreeReadLease(binding with { TargetDirectory = prepared.StagingDirectory, LogicalDirectory = binding.ComparisonRoot });
        byte[] left = ArrayPool<byte>.Shared.Rent(65536), right = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            foreach (var path in expected.Where(p => !p.Value.Directory).Select(p => p.Key).OrderBy(p => p, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var a = baseline.GetStream(path); var b = current.GetStream(path);
                if (a.Length != b.Length) return false;
                Services.HistoryOperationDiagnostics.Current?.Count("working-compare-files");
                for (long remaining = a.Length; remaining > 0;)
                {
                    int count = (int)Math.Min(65536, remaining);
                    await a.ReadExactlyAsync(left.AsMemory(0, count), token).ConfigureAwait(false);
                    await b.ReadExactlyAsync(right.AsMemory(0, count), token).ConfigureAwait(false);
                    Services.HistoryOperationDiagnostics.Current?.Count("working-compare-bytes", count * 2L);
                    if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
                    remaining -= count;
                }
            }
            baseline.Validate(); current.Validate();
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(left); ArrayPool<byte>.Shared.Return(right); }
    }

    private static bool MetadataMatches(HistoryRestoreService.PreparedRestoreSource prepared,
        HistoryRestoreSourceBinding binding, CancellationToken token,
        out Dictionary<string, (bool Directory, long Size)> expected, string? protectedRoot = null)
    {
        expected = [];
        if (!Directory.Exists(binding.TargetDirectory)) return false;
        using var expectedRoot = new WindowsPhysicalDirectoryLease(prepared.StagingDirectory);
        using var actualRoot = protectedRoot is null ? new WindowsPhysicalDirectoryLease(binding.TargetDirectory) : null;
        expected = ReadMetadata(expectedRoot.PhysicalPath, binding, token);
        var actual = ReadMetadata(protectedRoot ?? actualRoot!.PhysicalPath, binding, token);
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var entry) || pair.Value != entry))
        {
            Services.HistoryOperationDiagnostics.Current?.Count("working-compare-metadata-differences");
            return false;
        }
        return true;
    }

    private static Dictionary<string, (bool Directory, long Size)> ReadMetadata(
        string root, HistoryRestoreSourceBinding binding, CancellationToken token)
    {
        var result = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
        foreach (var entry in new HistoryRestoreTreeScope(binding).Enumerate(root))
        {
            token.ThrowIfCancellationRequested();
            result.Add(entry.RelativePath, (entry.Directory, entry.Directory ? 0 : new FileInfo(entry.FullPath).Length));
        }
        return result;
    }
}
