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
        HistoryRestoreSourceBinding binding, string staging, CancellationToken token,
        bool? preservePlayerDataOverride = null, IReadOnlyList<string>? additionalRestoreWhitelist = null,
        IReadOnlyList<string>? preservePaths = null)
    {
        var kind = PluginV3ModelMapper.ToKind(config);
        using var lease = kind.OwnerId.Value == "folderrewind.core" ? null
            : PluginV3RuntimeService.Runtime.TryAcquire<IRestoreStagingPreparationCapability>(
                new PluginId(kind.OwnerId.Value), capability => capability.Kind == kind, token);
        if (lease is null && (preservePlayerData || preservePlayerDataOverride.HasValue))
            throw new InvalidOperationException("Restore staging preparation is unavailable.");
        if (preservePlayerDataOverride.HasValue && lease?.Capability.SupportsPlayerDataOverride != true)
            throw new InvalidOperationException("Plugin does not support explicit player-data overrides.");
        var include = FileSystemHistoryRestoreMutationBackend.CreateBoundaryMatcher(binding);
        var proposed = new Dictionary<string, RestoreStagedFileProposal>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RestoreStagedFileProposal> proposals;
        IReadOnlyList<string> deletes = [];
        using (var current = new LockedView(binding.TargetDirectory, include))
        using (var target = new LockedView(staging, include))
        {
            foreach (var proposal in await RestoreWhitelistPreparation.PrepareAsync(current, target,
                config.Filters.RestoreWhitelist.Concat(additionalRestoreWhitelist ?? []), binding.TargetDirectory, token).ConfigureAwait(false))
                proposed.Add(proposal.RelativePath, proposal);
            if (lease is not null)
            {
                using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
                var result = await lease.Capability.PrepareAsync(new(operationId,
                    PluginV3ModelMapper.ToSnapshot(config), binding.SourceId.Value, current, target, preservePlayerData)
                    { PreservePlayerDataOverride = preservePlayerDataOverride }, lease.Context).ConfigureAwait(false);
                var error = result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (error is not null) throw new InvalidDataException(error.Code +
                    (error.Arguments.TryGetValue("message", out var message) ? ": " + message : string.Empty));
                // Freeze and validate plugin output before combining it with host-owned whitelist output.
                foreach (var proposal in RestoreStagingProposalValidator.ValidateAndFreeze(result, include))
                    proposed[proposal.RelativePath] = proposal;
                foreach (var diagnostic in result.Diagnostics) LogService.LogWarning(diagnostic.Code, "Restore preparation");
            }
            var preserve = await RestorePreservePreparation.PrepareAsync(current, target, preservePaths ?? [],
                kind.OwnerId.Value == "com.folderrewind.minerewind", include, token,
                proposed.Keys.Concat(current.AllRelativePaths).Concat(target.AllRelativePaths)).ConfigureAwait(false);
            deletes = preserve.Deletes;
            foreach (var path in deletes) proposed.Remove(path);
            foreach (var proposal in preserve.Files) proposed[proposal.RelativePath] = proposal;
            if (proposed.Count + deletes.Count > 4096) throw new InvalidDataException("Restore preparation exceeds file limit.");
            proposals = RestoreStagingProposalValidator.ValidateAndFreeze(new(proposed.Values.ToArray(), []), include);
        }
        bool changed = false;
        foreach (var relative in deletes)
        {
            token.ThrowIfCancellationRequested();
            var path = ArtifactPathRules.ResolveUnderRoot(staging, relative);
            if (File.Exists(path)) { File.Delete(path); changed = true; }
        }
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
    private sealed class LockedView : IRestoreSourceView, IDisposable
    {
        private readonly Dictionary<string, FileStream> _files = new(StringComparer.Ordinal);
        private readonly List<string> _allRelativePaths = [];
        public IReadOnlyList<string> AllRelativePaths => _allRelativePaths.AsReadOnly();
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
                        _allRelativePaths.Add(relative);
                        if (include(relative)) _files.Add(relative, new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public IReadOnlyList<string> RelativePaths => Array.AsReadOnly(_files.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray());
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
