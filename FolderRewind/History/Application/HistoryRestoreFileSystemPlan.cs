using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace FolderRewind.History.Application;

internal sealed record RestorePlanObject(string Path, RestoreObjectIdentity Identity, FileAttributes Attributes);
internal sealed record RestoreMoveAction(int ActionId, SourceId Source, string From, string To,
    RestoreObjectIdentity Identity, RestoreObjectIdentity FromParent, RestoreObjectIdentity ToParent, bool RequireEmpty = false,
    bool ResetPublishedInheritance = false, string? PublishedRootSecurity = null);
internal sealed record HistoryRestoreFileSystemPlan(HistoryTransactionId TransactionId,
    IReadOnlyList<string> Containers, IReadOnlyList<RestorePlanObject> Objects, IReadOnlyList<RestoreMoveAction> Actions,
    IReadOnlyList<RestoreSourcePlanEvidence>? Sources = null);
internal sealed record RestoreSourcePlanEvidence(SourceId Source, VersionId Version, string Target,
    EffectiveSourceBoundarySnapshot Boundary, IReadOnlyList<string> Files, IReadOnlyList<string> EmptyDirectories);
internal sealed record RestorePlanProof(string Path, long Length, string Sha256);

/// <summary>不可变意图与线性进度。恢复方向由 Workspace/Pack 判定，进度只作经过校验的提示。</summary>
internal static class HistoryRestoreFileSystemPlanStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static RestorePlanProof Save(string path, HistoryRestoreFileSystemPlan plan)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("plan-save-flush");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Options);
        AtomicFileService.Write(path, stream => stream.Write(bytes));
        HistoryOperationDiagnostics.Current?.Count("journal-flushes");
        return new(Path.GetFullPath(path), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    internal static HistoryRestoreFileSystemPlan Load(RestorePlanProof proof, HistoryTransactionId transaction)
    {
        var bytes = File.ReadAllBytes(proof.Path);
        if (bytes.LongLength != proof.Length || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(proof.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore immutable plan proof mismatch.");
        var plan = JsonSerializer.Deserialize<HistoryRestoreFileSystemPlan>(bytes, Options) ?? throw new InvalidDataException("Restore plan is empty.");
        if (plan.TransactionId != transaction || plan.Actions.Select(a => a.ActionId).Where((id, index) => id != index).Any()
            || plan.Containers.Count == 0 || plan.Objects.GroupBy(o => o.Identity).Any(g => g.Count() > 1))
            throw new InvalidDataException("Restore plan identity or action sequence is invalid.");
        return plan;
    }
    internal static IReadOnlyList<RestorePlanObject> CaptureTree(string root)
    {
        var objects = new List<RestorePlanObject>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var path))
        {
            var id = WindowsRestoreFileSystem.Identity(path);
            objects.Add(new(Path.GetFullPath(path), id, File.GetAttributes(path)));
            if (id.Directory)
                foreach (var entry in Directory.EnumerateFileSystemEntries(path)) pending.Push(entry);
        }
        return objects;
    }
    internal static Dictionary<string, RestorePlanObject> FinalLocations(HistoryRestoreFileSystemPlan plan, bool applied)
        => Locations(plan, applied ? plan.Actions.Count : 0);
    private static Dictionary<string, RestorePlanObject> Locations(HistoryRestoreFileSystemPlan plan, int completedActions)
    {
        var result = new Dictionary<string, RestorePlanObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Objects)
            if (!result.TryAdd(item.Path, item)) throw new InvalidDataException("Restore plan has a location collision.");
        foreach (var action in plan.Actions.Take(completedActions)) UpdateLocations(result, action);
        return result;
    }
    private static RestorePlanObject[] Members(Dictionary<string, RestorePlanObject> locations, RestoreMoveAction action)
        => action.Identity.Directory ? locations.Values.Where(item => item.Path.Equals(action.From, StringComparison.OrdinalIgnoreCase)
            || item.Path.StartsWith(action.From + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray()
            : locations.TryGetValue(action.From, out var file) ? [file] : [];
    private static void UpdateLocations(Dictionary<string, RestorePlanObject> locations, RestoreMoveAction action)
    {
        var members = Members(locations, action);
        foreach (var item in members) locations.Remove(item.Path);
        foreach (var item in members)
        {
            var path = item.Path.Equals(action.From, StringComparison.OrdinalIgnoreCase) ? action.To
                : Path.Combine(action.To, Path.GetRelativePath(action.From, item.Path));
            if (!locations.TryAdd(path, item with { Path = path })) throw new InvalidDataException("Restore plan has a location collision.");
        }
    }
    internal static IDisposable Execute(HistoryRestoreFileSystemPlan plan, string progressPath, CancellationToken token)
    {
        using var stableParents = ProtectExternalParents(plan);
        // Windows 禁止移动有已打开子项的非空目录。目录动作由原语持有根/父目录句柄，
        // 成员身份在移动前后复核；移动后再为成员建立写入保护，不关闭已取得的文件保护去强行交换。
        var directoryMoves = plan.Actions.Where(action => action.Identity.Directory).ToArray();
        using var inputReads = ReadLease.Acquire(FinalLocations(plan, false).Values.Where(item =>
            !directoryMoves.Any(action => item.Path.StartsWith(action.From + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))), allowRename: true);
        using var movedReads = new CompositeLease();
        using var progress = new RestoreActionProgress(progressPath, plan);
        var currentLocations = Locations(plan, 0);
        SourceId? previousSource = null;
        foreach (var action in plan.Actions)
        {
            if (previousSource is not null && previousSource != action.Source) progress.Flush();
            previousSource = action.Source;
            token.ThrowIfCancellationRequested();
            Reach("before-move-" + action.ActionId);
            var source = WindowsRestoreFileSystem.TryIdentity(action.From); var destination = WindowsRestoreFileSystem.TryIdentity(action.To);
            if (source != action.Identity || destination is not null) throw new IOException("Restore prepared locations changed before mutation.");
            var movedMembers = Members(currentLocations, action);
            foreach (var item in movedMembers)
                if (WindowsRestoreFileSystem.TryIdentity(item.Path) != item.Identity) throw new IOException("Restore directory membership identity changed.");
            if (action.RequireEmpty && Directory.EnumerateFileSystemEntries(action.From).Any()) throw new IOException("Restore directory gained external members.");
            WindowsRestoreFileSystem.MoveOwnedNoReplace(action.From, action.To, action.Identity, action.FromParent, action.ToParent, stableParents is not null);
            ApplyPublishedSecurity(action, stableParents is not null);
            UpdateLocations(currentLocations, action);
            movedReads.Add(ReadLease.Acquire(movedMembers.Select(item => item with {
                Path = StringComparer.OrdinalIgnoreCase.Equals(item.Path, action.From) ? action.To : Path.Combine(action.To, Path.GetRelativePath(action.From, item.Path)) }), allowRename: false));
            Reach("after-move-" + action.ActionId);
            progress.Append(action.ActionId);
        }
        progress.Flush();
        ValidateFinal(plan, true);
        return ReadLease.Acquire(FinalLocations(plan, true).Values, allowRename: false);
    }
    private static void ApplyPublishedSecurity(RestoreMoveAction action, bool ancestorsProtected)
    {
        if (!action.ResetPublishedInheritance && action.PublishedRootSecurity is null) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restore security requires Windows.");
        using var parents = !ancestorsProtected || action.Identity.Directory
            ? new WindowsDirectoryReadLease(action.Identity.Directory ? Path.Combine(action.To, "unused-child") : action.To) : null;
        using var file = action.Identity.Directory ? null : new FileStream(action.To, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write);
        if (WindowsRestoreFileSystem.Identity(action.To) != action.Identity) throw new IOException("Published security object changed.");
        if (action.Identity.Directory)
        {
            var security = new DirectorySecurity();
            if (action.PublishedRootSecurity is { } descriptor)
                security.SetSecurityDescriptorSddlForm(descriptor, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
            else security.SetAccessRuleProtection(false, false);
            new DirectoryInfo(action.To).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity(); security.SetAccessRuleProtection(false, false);
            new FileInfo(action.To).SetAccessControl(security);
        }
    }
    internal static void Rollback(HistoryRestoreFileSystemPlan plan)
    {
        using var stableParents = ProtectExternalParents(plan);
        var known = plan.Objects.Select(o => o.Identity).ToHashSet();
        foreach (var action in plan.Actions.Reverse())
        {
            var source = WindowsRestoreFileSystem.TryIdentity(action.From); var destination = WindowsRestoreFileSystem.TryIdentity(action.To);
            if (source == action.Identity && (destination is null || known.Contains(destination))) continue;
            if (destination == action.Identity && source is null)
            {
                Reach("before-undo-" + action.ActionId);
                WindowsRestoreFileSystem.MoveOwnedNoReplace(action.To, action.From, action.Identity, action.ToParent, action.FromParent, stableParents is not null);
                Reach("after-undo-" + action.ActionId);
                continue;
            }
            // 陌生对象、重复身份或丢失对象都不是“尚未执行”，不猜测覆盖或删除。
            throw new IOException("Restore rollback requires recovery: ambiguous object placement.");
        }
    }
    private static IDisposable? ProtectExternalParents(HistoryRestoreFileSystemPlan plan)
    {
        var parents = plan.Containers.Select(root => Path.GetDirectoryName(root)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = parents.Select(path => plan.Objects.SingleOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (known.Any(item => item is null)) return null;
        var lease = new CompositeLease();
        try
        {
            // 整个事务固定 Source 外的祖先；每次移动仍固定直接父目录并核对其身份。
            // Source 内目录按计划移动，不持有将妨碍自身改名的目录 DELETE 保护。
            foreach (var parent in known)
            {
                lease.Add(new WindowsDirectoryReadLease(Path.Combine(parent!.Path, "unused-child")));
                if (WindowsRestoreFileSystem.Identity(parent.Path) != parent.Identity) throw new IOException("Restore external parent changed.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal static void Cleanup(HistoryRestoreFileSystemPlan plan, bool applied, bool allowMissing)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("owned-cleanup");
        var expected = FinalLocations(plan, applied);
        var owned = expected.Where(pair => plan.Containers.Any(root => HistoryRestoreWorkspacePlanner.Overlaps(root, pair.Key)
            && (StringComparer.OrdinalIgnoreCase.Equals(root, pair.Key) || pair.Key.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var root in plan.Containers)
        {
            if (!Path.Exists(root)) { if (!allowMissing) throw new IOException("Restore owned container disappeared."); continue; }
            foreach (var actual in CaptureTree(root))
                if (!owned.TryGetValue(actual.Path, out var planned) || planned.Identity != actual.Identity)
                    throw new IOException("Restore cleanup found an external member; preserving the container.");
        }
        foreach (var item in owned.Values.OrderByDescending(o => o.Path.Length))
        {
            var identity = WindowsRestoreFileSystem.TryIdentity(item.Path);
            if (identity is null) { if (!allowMissing) throw new IOException("Restore cleanup member is missing."); continue; }
            if (identity != item.Identity) throw new IOException("Restore cleanup member was replaced.");
            WindowsRestoreFileSystem.DeleteOwned(item.Path, item.Identity);
            Reach("after-cleanup-object");
        }
    }
    internal static void ValidateFinal(HistoryRestoreFileSystemPlan plan, bool applied)
    {
        foreach (var item in FinalLocations(plan, applied).Values)
            if (WindowsRestoreFileSystem.TryIdentity(item.Path) != item.Identity) throw new IOException("Restore final object identities disagree with the plan.");
    }
    internal static void ValidatePublished(HistoryRestoreFileSystemPlan plan)
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("published-state-check");
        ValidateOutsideContainers(plan, true);
        foreach (var source in plan.Sources ?? [])
        {
            HistoryOperationDiagnostics.Current?.Count("published-tree-scans");
            var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(new(source.Source, source.Target, source.Boundary));
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(source.Target);
            while (pending.TryPop(out var directory))
            {
                var entries = Directory.GetFileSystemEntries(directory);
                var relativeDirectory = Path.GetRelativePath(source.Target, directory).Replace('\\', '/');
                if (relativeDirectory != "." && entries.Length == 0 && include(relativeDirectory)) empty.Add(relativeDirectory);
                foreach (var path in entries)
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Published restore gained a link.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else { var relative = Path.GetRelativePath(source.Target, path).Replace('\\', '/'); if (include(relative)) files.Add(relative); }
                }
            }
            if (!files.SetEquals(source.Files) || !empty.SetEquals(source.EmptyDirectories))
                throw new IOException("Published managed paths changed outside the restore plan.");
        }
        var expected = FinalLocations(plan, true);
        bool InContainer(string path) => plan.Containers.Any(root => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        foreach (var action in plan.Actions.Where(action => action.Identity.Directory && InContainer(action.From) && !InContainer(action.To)))
        {
            if (plan.Sources?.Any(source => source.Target.Equals(action.To, StringComparison.OrdinalIgnoreCase)
                && source.Boundary.ScopeMode == EffectiveBoundaryScopeMode.All
                && source.Boundary.FilterMode == EffectiveBoundaryFilterMode.Blacklist && source.Boundary.FilterRules.IsEmpty) == true) continue;
            foreach (var item in CaptureTree(action.To))
                if (!expected.TryGetValue(item.Path, out var known) || known.Identity != item.Identity)
                    throw new IOException("Published restore tree contains an external member.");
        }
    }
    internal static void ValidateOutsideContainers(HistoryRestoreFileSystemPlan plan, bool applied)
    {
        foreach (var item in FinalLocations(plan, applied).Values.Where(item => !plan.Containers.Any(root =>
            item.Path.Equals(root, StringComparison.OrdinalIgnoreCase) || item.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
            if (WindowsRestoreFileSystem.TryIdentity(item.Path) != item.Identity)
                throw new IOException("Restore target identities changed; preserving rollback data.");
    }
    private sealed class ReadLease : IDisposable
    {
        private readonly List<FileStream> _files = [];
        internal static ReadLease Acquire(IEnumerable<RestorePlanObject> objects, bool allowRename)
        {
            var lease = new ReadLease();
            try
            {
                foreach (var item in objects.Where(o => !o.Identity.Directory))
                {
                    var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read,
                        allowRename ? FileShare.Read | FileShare.Delete : FileShare.Read);
                    lease._files.Add(file);
                    if (WindowsRestoreFileSystem.Identity(file.SafeFileHandle) != item.Identity)
                        throw new IOException("Restore prepared file identity changed.");
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose() { foreach (var file in _files) file.Dispose(); _files.Clear(); }
    }
    private sealed class CompositeLease : IDisposable
    {
        private readonly List<IDisposable> _items = [];
        internal void Add(IDisposable lease) => _items.Add(lease);
        public void Dispose() { foreach (var item in _items) item.Dispose(); _items.Clear(); }
    }
    internal static void Reach(string point)
    {
#if FOLDERREWIND_PROTOCOL_TESTS
        TestFault?.Invoke(point);
#endif
    }
#if FOLDERREWIND_PROTOCOL_TESTS
    internal static Action<string>? TestFault;
#endif
}

internal sealed class RestoreActionProgress : IDisposable
{
    private readonly FileStream _stream;
    private int _sequence;
    private int _pending;
    internal RestoreActionProgress(string path, HistoryRestoreFileSystemPlan plan)
    {
        if (File.Exists(path)) throw new IOException("Restore progress already exists; recover the transaction first.");
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
    }
    internal void Append(int action)
    {
        var payload = Encoding.UTF8.GetBytes($"{_sequence}:{action}");
        var record = new byte[12 + payload.Length + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record, payload.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), _sequence);
        SHA256.HashData(record.AsSpan(0, 8)).AsSpan(0, 4).CopyTo(record.AsSpan(8));
        payload.CopyTo(record.AsSpan(12)); SHA256.HashData(payload).CopyTo(record.AsSpan(12 + payload.Length));
        _stream.Write(record); _sequence++; _pending++;
        HistoryOperationDiagnostics.Current?.Count("progress-bytes", record.Length);
        if (_pending >= 64) Flush();
    }
    internal void Flush()
    {
        using var timing = HistoryOperationDiagnostics.Current?.Measure("progress-flush");
        _stream.Flush(true); _pending = 0; HistoryOperationDiagnostics.Current?.Count("journal-flushes");
        HistoryRestoreFileSystemPlanStore.Reach("after-progress-flush");
    }
    internal static void Validate(string path, HistoryRestoreFileSystemPlan plan)
    {
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path); int sequence = 0; var header = new byte[12];
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < header.Length) return; // 只允许明确的末尾半条记录。
            stream.ReadExactly(header);
            if (!SHA256.HashData(header.AsSpan(0, 8)).AsSpan(0, 4).SequenceEqual(header.AsSpan(8)))
                throw new InvalidDataException("Restore progress header checksum mismatch.");
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
            int number = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 3 or > 128 || number != sequence || sequence >= plan.Actions.Count) throw new InvalidDataException("Restore progress is corrupt or out of order.");
            if (stream.Length - stream.Position < length + 32) return;
            var bytes = new byte[length + 32]; stream.ReadExactly(bytes);
            if (!SHA256.HashData(bytes.AsSpan(0, length)).AsSpan().SequenceEqual(bytes.AsSpan(length))
                || Encoding.UTF8.GetString(bytes, 0, length) != $"{sequence}:{plan.Actions[sequence].ActionId}")
                throw new InvalidDataException("Restore progress checksum or action mismatch.");
            sequence++;
        }
    }
    public void Dispose() { _stream.Dispose(); }
}
