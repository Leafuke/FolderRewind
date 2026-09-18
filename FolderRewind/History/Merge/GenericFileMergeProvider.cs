using FolderRewind.History.Domain;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public sealed record MergeFileValue(string Handle, string Digest, long Length);
public sealed record MergeTreeManifest(ImmutableSortedDictionary<string, MergeFileValue> Files, string Digest)
{
    public static MergeTreeManifest Empty { get; } = Create([]);
    public static MergeTreeManifest Create(IEnumerable<KeyValuePair<string, MergeFileValue>> files)
    {
        var sorted = files.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var signature = new StringBuilder("folderrewind/tree/1\0");
        foreach (var p in sorted) signature.Append(p.Key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(p.Key).Append(':')
            .Append(p.Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(p.Value.Digest).Append('\n');
        return new(sorted, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))));
    }
    public static async Task<MergeTreeManifest> ReadAsync(string root, Func<string, bool> include, CancellationToken token)
    {
        var files = new Dictionary<string, MergeFileValue>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Merge cannot follow links.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Merge cannot follow links.");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!include(relative)) continue;
                _ = RestoreStagingProposalValidator.ValidateAndFreeze(new([new(relative, ReadOnlyMemory<byte>.Empty)], []), _ => true);
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(Encoding.UTF8.GetBytes("folderrewind/file/1\0"));
                var buffer = new byte[65536]; long size = 0; int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) { hash.AppendData(buffer, 0, read); size += read; }
                files.Add(relative, new(path, Convert.ToHexString(hash.GetHashAndReset()), size));
            }
        }
        var result = Create(files);
        if (GenericFileMergeProvider.StructuralGroups(result.Files.Keys).Count != 0)
            throw new IOException("Input tree contains case or file/directory collisions.");
        return result;
    }
}

public enum MergeConflictKind { ModifyModify, ModifyDelete, AddAdd, PathStructure, SourceRoster, SourceBoundary }
public sealed record MergeConflictSubject(SourceId SourceId, ImmutableArray<string> Paths, string? ProviderUnitId = null);
public sealed record MergeConflict(string Id, MergeConflictSubject Subject, MergeConflictKind Kind, string InputSignature,
    ImmutableSortedDictionary<string, MergeFileValue> Base, ImmutableSortedDictionary<string, MergeFileValue> Ours,
    ImmutableSortedDictionary<string, MergeFileValue> Theirs);
public sealed record MergeFileProposal(MergeTreeManifest Automatic, ImmutableArray<MergeConflict> Conflicts);
public interface IHistoryMergeProvider
{
    string Version { get; }
    MergeFileProposal Analyze(SourceId source, MergeTreeManifest @base, MergeTreeManifest ours, MergeTreeManifest theirs);
}

public sealed class GenericFileMergeProvider : IHistoryMergeProvider
{
    public string Version => "generic-file/1";
    public MergeFileProposal Analyze(SourceId source, MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t)
    {
        var paths = b.Files.Keys.Concat(o.Files.Keys).Concat(t.Files.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var structural = StructuralGroups(paths);
        var claimed = structural.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var automatic = new Dictionary<string, MergeFileValue>(StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();
        foreach (var group in structural)
        {
            MergeTreeManifest Subtree(MergeTreeManifest tree) => MergeTreeManifest.Create(tree.Files.Where(p => group.Contains(p.Key, StringComparer.Ordinal)));
            var bg = Subtree(b); var og = Subtree(o); var tg = Subtree(t);
            var chosen = og.Digest == tg.Digest ? og : og.Digest == bg.Digest ? tg : tg.Digest == bg.Digest ? og : null;
            if (chosen is null) Conflict(group, MergeConflictKind.PathStructure);
            else foreach (var pair in chosen.Files) automatic.Add(pair.Key, pair.Value);
        }
        foreach (var path in paths.Except(claimed).Order(StringComparer.Ordinal))
        {
            var bv = b.Files.GetValueOrDefault(path); var ov = o.Files.GetValueOrDefault(path); var tv = t.Files.GetValueOrDefault(path);
            MergeFileValue? selected;
            if (Equal(ov, tv)) selected = ov;
            else if (Equal(ov, bv)) selected = tv;
            else if (Equal(tv, bv)) selected = ov;
            else { Conflict([path], bv is null ? MergeConflictKind.AddAdd
                : ov is null || tv is null ? MergeConflictKind.ModifyDelete : MergeConflictKind.ModifyModify); continue; }
            if (selected is not null) automatic.Add(path, selected);
        }
        return new(MergeTreeManifest.Create(automatic), conflicts.ToImmutableArray());

        void Conflict(IEnumerable<string> members, MergeConflictKind kind)
        {
            var subject = members.Order(StringComparer.Ordinal).ToImmutableArray();
            ImmutableSortedDictionary<string, MergeFileValue> Subset(MergeTreeManifest tree)
                => tree.Files.Where(p => subject.Contains(p.Key)).ToImmutableSortedDictionary(StringComparer.Ordinal);
            var bs = Subset(b); var os = Subset(o); var ts = Subset(t);
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"folderrewind/conflict/1\0{Version}\0{source}\0{kind}\0{string.Join('\0', subject)}\0"
                + MergeTreeManifest.Create(bs).Digest + MergeTreeManifest.Create(os).Digest + MergeTreeManifest.Create(ts).Digest)));
            conflicts.Add(new(signature, new(source, subject), kind, signature, bs, os, ts));
        }
    }
    private static bool Equal(MergeFileValue? a, MergeFileValue? b) => a?.Digest == b?.Digest && a?.Length == b?.Length;

    internal static List<string[]> StructuralGroups(IEnumerable<string> paths)
    {
        var items = paths.Distinct(StringComparer.Ordinal).ToArray();
        var parent = Enumerable.Range(0, items.Length).ToArray();
        int Root(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) => parent[Root(a)] = Root(b);
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Length; i++)
            if (byPath.TryGetValue(items[i], out var existing)) Union(i, existing); else byPath.Add(items[i], i);

        // Windows resolves every path component case-insensitively. Track directory prefixes as well as
        // complete file names so A/x and a/y become one atomic structural conflict instead of an
        // unrepresentable result that happens to depend on enumeration order.
        var canonicalPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefixMembers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var collidedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Length; i++)
        {
            IEnumerable<string> Prefixes()
            {
                for (int slash = items[i].IndexOf('/'); slash >= 0; slash = items[i].IndexOf('/', slash + 1))
                    yield return items[i][..slash];
                yield return items[i];
            }
            foreach (var prefix in Prefixes())
            {
                if (!canonicalPrefixes.TryGetValue(prefix, out var canonical))
                {
                    canonicalPrefixes.Add(prefix, prefix);
                    prefixMembers.Add(prefix, [i]);
                    continue;
                }
                var members = prefixMembers[prefix];
                if (!StringComparer.Ordinal.Equals(canonical, prefix))
                {
                    foreach (var member in members) Union(i, member);
                    collidedPrefixes.Add(prefix);
                }
                else if (collidedPrefixes.Contains(prefix)) Union(i, members[0]);
                members.Add(i);
            }
            for (int slash = items[i].IndexOf('/'); slash >= 0; slash = items[i].IndexOf('/', slash + 1))
                if (byPath.TryGetValue(items[i][..slash], out var prefix)) Union(i, prefix);
        }
        return Enumerable.Range(0, items.Length).GroupBy(Root).Where(g => g.Count() > 1)
            .Select(g => g.Select(i => items[i]).Order(StringComparer.Ordinal).ToArray()).ToList();
    }
}
