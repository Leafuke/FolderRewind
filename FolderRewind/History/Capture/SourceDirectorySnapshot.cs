using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Capture;

/// <summary>Host-owned directory inventory from the same stable view as the file witness.</summary>
public static class SourceDirectorySnapshot
{
    public static ImmutableArray<string> Read(string root, Func<string, bool> include,
        IEnumerable<string> files, CancellationToken token)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A stable Native source cannot contain filesystem links.");
                if ((attributes & FileAttributes.Directory) == 0) continue;
                pending.Push(entry);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (include(relative)) Add(relative);
            }
        }
        foreach (var file in files) AddParents(file);
        return [.. directories.Order(StringComparer.Ordinal)];

        void Add(string path)
        {
            directories.Add(ArtifactPathRules.NormalizeRelativePath(path));
            AddParents(path);
            if (directories.Count > ArtifactTransformLimits.Default.MaximumFiles)
                throw new InvalidDataException("Native directory inventory exceeds its entry budget.");
        }
        void AddParents(string path)
        {
            while (path.LastIndexOf('/') is var separator && separator >= 0)
            { path = path[..separator]; directories.Add(ArtifactPathRules.NormalizeRelativePath(path)); }
            if (directories.Count > ArtifactTransformLimits.Default.MaximumFiles)
                throw new InvalidDataException("Native directory inventory exceeds its entry budget.");
        }
    }

    public static void Validate(IReadOnlyList<string> directories, IEnumerable<string> filePaths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in filePaths)
            if (file != ArtifactPathRules.NormalizeRelativePath(file) || !files.Add(file))
                throw new InvalidDataException("Native file inventory is ambiguous.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (directories.Count + files.Count > ArtifactTransformLimits.Default.MaximumFiles)
            throw new InvalidDataException("Native logical tree exceeds its entry budget.");
        foreach (var directory in directories)
            if (directory != ArtifactPathRules.NormalizeRelativePath(directory) || !paths.Add(directory) || files.Contains(directory))
                throw new InvalidDataException("Native directory inventory is ambiguous.");
        foreach (var entry in directories.Concat(files))
        {
            var parent = entry;
            while (parent.LastIndexOf('/') is var separator && separator >= 0)
            { parent = parent[..separator]; if (!paths.Contains(parent)) throw new InvalidDataException("Native logical tree is missing a parent directory."); }
        }
    }
}
