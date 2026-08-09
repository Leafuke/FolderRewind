using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FolderRewind.Services.Discovery;

public static class DiscoveryThreeWayReviewService
{
    private const string MissingFingerprint = "<missing>";

    public static IReadOnlyList<DiscoverySourceChange> Review(
        ReviewedDiscoveryBaseline? previousBaseline,
        IEnumerable<ReviewedDiscoverySource>? currentSources,
        ReviewedDiscoveryBaseline? discoveredBaseline,
        IReadOnlySet<string>? defaultSelectedRoots = null)
    {
        var previous = Index(previousBaseline?.Sources);
        var current = Index(currentSources);
        var discovered = Index(discoveredBaseline?.Sources);
        var overrides = (previousBaseline?.UserOverrides ?? new ObservableCollection<ReviewedDiscoveryOverride>())
            .GroupBy(item => Normalize(item.NormalizedRootPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var keys = previous.Keys.Concat(discovered.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoverySourceChange>();

        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var oldSource);
            current.TryGetValue(key, out var currentSource);
            discovered.TryGetValue(key, out var newSource);
            var oldFingerprint = Fingerprint(oldSource);
            var currentFingerprint = Fingerprint(currentSource);
            var newFingerprint = Fingerprint(newSource);

            if (oldFingerprint == newFingerprint)
            {
                if (currentFingerprint == oldFingerprint)
                {
                    continue;
                }
                if (overrides.TryGetValue(key, out var reviewedOverride)
                    && reviewedOverride.UpstreamFingerprint == oldFingerprint
                    && reviewedOverride.CurrentFingerprint == currentFingerprint)
                {
                    continue;
                }
                result.Add(Change(key, DiscoverySourceChangeKind.UserModified, oldSource, currentSource, newSource, false, false));
                continue;
            }

            if (oldSource == null)
            {
                var conflict = currentSource != null && currentFingerprint != newFingerprint;
                var selected = !conflict
                               && currentSource == null
                               && (defaultSelectedRoots?.Contains(key) ?? true);
                result.Add(Change(
                    key,
                    conflict ? DiscoverySourceChangeKind.Conflict : DiscoverySourceChangeKind.Added,
                    null,
                    currentSource,
                    newSource,
                    true,
                    selected));
                continue;
            }

            if (newSource == null)
            {
                var conflict = currentSource != null && currentFingerprint != oldFingerprint;
                result.Add(Change(
                    key,
                    conflict ? DiscoverySourceChangeKind.Conflict : DiscoverySourceChangeKind.Removed,
                    oldSource,
                    currentSource,
                    null,
                    currentSource != null,
                    false));
                continue;
            }

            var userChanged = currentFingerprint != oldFingerprint;
            var kind = userChanged ? DiscoverySourceChangeKind.Conflict : DiscoverySourceChangeKind.Changed;
            var selectedByDiscovery = defaultSelectedRoots?.Contains(key) ?? true;
            result.Add(Change(
                key,
                kind,
                oldSource,
                currentSource,
                newSource,
                true,
                !userChanged && selectedByDiscovery && IsExpansion(oldSource, newSource)));
        }
        return result;
    }

    public static ReviewedDiscoveryBaseline CompleteReview(
        ReviewedDiscoveryBaseline discoveredBaseline,
        IEnumerable<ReviewedDiscoverySource> currentSources,
        IEnumerable<string> previouslyTrackedRoots)
    {
        var completed = Clone(discoveredBaseline);
        var current = Index(currentSources);
        var tracked = previouslyTrackedRoots
            .Concat(completed.Sources.Select(source => source.NormalizedRootPath))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var upstream = Index(completed.Sources);
        foreach (var key in tracked)
        {
            upstream.TryGetValue(key, out var upstreamSource);
            current.TryGetValue(key, out var currentSource);
            var upstreamFingerprint = Fingerprint(upstreamSource);
            var currentFingerprint = Fingerprint(currentSource);
            if (upstreamFingerprint == currentFingerprint)
            {
                continue;
            }
            completed.UserOverrides.Add(new ReviewedDiscoveryOverride
            {
                NormalizedRootPath = key,
                UpstreamFingerprint = upstreamFingerprint,
                CurrentFingerprint = currentFingerprint
            });
        }
        return completed;
    }

    public static string Fingerprint(ReviewedDiscoverySource? source)
    {
        if (source == null)
        {
            return MissingFingerprint;
        }
        var payload = source.Mode == BackupSourceScopeMode.All
            ? "all"
            : "include|" + string.Join("|", source.IncludePatterns
                .Select(NormalizePattern)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(pattern => pattern, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static ReviewedDiscoveryBaseline Clone(ReviewedDiscoveryBaseline? source) => new()
    {
        Sources = new ObservableCollection<ReviewedDiscoverySource>(
            (source?.Sources ?? new ObservableCollection<ReviewedDiscoverySource>()).Select(CloneSource)),
        UserOverrides = new ObservableCollection<ReviewedDiscoveryOverride>(
            (source?.UserOverrides ?? new ObservableCollection<ReviewedDiscoveryOverride>()).Select(item =>
                new ReviewedDiscoveryOverride
                {
                    NormalizedRootPath = Normalize(item.NormalizedRootPath),
                    UpstreamFingerprint = item.UpstreamFingerprint,
                    CurrentFingerprint = item.CurrentFingerprint
                }))
    };

    public static ReviewedDiscoverySource CloneSource(ReviewedDiscoverySource source) => new()
    {
        NormalizedRootPath = Normalize(source.NormalizedRootPath),
        Mode = source.Mode,
        IncludePatterns = new ObservableCollection<string>(source.IncludePatterns ?? new ObservableCollection<string>()),
        ResourceIds = new ObservableCollection<string>(source.ResourceIds ?? new ObservableCollection<string>())
    };

    private static DiscoverySourceChange Change(
        string key,
        DiscoverySourceChangeKind kind,
        ReviewedDiscoverySource? previous,
        ReviewedDiscoverySource? current,
        ReviewedDiscoverySource? discovered,
        bool actionable,
        bool selected) => new()
    {
        NormalizedRootPath = key,
        Kind = kind,
        Previous = previous == null ? null : CloneSource(previous),
        Current = current == null ? null : CloneSource(current),
        Discovered = discovered == null ? null : CloneSource(discovered),
        IsActionable = actionable,
        IsSelected = actionable && selected
    };

    private static Dictionary<string, ReviewedDiscoverySource> Index(
        IEnumerable<ReviewedDiscoverySource>? sources) =>
        (sources ?? Array.Empty<ReviewedDiscoverySource>())
        .Where(source => !string.IsNullOrWhiteSpace(source.NormalizedRootPath))
        .GroupBy(source => Normalize(source.NormalizedRootPath), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => CloneSource(group.Last()), StringComparer.OrdinalIgnoreCase);

    private static bool IsExpansion(ReviewedDiscoverySource oldSource, ReviewedDiscoverySource newSource)
    {
        if (newSource.Mode == BackupSourceScopeMode.All)
        {
            return true;
        }
        if (oldSource.Mode == BackupSourceScopeMode.All)
        {
            return false;
        }
        return oldSource.IncludePatterns.All(pattern =>
            newSource.IncludePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => DiscoveryResourcePlanner.NormalizePath(path);
    private static string NormalizePattern(string pattern) =>
        (pattern ?? string.Empty).Trim().Replace('\\', '/');
}
