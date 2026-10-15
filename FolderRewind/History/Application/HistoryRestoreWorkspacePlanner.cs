using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Application;

internal sealed record HistoryRestoreWorkspaceLocation(SourceId SourceId, string Target, string Container,
    RestoreObjectIdentity ParentIdentity, RestoreObjectIdentity? TargetIdentity, ulong AvailableBytes);

internal static class HistoryRestoreWorkspacePlanner
{
    internal static IReadOnlyList<HistoryRestoreWorkspaceLocation> Plan(HistoryTransactionId transaction,
        IReadOnlyList<HistoryRestoreSourceBinding> sources, IEnumerable<string> protectedRoots, long requiredBytes = 0)
    {
        var targets = sources.Select(source => Path.GetFullPath(source.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        var protectedPaths = protectedRoots.Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        for (int i = 0; i < targets.Length; i++)
        {
            if (sources.Take(i).Any(source => source.SourceId == sources[i].SourceId)
                || targets.Take(i).Any(other => Overlaps(other, targets[i])))
                throw new IOException("Restore Sources overlap or have duplicate identities.");
            if (protectedPaths.Any(root => Overlaps(root, targets[i]))) throw new IOException("Restore target overlaps protected application, state, History or backup storage.");
        }
        var plans = new List<HistoryRestoreWorkspaceLocation>();
        var ancestorIdentities = new List<HashSet<RestoreObjectIdentity>>();
        for (int i = 0; i < sources.Count; i++)
        {
            var target = targets[i];
            var parent = Path.GetDirectoryName(target) ?? throw new IOException("Restore target cannot be a volume root.");
            if (!Directory.Exists(parent)) throw new IOException("Restore target parent must already exist.");
            if (File.Exists(target)) throw new IOException("Restore target is a file.");
            var (identity, available) = WindowsRestoreFileSystem.RequireLocalNtfs(parent);
            if (plans.Any(plan => plan.ParentIdentity == identity
                && !Path.GetDirectoryName(plan.Target)!.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Restore Source parents use ambiguous filesystem aliases.");
            if ((ulong)Math.Max(requiredBytes, 0) > available) throw new IOException("Insufficient space on the restore target volume.");
            var ancestors = new HashSet<RestoreObjectIdentity>();
            for (string? directory = parent; directory is not null; directory = Path.GetDirectoryName(directory))
                ancestors.Add(WindowsRestoreFileSystem.Identity(directory));
            var existing = WindowsRestoreFileSystem.TryIdentity(target);
            if (existing is not null && (!existing.Directory || existing.Volume != identity.Volume))
                throw new IOException("Restore target identity is unsupported.");
            foreach (var protectedPath in protectedPaths.Where(Directory.Exists))
            {
                var protectedIdentity = WindowsRestoreFileSystem.Identity(protectedPath);
                if (ancestors.Contains(protectedIdentity) || existing == protectedIdentity)
                    throw new IOException("Restore target aliases protected storage.");
                for (string? ancestor = protectedPath; existing is not null && ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                    if (WindowsRestoreFileSystem.Identity(ancestor) == existing) throw new IOException("Restore target physically contains protected storage.");
            }
            if (existing is not null && plans.Any(plan => plan.TargetIdentity == existing || ancestorIdentities[plans.IndexOf(plan)].Contains(existing))
                || plans.Any(plan => plan.TargetIdentity is not null && ancestors.Contains(plan.TargetIdentity)))
                throw new IOException("Restore target aliases or nests another Source.");
            var container = Path.Combine(parent, $".folderrewind-restore-{transaction}-{sources[i].SourceId}");
            if (Path.Exists(container)) throw new IOException("Restore container already exists; cannot adopt it.");
            plans.Add(new(sources[i].SourceId, target, container, identity, existing, available));
            ancestorIdentities.Add(ancestors);
        }
        return plans;
    }
    internal static bool Overlaps(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b)
        || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
