using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;

namespace FolderRewind.History.Application;

internal static class HistoryRestoreMutationPlanner
{
    internal static HistoryRestoreFileSystemPlan Build(HistoryRestorePreparationSession session,
        IReadOnlyList<HistoryRestoreService.PreparedRestoreSource> sources, bool allowDirectorySwap, bool protectCurrentFiles = false,
        IReadOnlySet<SourceId>? unchangedTargets = null)
    {
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("mutation-plan-build");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore mutation planning requires Windows NTFS.");
        session.ValidateBindings();
        var objects = new Dictionary<string, RestorePlanObject>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<RestoreMoveAction>();
        var evidence = new List<RestoreSourcePlanEvidence>();
        foreach (var source in sources)
        {
            var workspace = session.Journal.Workspaces.Single(w => w.Location.SourceId == source.Binding.SourceId);
            var location = workspace.Location; var target = location.Target; var staging = source.StagingDirectory;
            AddPath(Path.GetDirectoryName(target)!);
            if (WindowsRestoreFileSystem.Identity(Path.GetDirectoryName(target)!) != location.ParentIdentity
                || WindowsRestoreFileSystem.TryIdentity(target) != location.TargetIdentity)
                throw new IOException("Restore target binding changed during preparation.");
            var boundary = source.Binding.Boundary;
            if (unchangedTargets?.Contains(source.Binding.SourceId) == true)
            {
                // 一致证据仍被持有时，同一版本只需提交分支状态，无需替换相同文件。
                var entries = new HistoryRestoreTreeScope(source.Binding).Enumerate(target, includeExcluded: true).ToArray();
                var entryIndex = Index(entries.Select(e => e.RelativePath));
                foreach (var entry in entries.Where(e => e.Managed)) AddPath(entry.FullPath);
                AddPath(target);
                foreach (var item in HistoryRestoreFileSystemPlanStore.CaptureTree(location.Container)) Add(item);
                evidence.Add(new(source.Binding.SourceId, source.Version.VersionId, target, source.Binding.Boundary,
                    entries.Where(e => e.Managed && !e.Directory).Select(e => e.RelativePath).ToArray(),
                    entries.Where(e => e.Managed && e.Directory && !entryIndex.HasDescendants(e.RelativePath))
                        .Select(e => e.RelativePath).ToArray(), source.Binding.ComparisonRoot));
                Services.HistoryOperationDiagnostics.Current?.Count("unchanged-target-sources");
                continue;
            }
            bool physicalAll = boundary.ScopeMode == EffectiveBoundaryScopeMode.All
                && boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist && boundary.Rules.IsEmpty;
            bool swap = allowDirectorySwap && source.ApplyMode == HistoryRestoreApplyMode.Clean && physicalAll
                && source.Version.CaptureScope == CaptureScope.FullSource && source.Version.BoundaryConfidence == HistoricalBoundaryConfidence.Known
                && source.Fidelity == MaterializationFidelity.Exact;
            string? publishedRootSecurity = null;
            var containerId = workspace.ContainerIdentity ?? throw new IOException("Restore container is unregistered.");
            if (swap && location.TargetIdentity is not null)
            {
                try
                {
                    var oldRoot = new DirectoryInfo(target);
                    new DirectoryInfo(staging).SetAccessControl(oldRoot.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
                    publishedRootSecurity = oldRoot.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
                        .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
                    File.SetAttributes(staging, File.GetAttributes(target));
                }
                catch (Exception error) when (error is UnauthorizedAccessException or PrivilegeNotHeldException)
                {
                    swap = false;
                    Services.HistoryOperationDiagnostics.Current?.Observe("directory-swap-fallback", "root-security-cannot-be-preserved");
                }
            }
            if (swap)
            {
                if (location.TargetIdentity is not null)
                {
                    // 已有根的 ACL/属性属于目标，不能把容器或归档的权限意外带入用户目录。
                    foreach (var item in HistoryRestoreFileSystemPlanStore.CaptureTree(target)) Add(item);
                    Move(source.Binding.SourceId, target, Path.Combine(location.Container, "rollback"), location.TargetIdentity,
                        location.ParentIdentity, containerId);
                }
                Move(source.Binding.SourceId, staging, target, WindowsRestoreFileSystem.Identity(staging), containerId, location.ParentIdentity,
                    reset: publishedRootSecurity is null, security: publishedRootSecurity);
                Services.HistoryOperationDiagnostics.Current?.Observe("mutation-strategy", "DirectorySwap");
                Services.HistoryOperationDiagnostics.Current?.Count("directory-swap-sources");
            }
            else
            {
                BuildDelta(source, workspace);
                Services.HistoryOperationDiagnostics.Current?.Observe("mutation-strategy", "FileDelta");
                Services.HistoryOperationDiagnostics.Current?.Count("file-delta-sources");
            }
            var containerTree = HistoryRestoreFileSystemPlanStore.CaptureTree(location.Container);
            foreach (var item in containerTree) Add(item);
            if (swap)
            {
                var stageTree = containerTree.Where(item => item.Path.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray();
                var stageIndex = Index(stageTree.Select(item => Path.GetRelativePath(staging, item.Path).Replace('\\', '/')));
                evidence.Add(new(source.Binding.SourceId, source.Version.VersionId, target, source.Binding.Boundary,
                    stageTree.Where(item => !item.Identity.Directory).Select(item => Path.GetRelativePath(staging, item.Path).Replace('\\', '/')).ToArray(),
                    stageTree.Where(item => item.Identity.Directory && !stageIndex.HasDescendants(Path.GetRelativePath(staging, item.Path).Replace('\\', '/')))
                        .Select(item => Path.GetRelativePath(staging, item.Path).Replace('\\', '/')).ToArray(), source.Binding.ComparisonRoot));
            }
            session.RecordPrepared(source.Binding.SourceId);
        }
        return new(session.Journal.TransactionId, session.Journal.Workspaces.Select(w => w.Location.Container).ToArray(), objects.Values.ToArray(), actions, evidence);

        void Add(RestorePlanObject item)
        {
            if (objects.TryGetValue(item.Path, out var known) && known.Identity != item.Identity) throw new IOException("Restore object changed while planning.");
            objects[item.Path] = item;
        }
        void AddPath(string path) => Add(new(path, WindowsRestoreFileSystem.Identity(path), File.GetAttributes(path)));
        void Move(SourceId source, string from, string to, RestoreObjectIdentity identity, RestoreObjectIdentity fromParent, RestoreObjectIdentity toParent,
            bool empty = false, bool reset = false, string? security = null)
            => actions.Add(new(actions.Count, source, from, to, identity, fromParent, toParent, empty, reset, security));
        void BuildDelta(HistoryRestoreService.PreparedRestoreSource source, RestoreWorkspaceRegistration workspace)
        {
            var target = workspace.Location.Target; var container = workspace.Location.Container;
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(source.Binding);
            var includeDirectory = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(source.Binding, isDirectory: true);
            var stage = Scan(source.StagingDirectory, source.Binding);
            var current = Directory.Exists(target) ? Scan(target, source.Binding) : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var currentIndex = new RestorePathIndex<bool>();
            var excluded = new RestorePathIndex<bool>();
            foreach (var pair in current)
            { currentIndex.Set(pair.Key, pair.Value); if (!(pair.Value ? includeDirectory(pair.Key) : include(pair.Key))) excluded.Set(pair.Key, pair.Value); }
            if (Directory.Exists(target))
            {
                new DirectoryInfo(source.StagingDirectory).SetAccessControl(new DirectoryInfo(target).GetAccessControl(AccessControlSections.Access));
                foreach (var stageDir in new HistoryRestoreTreeScope(source.Binding).Enumerate(source.StagingDirectory)
                    .Where(e => e.Directory).Select(e => e.FullPath).OrderBy(path => path.Length))
                {
                    var counterpart = Path.Combine(target, Path.GetRelativePath(source.StagingDirectory, stageDir));
                    if (Directory.Exists(counterpart)) new DirectoryInfo(stageDir).SetAccessControl(new DirectoryInfo(counterpart).GetAccessControl(AccessControlSections.Access));
                }
            }
            var stagedFiles = stage.Where(pair => !pair.Value && include(pair.Key)).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var neededDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            foreach (var path in stagedFiles.Concat(stage.Where(pair => pair.Value && includeDirectory(pair.Key)).Select(pair => pair.Key)))
            {
                var directory = stage.TryGetValue(path, out var isDirectory) && isDirectory ? path : Parent(path);
                while (true) { neededDirs.Add(directory); if (directory.Length == 0) break; directory = Parent(directory); }
            }
            var parentIds = new Dictionary<string, RestoreObjectIdentity>(StringComparer.OrdinalIgnoreCase);
            var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var retiredDirectories = new RestorePathIndex<bool>();
            var rollback = Path.Combine(container, "rollback-items");
            var rollbackRoot = WindowsRestoreFileSystem.CreateDirectoryNew(rollback, workspace.ContainerIdentity!); rollbackRoot.Handle.Dispose();
            var newDirs = Path.Combine(container, "new-directories");
            var newDirsRoot = WindowsRestoreFileSystem.CreateDirectoryNew(newDirs, workspace.ContainerIdentity!); newDirsRoot.Handle.Dispose();
            if (workspace.Location.TargetIdentity is not null) { parentIds[""] = workspace.Location.TargetIdentity; AddPath(target); }

            // 结构冲突先保全旧对象；目录内有边界外内容时不能以覆盖文件为由删除其整棵树。
            foreach (var path in neededDirs.Where(path => path.Length > 0 && current.TryGetValue(path, out var dir) && !dir).OrderBy(path => path.Count(c => c == '/')))
            {
                if (!include(path)) throw new IOException("Restore directory conflicts with a boundary-excluded file.");
                Retire(path, directory: false);
            }
            foreach (var path in stagedFiles.Where(path => current.TryGetValue(path, out var dir) && dir).OrderBy(path => path.Count(c => c == '/')))
            {
                if (!includeDirectory(path) || excluded.HasDescendants(path))
                    throw new IOException("Restore file conflicts with a directory containing boundary-excluded data.");
                Retire(path, directory: true);
            }
            foreach (var path in neededDirs.OrderBy(path => path.Count(c => c == '/')).ThenBy(path => path.Length))
            {
                if (parentIds.ContainsKey(path)) continue;
                if (current.TryGetValue(path, out var directory) && directory && !IsRetired(path))
                { var actual = Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)); parentIds[path] = WindowsRestoreFileSystem.Identity(actual); AddPath(actual); continue; }
                var temporary = Path.Combine(newDirs, "d-" + parentIds.Count);
                var created = WindowsRestoreFileSystem.CreateDirectoryNew(temporary, newDirsRoot.Identity); created.Handle.Dispose();
                var stageCounterpart = path.Length == 0 ? source.StagingDirectory : Path.Combine(source.StagingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(stageCounterpart))
                    new DirectoryInfo(temporary).SetAccessControl(new DirectoryInfo(stageCounterpart).GetAccessControl(AccessControlSections.Access));
                parentIds[path] = created.Identity;
                Move(source.Binding.SourceId, temporary, path.Length == 0 ? target : Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)),
                    created.Identity, newDirsRoot.Identity, path.Length == 0 ? workspace.Location.ParentIdentity : parentIds[Parent(path)], reset: true);
            }
            foreach (var pair in current.Where(pair => !pair.Value && (stagedFiles.Contains(pair.Key)
                    || source.ApplyMode == HistoryRestoreApplyMode.Clean && include(pair.Key) && !stagedFiles.Contains(pair.Key)))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                if (!IsRetired(pair.Key)) Retire(pair.Key, directory: false);
            foreach (var path in stagedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var from = Path.Combine(source.StagingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                Move(source.Binding.SourceId, from, Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar)),
                    WindowsRestoreFileSystem.Identity(from), WindowsRestoreFileSystem.Identity(Path.GetDirectoryName(from)!), parentIds[Parent(path)], reset: true);
            }
            if (source.ApplyMode == HistoryRestoreApplyMode.Clean)
                foreach (var path in current.Where(pair => pair.Value && includeDirectory(pair.Key) && !neededDirs.Contains(pair.Key))
                    .Select(pair => pair.Key).OrderByDescending(path => path.Length))
                    if (!IsRetired(path) && !excluded.HasDescendants(path))
                        Retire(path, directory: true, empty: true);

            var finalEntries = new Dictionary<string, bool>(current, StringComparer.OrdinalIgnoreCase);
            foreach (var path in current.Keys.Where(IsRetired)) finalEntries.Remove(path);
            foreach (var directory in neededDirs.Where(path => path.Length > 0)) finalEntries[directory] = true;
            foreach (var file in stagedFiles) finalEntries[file] = false;
            var finalIndex = Index(finalEntries.Keys);
            evidence.Add(new(source.Binding.SourceId, source.Version.VersionId, target, source.Binding.Boundary,
                finalEntries.Where(pair => !pair.Value && include(pair.Key)).Select(pair => pair.Key).ToArray(),
                finalEntries.Where(pair => pair.Value && includeDirectory(pair.Key) && !finalIndex.HasDescendants(pair.Key))
                    .Select(pair => pair.Key).ToArray(), source.Binding.ComparisonRoot));

            void Retire(string relative, bool directory, bool empty = false)
            {
                if (directory && !empty && protectCurrentFiles)
                {
                    foreach (var child in currentIndex.EntriesUnder(relative).OrderByDescending(p => p.Key.Length))
                        Retire(child.Key, child.Value, empty: child.Value);
                    empty = true;
                }
                var from = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(from)!;
                var identity = WindowsRestoreFileSystem.Identity(from);
                if (directory && !empty) foreach (var item in HistoryRestoreFileSystemPlanStore.CaptureTree(from)) Add(item);
                else AddPath(from);
                AddPath(parent);
                var to = Path.Combine(rollback, "old-" + actions.Count);
                Move(source.Binding.SourceId, from, to, identity, WindowsRestoreFileSystem.Identity(parent), rollbackRoot.Identity, empty);
                retired.Add(relative);
                if (directory) retiredDirectories.Set(relative, true);
            }
            bool IsRetired(string path) => retired.Contains(path) || retiredDirectories.HasAncestor(path);
        }
    }
    private static Dictionary<string, bool> Scan(string root, HistoryRestoreSourceBinding binding)
    {
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new HistoryRestoreTreeScope(binding).Enumerate(root, includeExcluded: true))
            if (!entries.TryAdd(entry.RelativePath, entry.Directory)) throw new IOException("Restore tree contains a case collision.");
        Services.HistoryOperationDiagnostics.Current?.Count("target-or-staging-scans");
        return entries;
    }
    private static string Parent(string path) => path.LastIndexOf('/') is var index && index >= 0 ? path[..index] : "";
    private static RestorePathIndex<bool> Index(IEnumerable<string> paths)
    {
        var index = new RestorePathIndex<bool>();
        foreach (var path in paths) index.Set(path, true);
        return index;
    }
}
