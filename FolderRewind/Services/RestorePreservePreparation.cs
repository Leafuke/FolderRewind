using FolderRewind.Plugin.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record RestorePreservePlan(IReadOnlyList<RestoreStagedFileProposal> Files, IReadOnlyList<string> Deletes);

internal static class RestorePreservePreparation
{
    public static async Task<RestorePreservePlan> PrepareAsync(IRestoreSourceView current,
        IRestoreSourceView target, IEnumerable<string> paths, bool minecraft, Func<string, bool> include,
        CancellationToken token, IEnumerable<string>? extraTargetPaths = null)
    {
        var selectors = RestorePreservePaths.Normalize(paths);
        if (selectors.Length == 0) return new([], []);
        var prefix = "";
        if (minecraft)
        {
            var roots = current.RelativePaths.Where(path => path == "level.dat"
                || path.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (roots.Length != 1) throw new InvalidDataException("Preservation requires one managed Minecraft world.");
            prefix = roots[0][..^"level.dat".Length];
        }
        selectors = selectors.Select(path => prefix + path).ToArray();
        foreach (var selector in selectors)
            if (!include(selector.EndsWith('/') ? selector + "__preserve_boundary_probe__" : selector))
                throw new InvalidDataException("Preserve selector is outside the managed boundary.");
        bool Selected(string path) => selectors.Any(selector => RestorePreservePaths.Matches(selector, path));
        var currentPaths = current.RelativePaths.Where(Selected).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetPaths = target.RelativePaths.Concat(extraTargetPaths ?? []).Where(Selected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (currentPaths.Concat(targetPaths).Any(path => !include(path)))
            throw new InvalidDataException("Preservation cannot expand the managed boundary.");
        var deletes = targetPaths.Where(path => !currentPaths.Contains(path)).ToArray();
        if (currentPaths.Count + deletes.Length > 4096) throw new InvalidDataException("Preserve preparation exceeds file limit.");
        var files = new List<RestoreStagedFileProposal>();
        long bytes = 0;
        foreach (var path in currentPaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            await using var input = await current.OpenReadAsync(path, token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int count;
            while ((count = await input.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
            {
                bytes = checked(bytes + count);
                if (bytes > 64 * 1024 * 1024) throw new InvalidDataException("Preserve preparation exceeds byte limit.");
                buffer.Write(chunk, 0, count);
            }
            files.Add(new(path, buffer.ToArray()));
        }
        return new(files.AsReadOnly(), Array.AsReadOnly(deletes));
    }
}
