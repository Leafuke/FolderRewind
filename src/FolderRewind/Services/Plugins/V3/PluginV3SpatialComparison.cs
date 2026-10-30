using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Plugins.V3;

/// <summary>One bounded, read-only invocation lifetime. Closing the enumerator drains plugin state before input leases end.</summary>
internal static class PluginV3SpatialComparison
{
    internal static async Task<SpatialComparisonDescription> DescribeAsync(SpatialComparisonRequest request, CancellationToken token)
    {
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<ISpatialComparisonCapability>(new(request.Left.Config.Kind.OwnerId.Value), c => c.Kind == request.Left.Config.Kind, token)
            ?? throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ComparisonUnavailable"));
        using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
        var result = await lease.Capability.DescribeComparisonAsync(request, lease.Context).ConfigureAwait(false);
        if (result.Categories.Count > 32 || result.Categories.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 256)
            || result.Categories.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != result.Categories.Count)
            throw new InvalidDataException("Invalid comparison category catalog.");
        return new(Array.AsReadOnly(result.Categories.Select(c => c with { Name = FreezeText(c.Name) }).ToArray()));
    }
    internal static async IAsyncEnumerable<SpatialComparisonPage> ReadAsync(SpatialComparisonRequest initial,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (initial.Left.FixedInput is null || initial.Right.FixedInput is null
            || initial.Left.Config.ConfigId != initial.Right.Config.ConfigId || initial.Left.Config.Kind != initial.Right.Config.Kind
            || initial.Left.Folder.FolderId != initial.Right.Folder.FolderId)
            throw new InvalidDataException("Spatial comparison requires two fixed views of the same Source.");
        using var lease = PluginV3RuntimeService.Runtime.TryAcquire<ISpatialComparisonCapability>(new(initial.Left.Config.Kind.OwnerId.Value), c => c.Kind == initial.Left.Config.Kind, token)
            ?? throw new InvalidOperationException(I18n.GetString("SpatialWorkspace_ComparisonUnavailable"));
        using var callback = NativeHostMutationContext.EnterCoordinatorCallback();
        try
        {
            string? cursor = null; long total = 0, scanned = 0; var cursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                token.ThrowIfCancellationRequested();
                var page = await lease.Capability.CompareAsync(initial with { Cursor = cursor, Count = 500 }, lease.Context).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (page.Changes.Count > 500 || page.ScannedFiles < scanned || page.NextCursor?.Length > 4096
                    || page.NextCursor is not null && (!cursors.Add(page.NextCursor) || cursors.Count > 100_000)
                    || (total += page.Changes.Count) > 1_000_000)
                    throw new InvalidDataException("Spatial comparison exceeded its progress or result budget.");
                scanned = page.ScannedFiles; cursor = page.NextCursor;
                var rows = page.Changes.Select(Freeze).ToArray();
                yield return new(Array.AsReadOnly(rows), cursor, scanned);
            } while (cursor is not null);
        }
        finally
        {
            try { await lease.Capability.CloseComparisonAsync(initial.ComparisonId, lease.Context).ConfigureAwait(false); }
            catch (Exception ex) { LogService.LogWarning(ex.ToString(), "Spatial comparison cleanup"); }
        }
    }
    private static SpatialChange Freeze(SpatialChange row)
    {
        if (string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 4096 || !Enum.IsDefined(row.Kind)
            || row.Components.Count > 16 || row.Components.Any(c => c is null || c.Length > 256)
            || row.SourcePaths.Count > 128 || row.SourcePaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 4096 || Path.IsPathRooted(p) || p.Replace('\\', '/').Split('/').Any(s => s is "." or ".." or "")))
            throw new InvalidDataException("Invalid spatial comparison item.");
        if (row.Location is { } l && (string.IsNullOrWhiteSpace(l.WorldId) || l.WorldId.Length > 4096 || l.LayerId?.Length > 256
            || l.Bounds is { } b && (new[] { b.MinX, b.MinY, b.MaxX, b.MaxY }.Any(v => !double.IsFinite(v)) || b.MinX >= b.MaxX || b.MinY >= b.MaxY)))
            throw new InvalidDataException("Invalid spatial comparison location.");
        return row with { Name = FreezeText(row.Name), Description = FreezeText(row.Description), Components = Array.AsReadOnly(row.Components.ToArray()), SourcePaths = Array.AsReadOnly(row.SourcePaths.ToArray()) };
    }
    private static LocalizedText FreezeText(LocalizedText text)
    {
        if (text.Default is null || text.Default.Length > 16_384 || text.Translations.Count > 32
            || text.Translations.Any(p => p.Key.Length > 128 || p.Value is null || p.Value.Length > 16_384))
            throw new InvalidDataException("Spatial text budget exceeded.");
        return new(text.Default, new ReadOnlyDictionary<string, string>(text.Translations.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)));
    }
}
