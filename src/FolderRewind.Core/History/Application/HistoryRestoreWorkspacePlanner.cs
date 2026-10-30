using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Application;

internal sealed record HistoryRestoreWorkspaceLocation(SourceId SourceId, string Target, string Container,
    RestoreObjectIdentity ParentIdentity, RestoreObjectIdentity? TargetIdentity, ulong AvailableBytes,
    string? LogicalTarget = null);

internal static class HistoryRestoreWorkspacePlanner
{
    internal static IReadOnlyList<HistoryRestoreWorkspaceLocation> Plan(HistoryTransactionId transaction,
        IReadOnlyList<HistoryRestoreSourceBinding> sources, IEnumerable<string> protectedRoots, long requiredBytes = 0)
    {
        var targets = sources.Select(source => Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.TargetDirectory))).ToArray();
        var protectedPaths = protectedRoots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray();
        for (int i = 0; i < targets.Length; i++)
        {
            if (sources.Take(i).Any(source => source.SourceId == sources[i].SourceId)
                || targets.Take(i).Any(other => Overlaps(other, targets[i])))
                throw new IOException("Restore Sources overlap or have duplicate identities.");
            if (protectedPaths.Any(root => Overlaps(root, targets[i]))) throw new IOException("Restore target overlaps protected application, state, History or backup storage.");
        }
        var plans = new List<HistoryRestoreWorkspaceLocation>();
        var protectedPhysical = protectedPaths.Select(ResolvePath).ToArray();
        for (int i = 0; i < sources.Count; i++)
        {
            var target = ResolvePath(targets[i]);
            var parent = Path.GetDirectoryName(target) ?? throw new IOException("Restore target cannot be a volume root.");
            if (!Directory.Exists(parent)) throw new IOException("Restore target parent must already exist.");
            if (File.Exists(target)) throw new IOException("Restore target is a file.");
            var (identity, available) = WindowsRestoreFileSystem.RequireLocalNtfs(parent);
            if (plans.Any(plan => Overlaps(plan.Target, target))) throw new IOException("Restore Sources physically overlap.");
            if (protectedPhysical.Any(root => Overlaps(root, target))) throw new IOException("Restore target physically overlaps protected storage.");
            if ((ulong)Math.Max(requiredBytes, 0) > available) throw new IOException("Insufficient space on the restore target volume.");
            var existing = WindowsRestoreFileSystem.TryIdentity(target);
            if (existing is not null && (!existing.Directory || existing.Volume != identity.Volume))
                throw new IOException("Restore target identity is unsupported.");
            if (existing is not null && plans.Any(plan => plan.TargetIdentity == existing))
                throw new IOException("Restore target aliases or nests another Source.");
            var container = Path.Combine(parent, $".folderrewind-restore-{transaction}-{sources[i].SourceId}");
            if (Path.Exists(container)) throw new IOException("Restore container already exists; cannot adopt it.");
            plans.Add(new(sources[i].SourceId, target, container, identity, existing, available, targets[i]));
        }
        return plans;
    }
    internal static string ResolvePath(string path)
    {
        var suffix = new Stack<string>();
        path = Path.GetFullPath(path);
        while (true)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                var parent = Path.GetDirectoryName(path);
                if (parent is null) throw;
                suffix.Push(Path.GetFileName(path)); path = parent;
                continue;
            }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Restore binding is a file: " + path);
            using var binding = new WindowsPhysicalDirectoryLease(path, protectFinal: false);
            var physical = binding.PhysicalPath;
            while (suffix.TryPop(out var name)) physical = Path.Combine(physical, name);
            return physical;
        }
    }
    internal static void ValidateBinding(HistoryRestoreWorkspaceLocation location)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(ResolvePath(location.LogicalTarget ?? location.Target), location.Target)
            || WindowsRestoreFileSystem.Identity(Path.GetDirectoryName(location.Target)!) != location.ParentIdentity
            || WindowsRestoreFileSystem.TryIdentity(location.Target) != location.TargetIdentity)
            throw new IOException("Restore target binding changed during preparation.");
    }
    internal static bool Overlaps(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b)
        || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
