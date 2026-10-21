using FolderRewind.History.Capture;
using FolderRewind.History.Domain;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public sealed record MergeFileValue(string Handle, string Digest, long Length);
public sealed record MergeTreeManifest(ImmutableSortedDictionary<string, MergeFileValue> Files, string Digest)
{
    public ImmutableArray<string> Directories { get; init; } = [];
    [JsonIgnore] public IEnumerable<string> Paths => Files.Keys.Concat(Directories);
    [JsonIgnore] public bool IsEmpty => Files.IsEmpty && Directories.IsEmpty;
    public static MergeTreeManifest Empty { get; } = Create([]);

    public static MergeTreeManifest Create(IEnumerable<KeyValuePair<string, MergeFileValue>> files,
        IEnumerable<string>? directories = null)
    {
        var sorted = files.ToImmutableSortedDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        // File-only callers infer their parents. Explicit inventories also represent partial provider
        // proposals/conflict views, so never synthesize directories withheld by a structural conflict.
        var inventory = directories?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (directories is null)
            foreach (var file in sorted.Keys)
                for (int slash = file.LastIndexOf('/'); slash >= 0; slash = file.LastIndexOf('/', slash - 1))
                {
                    inventory.Add(file[..slash]);
                    if (slash == 0) break;
                }
        var ordered = inventory.Order(StringComparer.Ordinal).ToImmutableArray();
        var signature = new StringBuilder("folderrewind/tree/2\0");
        foreach (var directory in ordered)
            signature.Append('D').Append(directory.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(directory).Append('\n');
        foreach (var p in sorted)
            signature.Append('F').Append(p.Key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p.Key).Append(':')
                .Append(p.Value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(p.Value.Digest).Append('\n');
        return new(sorted, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString())))) { Directories = ordered };
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
        var result = Create(files, SourceDirectorySnapshot.Read(root, include, files.Keys, token));
        SourceDirectorySnapshot.Validate(result.Directories, result.Files.Keys);
        if (GenericFileMergeProvider.StructuralGroups(result).Count != 0)
            throw new IOException("Input tree contains case or file/directory collisions.");
        return result;
    }
}

public enum MergeConflictKind { ModifyModify, ModifyDelete, AddAdd, PathStructure, SourceRoster, SourceBoundary }
public sealed record MergeConflictSubject(SourceId SourceId, ImmutableArray<string> Paths, string? ProviderUnitId = null);
public sealed record MergeConflict(string Id, MergeConflictSubject Subject, MergeConflictKind Kind, string InputSignature,
    ImmutableSortedDictionary<string, MergeFileValue> Base, ImmutableSortedDictionary<string, MergeFileValue> Ours,
    ImmutableSortedDictionary<string, MergeFileValue> Theirs)
{
    public ImmutableArray<string> BaseDirectories { get; init; } = [];
    public ImmutableArray<string> OursDirectories { get; init; } = [];
    public ImmutableArray<string> TheirsDirectories { get; init; } = [];
    [JsonIgnore] public bool CanImportFile => Subject.Paths.Length == 1
        && BaseDirectories.IsEmpty && OursDirectories.IsEmpty && TheirsDirectories.IsEmpty
        && Kind is not (MergeConflictKind.PathStructure or MergeConflictKind.SourceRoster or MergeConflictKind.SourceBoundary);
}

public sealed record MergeProviderDescriptor(string Id, string ImplementationVersion, int SchemaVersion, string PolicyId, string PolicyVersion)
{
    public const string GenericIdentity = "generic-file@2;schema=2";
    public const string GenericPolicyIdentity = "conservative@2";
    public static MergeProviderDescriptor Generic { get; } = new("generic-file", "2", 2, "conservative", "2");
    public string Identity => $"{Id}@{ImplementationVersion};schema={SchemaVersion}";
    public string PolicyIdentity => $"{PolicyId}@{PolicyVersion}";
}
public sealed record MergeFileProposal(MergeTreeManifest Automatic, ImmutableArray<MergeConflict> Conflicts,
    ImmutableArray<string> HandledPaths = default);
public interface IHistoryMergeProvider
{
    MergeProviderDescriptor Descriptor { get; }
    string Version { get; }
    MergeFileProposal Analyze(SourceId source, MergeTreeManifest @base, MergeTreeManifest ours, MergeTreeManifest theirs);
}

public sealed class GenericFileMergeProvider : IHistoryMergeProvider
{
    public MergeProviderDescriptor Descriptor => MergeProviderDescriptor.Generic;
    public string Version => Descriptor.Identity;
    public MergeFileProposal Analyze(SourceId source, MergeTreeManifest b, MergeTreeManifest o, MergeTreeManifest t)
    {
        var paths = b.Paths.Concat(o.Paths).Concat(t.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var structural = StructuralGroups(b, o, t);
        var claimed = structural.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var automatic = new Dictionary<string, MergeFileValue>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var bd = b.Directories.ToHashSet(StringComparer.Ordinal);
        var od = o.Directories.ToHashSet(StringComparer.Ordinal);
        var td = t.Directories.ToHashSet(StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();
        foreach (var group in structural)
        {
            var bg = View(group, b, bd); var og = View(group, o, od); var tg = View(group, t, td);
            var chosen = og.Digest == tg.Digest ? og : og.Digest == bg.Digest ? tg : tg.Digest == bg.Digest ? og : null;
            if (chosen is null) Conflict(group, MergeConflictKind.PathStructure);
            else
            {
                foreach (var pair in chosen.Files) automatic.Add(pair.Key, pair.Value);
                directories.UnionWith(chosen.Directories);
            }
        }
        foreach (var path in paths.Where(p => !claimed.Contains(p)))
        {
            if (bd.Contains(path) || od.Contains(path) || td.Contains(path))
            {
                bool bv = bd.Contains(path), ov = od.Contains(path), tv = td.Contains(path);
                if (ov == tv ? ov : ov == bv ? tv : ov) directories.Add(path);
                continue;
            }
            var baseFile = b.Files.GetValueOrDefault(path); var ours = o.Files.GetValueOrDefault(path); var theirs = t.Files.GetValueOrDefault(path);
            MergeFileValue? selected;
            if (Equal(ours, theirs)) selected = ours;
            else if (Equal(ours, baseFile)) selected = theirs;
            else if (Equal(theirs, baseFile)) selected = ours;
            else { Conflict([path], baseFile is null ? MergeConflictKind.AddAdd
                : ours is null || theirs is null ? MergeConflictKind.ModifyDelete : MergeConflictKind.ModifyModify); continue; }
            if (selected is not null) automatic.Add(path, selected);
        }
        return new(MergeTreeManifest.Create(automatic, directories), conflicts.ToImmutableArray(), paths.ToImmutableArray());

        static MergeTreeManifest View(IEnumerable<string> members, MergeTreeManifest tree, HashSet<string> directoryPaths)
            => MergeTreeManifest.Create(members.Where(tree.Files.ContainsKey)
                .Select(path => new KeyValuePair<string, MergeFileValue>(path, tree.Files[path])), members.Where(directoryPaths.Contains));

        void Conflict(IEnumerable<string> members, MergeConflictKind kind)
        {
            var subject = members.Order(StringComparer.Ordinal).ToImmutableArray();
            var bs = View(subject, b, bd); var os = View(subject, o, od); var ts = View(subject, t, td);
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"folderrewind/conflict/3\0{Version}\0{source}\0{kind}\0{string.Join('\0', subject)}\0" + bs.Digest + os.Digest + ts.Digest)));
            conflicts.Add(new(signature, new(source, subject), kind, signature, bs.Files, os.Files, ts.Files)
                { BaseDirectories = bs.Directories, OursDirectories = os.Directories, TheirsDirectories = ts.Directories });
        }
    }
    private static bool Equal(MergeFileValue? a, MergeFileValue? b) => a?.Digest == b?.Digest && a?.Length == b?.Length;

    internal static List<string[]> StructuralGroups(params MergeTreeManifest[] trees)
    {
        var paths = trees.SelectMany(tree => tree.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = trees.SelectMany(tree => tree.Files.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (spelling.TryGetValue(path, out var canonical) && canonical != path) roots.Add(path);
            else spelling.TryAdd(path, path);
        }
        foreach (var directory in trees.SelectMany(tree => tree.Directories))
            if (files.Contains(directory)) roots.Add(directory);

        // A removed/replaced directory owns its complete subtree: selecting its surviving children
        // independently would silently undo deletion or discard changes on the other side.
        if (trees.Length == 3)
        {
            var od = trees[1].Directories.ToHashSet(StringComparer.Ordinal);
            var td = trees[2].Directories.ToHashSet(StringComparer.Ordinal);
            foreach (var directory in trees[0].Directories)
                if (od.Contains(directory) != td.Contains(directory)) roots.Add(directory);
        }
        var outermost = roots.Where(path =>
        {
            for (int slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (roots.Contains(path[..slash])) return false;
                if (slash == 0) break;
            }
            return true;
        }).Order(StringComparer.Ordinal);
        var groups = outermost.ToDictionary(root => root, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var ancestor = path;
            while (true)
            {
                if (groups.TryGetValue(ancestor, out var group)) { group.Add(path); break; }
                var slash = ancestor.LastIndexOf('/');
                if (slash < 0) break;
                ancestor = ancestor[..slash];
            }
        }
        return groups.Values.Select(group => group.ToArray()).ToList();
    }
}
