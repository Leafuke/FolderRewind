using FolderRewind.History.Merge;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Retention;

// A storage-rewrite witness, not a replacement for a Version's historical fingerprint.
public sealed record HistoryRewriteFile(string Digest, long Length, DateTime LastWriteUtc, FileAttributes Attributes);

public sealed record HistoryRewriteTree(
    ImmutableSortedDictionary<string, HistoryRewriteFile> Files,
    ImmutableSortedSet<string> Directories)
{
    public long TotalBytes => Files.Values.Sum(f => f.Length);

    public static async Task<HistoryRewriteTree> ReadAsync(string root, CancellationToken token = default)
    {
        var content = await MergeTreeManifest.ReadAsync(root, _ => true, token).ConfigureAwait(false);
        var files = content.Files.ToImmutableSortedDictionary(p => p.Key, p =>
        {
            var info = new FileInfo(p.Value.Handle);
            return new HistoryRewriteFile(p.Value.Digest, p.Value.Length, info.LastWriteTimeUtc,
                info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }, StringComparer.Ordinal);
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToImmutableSortedSet(StringComparer.Ordinal);
        return new(files, directories);
    }

    public bool EquivalentTo(HistoryRewriteTree other)
        => Files.SequenceEqual(other.Files) && Directories.SetEquals(other.Directories);

    public ImmutableArray<string> ChangedFrom(HistoryRewriteTree baseline)
        => [.. Files.Where(p => !baseline.Files.TryGetValue(p.Key, out var old) || old != p.Value).Select(p => p.Key)];

    public bool RequiresFullAgainst(HistoryRewriteTree baseline)
    {
        // Existing deltas apply deletions after extraction. Structural replacements must not use that encoding.
        if (!Directories.SetEquals(baseline.Directories)) return true;
        var oldPaths = baseline.Files.Keys.ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        return Files.Keys.Any(p => oldPaths.TryGetValue(p, out var old) && p != old);
    }
}

public interface IHistoryChainRewriteArchiveBackend : IHistoryCompactionBackend
{
    Task<HistoryCompactionPayload> CreateDeltaAsync(
        Domain.SourceVersion version, string targetDirectory, ImmutableArray<string> changedFiles,
        Domain.RepresentationId replacementId, string durableOutputDirectory, CancellationToken token);
}
