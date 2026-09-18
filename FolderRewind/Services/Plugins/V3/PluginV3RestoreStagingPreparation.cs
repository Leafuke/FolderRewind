using FolderRewind.History.Application;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

internal static class PluginV3RestoreStagingPreparation
{
    public static async Task<bool> PrepareAsync(BackupConfig config, Guid operationId, bool preservePlayerData,
        HistoryRestoreSourceBinding binding, string staging, CancellationToken token)
    {
        var kind = PluginV3ModelMapper.ToKind(config);
        if (kind.OwnerId.Value == "folderrewind.core") return false;
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<IRestoreStagingPreparationCapability>(
            new PluginId(kind.OwnerId.Value), capability => capability.Kind == kind, token);
        if (lease is null)
        {
            if (preservePlayerData) throw new InvalidOperationException("Restore staging preparation is unavailable.");
            return false;
        }
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding);
        IReadOnlyList<RestoreStagedFileProposal> proposals;
        using (var current = new LockedView(binding.TargetDirectory, include))
        using (var target = new LockedView(staging, include))
        using (NativeHostMutationContext.EnterCoordinatorCallback())
        {
            var result = await lease.Capability.PrepareAsync(new(operationId,
                PluginV3ModelMapper.ToSnapshot(config), binding.SourceId.Value, current, target, preservePlayerData),
                lease.Context).ConfigureAwait(false);
            proposals = RestoreStagingProposalValidator.ValidateAndFreeze(result, include);
            foreach (var diagnostic in result.Diagnostics) LogService.LogWarning(diagnostic.Code, "Restore preparation");
        }
        bool changed = false;
        foreach (var proposal in proposals)
        {
            var path = ArtifactPathRules.ResolveUnderRoot(staging, proposal.RelativePath);
            if (File.Exists(path) && (await File.ReadAllBytesAsync(path, token).ConfigureAwait(false))
                .AsSpan().SequenceEqual(proposal.Content.Span)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, proposal.Content.ToArray(), token).ConfigureAwait(false);
            changed = true;
        }
        return changed;
    }

    // 插件只能读取复制出的流；文件锁在整个 preparation 回调期间保留。
    private sealed class LockedView : IVersionMetadataSourceView, IDisposable
    {
        private readonly Dictionary<string, FileStream> _files = new(StringComparer.Ordinal);
        public LockedView(string root, Func<string, bool> include)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.TryPop(out var directory))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Restore preparation cannot follow links.");
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Restore preparation cannot follow links.");
                        if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (include(relative)) _files.Add(relative, new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ArtifactPathRules.NormalizeRelativePath(relativePath);
            if (!_files.TryGetValue(path, out var input)) throw new FileNotFoundException(path);
            lock (input)
            {
                if (input.Length > 64 * 1024 * 1024) throw new IOException("Restore preparation input exceeds limit.");
                input.Position = 0;
                using var buffer = new MemoryStream(); input.CopyTo(buffer);
                return ValueTask.FromResult<Stream>(new MemoryStream(buffer.ToArray(), writable: false));
            }
        }
        public void Dispose() { foreach (var stream in _files.Values) stream.Dispose(); _files.Clear(); }
    }
}
