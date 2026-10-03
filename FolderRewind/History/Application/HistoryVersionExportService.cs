using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

// 非破坏性物化：不调用源恢复 coordinator，不提交历史，不修改 Workspace 或分支。
public sealed class HistoryVersionExportService(RepresentationRuntime representations)
{
    public async Task ExportAsync(VersionId versionId, IReadOnlyList<VersionRepresentation> graph,
        IRepresentationEnvironment environment, string destination, IReadOnlyList<string> protectedRoots,
        CancellationToken token = default, MaterializationFidelity requiredFidelity = MaterializationFidelity.Exact)
    {
        var target = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Export parent is missing.");
        RequireDestination(target, protectedRoots);
        Directory.CreateDirectory(parent);
        RequireNoLinks(parent);
        var staging = Path.Combine(parent, ".folderrewind-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var lease = await representations.LockVersionAsync(versionId, graph, environment, requiredFidelity, token).ConfigureAwait(false);
            await representations.MaterializeAsync(lease.RepresentationId, graph, environment,
                requiredFidelity, staging, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            RequireDestination(target, protectedRoots);
            RequireNoLinks(parent);
            // Move 对已存在目标失败，不覆盖用户文件；只有完整物化成功才发布。
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
    public static void RequireDestination(string destination, IReadOnlyList<string> protectedRoots)
    {
        if (!Path.IsPathFullyQualified(destination) || Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Export requires a new, unoccupied destination.");
        foreach (var root in protectedRoots)
            if (!BackupPathOverlapPolicy.Validate(destination, root).IsSafe)
                throw new IOException("Export destination overlaps a source or backup repository.");
        RequireNoLinks(Path.GetDirectoryName(Path.GetFullPath(destination))!);
    }
    private static void RequireNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Export destination must not traverse a reparse point.");
    }
}
