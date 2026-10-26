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
        IReadOnlyList<string>? preservePaths = null, History.Storage.HistoryWorkingTreeReadLease? currentInput = null)
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
        using (var current = new History.Storage.HistoryRestoreSourceReadView(binding.TargetDirectory, binding, currentInput))
        using (var target = new History.Storage.HistoryRestoreSourceReadView(staging, binding))
        {
            foreach (var proposal in await RestoreWhitelistPreparation.PrepareAsync(current, target,
                config.Filters.RestoreWhitelist.Concat((additionalRestoreWhitelist ?? []).Select(PathFilterRule.Legacy)), binding.ComparisonRoot, token, config.Filters.UseRegex).ConfigureAwait(false))
                proposed.Add(proposal.RelativePath, proposal);
            if (lease is not null)
            {
                using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
                var result = await lease.Capability.PrepareAsync(new(operationId,
                    PluginV3ModelMapper.ToSnapshot(config), binding.SourceId.Value, current, target, preservePlayerData)
                    { PreservePlayerDataOverride = preservePlayerDataOverride }, lease.Context).ConfigureAwait(false);
                OperationDiagnosticFormatter.Log(result.Diagnostics, "Restore preparation", nameof(PluginV3RestoreStagingPreparation));
                var error = result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                if (error is not null) throw new InvalidDataException(error.Code +
                    (error.Arguments.TryGetValue("message", out var message) ? ": " + message : string.Empty));
                // Freeze and validate plugin output before combining it with host-owned whitelist output.
                foreach (var proposal in RestoreStagingProposalValidator.ValidateAndFreeze(result, include))
                    proposed[proposal.RelativePath] = proposal;
            }
            var preserve = await RestorePreservePreparation.PrepareAsync(current, target, preservePaths ?? [],
                kind.OwnerId.Value == "com.folderrewind.minerewind", include, token,
                proposed.Keys.Concat(current.EnumerateAllRelativePaths()).Concat(target.EnumerateAllRelativePaths())).ConfigureAwait(false);
            deletes = preserve.Deletes;
            foreach (var path in deletes) proposed.Remove(path);
            foreach (var proposal in preserve.Files) proposed[proposal.RelativePath] = proposal;
            if (proposed.Count + deletes.Count > 4096) throw new InvalidDataException("Restore preparation exceeds file limit.");
            proposals = RestoreStagingProposalValidator.ValidateAndFreeze(new(proposed.Values.ToArray(), []), include);
        }
        bool changed = false;
        using var space = ArtifactSpaceBudget.Shared.Reserve(new ArtifactSpaceRequest(staging,
            checked(proposals.Sum(p => (long)p.Content.Length + 4096))));
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

}
