using FolderRewind.History.Storage;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public enum HistoryRepairStatus
{
    Rebuilt = 0,
    RepairRequired = 1,
    CompatibilityBlocked = 2
}

public sealed record HistoryRepairResult(
    HistoryRepairStatus Status,
    int HealthyPacks,
    ImmutableArray<string> QuarantinedPackPaths,
    ImmutableArray<string> Diagnostics);

public sealed class HistoryRepositoryRepairService
{
    private readonly HistoryPackCodec _codec = new();

    public async Task<HistoryRepairResult> RepairAsync(
        HistoryRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        await using var lease = await runtime.MutationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var healthy = new List<HistoryPackReadResult>();
        var quarantined = new List<string>();
        var diagnostics = new List<string>();
        foreach (var path in Directory.GetFiles(runtime.Repository.Paths.PacksRoot, "*.frpack", SearchOption.AllDirectories)
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var decoded = _codec.Decode(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                if (decoded.ContainsUnsupportedObjectSchema)
                {
                    diagnostics.Add($"CompatibilityBlocked: {Path.GetFileName(path)} contains an unknown object schema.");
                    return new(
                        HistoryRepairStatus.CompatibilityBlocked,
                        healthy.Count,
                        quarantined.ToImmutableArray(),
                        diagnostics.ToImmutableArray());
                }
                healthy.Add(decoded);
            }
            catch (HistoryPackCompatibilityException ex)
            {
                diagnostics.Add($"CompatibilityBlocked: {Path.GetFileName(path)}: {ex.Message}");
                return new(HistoryRepairStatus.CompatibilityBlocked, healthy.Count, [], diagnostics.ToImmutableArray());
            }
            catch (HistoryRepositoryException ex)
            {
                var target = runtime.Repository.Paths.GetQuarantinePath(null);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(path, target, overwrite: false);
                quarantined.Add(target);
                diagnostics.Add($"Quarantined corrupt Pack {Path.GetFileName(path)}: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"RepairRequired: cannot read Pack {Path.GetFileName(path)}: {ex.Message}");
                return new(
                    HistoryRepairStatus.RepairRequired,
                    healthy.Count,
                    quarantined.ToImmutableArray(),
                    diagnostics.ToImmutableArray());
            }
        }
        try
        {
            new HistoryRepositoryValidator(_codec).Validate(runtime.ConfigId, healthy);
        }
        catch (HistoryRepositoryValidationException ex)
        {
            diagnostics.Add("Reference validation failed after quarantine: " + ex.Message);
            return new(HistoryRepairStatus.RepairRequired, healthy.Count, quarantined.ToImmutableArray(), diagnostics.ToImmutableArray());
        }
        await runtime.Index.RebuildAsync(healthy, cancellationToken).ConfigureAwait(false);
        runtime.ChangeFeed.Publish(runtime.ConfigId, HistoryChangeKind.IndexRebuilt);
        return new(HistoryRepairStatus.Rebuilt, healthy.Count, quarantined.ToImmutableArray(), diagnostics.ToImmutableArray());
    }
}
