#if PERFORMANCE_POST_RUN_ACCEPTANCE || SPATIAL_WORKSPACE_UI_ACCEPTANCE || UI_ACCEPTANCE_CRITERIA_TEST
using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderRewind.Services.Development;

// Pure BCL evidence checks shared by the opt-in fixtures and their regression tests.
internal sealed record UiCriterionResult(string Status, string[] Failures);
internal sealed record UiManagerGcRound(int Round, int Total, int Managers, int Canvases);
internal sealed record UiPostRunEvidence(int TrackedReferences, int ProviderGenerations, UiManagerGcRound[]? Rounds);
internal sealed record UiWeakTarget(int Generation, string Kind, string Id, bool Alive);
internal sealed record UiWeakRound(int Round, UiWeakTarget[]? Targets);
internal sealed record UiCanvasRelease(bool HasSession, bool HasLayer, bool Active, bool NavigationEnabled,
    int Bitmaps, int Images, int Pending, int Running, int TileChildren, int FallbackChildren, bool TimerEnabled);
internal sealed record UiSpatialRelease(int Generation, bool IndexDisposed, UiCanvasRelease? LeftCanvas,
    UiCanvasRelease? RightCanvas, bool FixtureTreesDeleted, int ProviderGenerations, int ActiveComparisons,
    bool ScanCompleted, bool Active, string? NavigationCacheMode);
internal sealed record UiFrameEvidence(string? CurrentPage, int? BackCount, string?[]? ForwardPages, string?[]? ForwardParameterTypes);
internal sealed record UiSpatialLifetimeEvidence(int PageCount, UiSpatialRelease[]? Releases,
    UiFrameEvidence? AfterBack, UiFrameEvidence? AtTasks, UiFrameEvidence? AfterNavigation,
    UiWeakRound[]? BackRounds, UiWeakRound[]? NavigationRounds);
internal sealed record UiFullScanObservation(int ExpectedRows, int Rows, int VisibleRows, string? Error);
internal sealed record UiOverlayBounds(double MinX, double MinY, double MaxX, double MaxY);
internal sealed record UiOverlayLocation(string? World, string? Layer, UiOverlayBounds? Bounds);
internal sealed record UiOverlaySourceRow(string? Id, string? Status, string[]? Components,
    string[]? ComponentLabels, UiOverlayLocation[]? Locations);
internal sealed record UiOverlayMarker(UiOverlayBounds? Bounds, string? Status, int Count, bool Aggregate,
    string[]? States, string[]? Ids, string[]? Components);
internal sealed record UiCanvasOverlaySnapshot(int RenderedMarkers, UiOverlayMarker[]? Regions);
internal sealed record UiOverlayObservation(bool Active, bool Drained, string? World, string? Layer,
    UiOverlaySourceRow[]? Rows, UiCanvasOverlaySnapshot? Left, UiCanvasOverlaySnapshot? Right);
internal sealed record UiFilterObservation(string? Search, string? Filter, string? Category, string? Counts,
    string? ExpectedCounts, string[]? Breakdown, string[]? Ids, string? Error, UiOverlayObservation? Overlays = null);
internal sealed record UiStopObservation(int Rows, int RowsAfterDelay, int ActiveComparisons, long CanceledCalls,
    string? Status, string? ExpectedStatus, string? Error);
internal sealed record UiCloseObservation(bool Active, bool ScanCompleted, int ProviderGenerations, int ActiveComparisons,
    long CanceledCalls, long PreviousCanceledCalls, long CanceledRenders, long PreviousCanceledRenders,
    bool FixtureTreesDeleted, bool RowsStable, int ChangesAfterClose);
internal sealed record UiSpatialFunctionalEvidence(bool LifetimeOnly, UiFullScanObservation? FullScan,
    UiFilterObservation? Filter, UiStopObservation? Stop, UiCloseObservation? Close);

internal static class UiAcceptanceCriteria
{
    private const string HomePage = "FolderRewind.Views.HomePage";
    private const string TasksPage = "FolderRewind.Views.BackupTasksPage";
    private static readonly Dictionary<string, int> ExpectedKinds = new(StringComparer.Ordinal)
    {
        ["SpatialWorkspacePage"] = 1,
        ["SpatialWorkspaceViewModel"] = 1,
        ["SpatialWorkspaceIndex"] = 1,
        ["SpatialPreviewCanvas"] = 2,
        ["PluginV3SpatialPreview"] = 2
    };

    internal static UiCriterionResult EvaluatePostRun(UiPostRunEvidence? evidence)
    {
        var errors = new List<string>();
        if (evidence is null) return Result(["Post-run observation is missing."]);
        if (evidence.TrackedReferences <= 0) errors.Add("No weak references were tracked.");
        if (evidence.ProviderGenerations != 0) errors.Add("Provider generations did not drain.");
        if (evidence.Rounds is not { Length: 3 } rounds)
            errors.Add("Exactly three post-run GC rounds are required.");
        else
        {
            for (int i = 0; i < rounds.Length; i++)
            {
                var round = rounds[i];
                if (round is null || round.Round != i + 1 || round.Managers < 0 || round.Canvases < 0 ||
                    round.Total != round.Managers + round.Canvases)
                { errors.Add($"Post-run round {i + 1} is incomplete or inconsistent."); continue; }
                if (round.Managers != 0 || round.Canvases != 0)
                    errors.Add($"Post-run round {i + 1} retains Manager or Canvas targets.");
            }
        }
        return Result(errors);
    }

    internal static UiCriterionResult EvaluateSpatial(UiSpatialLifetimeEvidence? evidence)
    {
        var errors = new List<string>();
        if (evidence is null) return Result(["Spatial lifecycle observation is missing."]);
        int count = evidence.PageCount;
        if (count is not (2 or 5 or 20)) errors.Add("The page count must be 2, 5, or 20.");
        if (evidence.Releases is null || evidence.Releases.Length != count ||
            evidence.Releases.Any(value => value is null) ||
            !evidence.Releases.Select(value => value.Generation).OrderBy(value => value).SequenceEqual(Enumerable.Range(1, Math.Max(0, count))))
            errors.Add("A release observation for each page generation is required.");
        else foreach (var release in evidence.Releases)
        {
            if (!release.IndexDisposed || !CanvasCleared(release.LeftCanvas) || !CanvasCleared(release.RightCanvas) ||
                !release.FixtureTreesDeleted || release.ProviderGenerations != 0 || release.ActiveComparisons != 0 ||
                !release.ScanCompleted || release.Active || string.IsNullOrWhiteSpace(release.NavigationCacheMode))
                errors.Add($"Generation {release.Generation} did not provide complete resource-release evidence.");
        }
        if (!FrameIs(evidence.AfterBack, HomePage)) errors.Add("The Frame after Back was not observed on Home.");
        if (!FrameIs(evidence.AtTasks, TasksPage)) errors.Add("The real Tasks navigation was not observed.");
        if (!FrameIs(evidence.AfterNavigation, HomePage) || evidence.AfterNavigation!.ForwardPages!.Length != 0)
            errors.Add("The real Home navigation did not leave an empty forward stack.");
        CheckRounds(evidence.BackRounds, count, afterBack: true, errors);
        CheckRounds(evidence.NavigationRounds, count, afterBack: false, errors);
        var back = evidence.BackRounds?.FirstOrDefault()?.Targets;
        var navigation = evidence.NavigationRounds?.FirstOrDefault()?.Targets;
        if (back is not null && navigation is not null &&
            !back.Where(target => target is not null).Select(target => (target.Id, target.Generation, target.Kind))
                .OrderBy(target => target.Id, StringComparer.Ordinal).SequenceEqual(
                    navigation.Where(target => target is not null).Select(target => (target.Id, target.Generation, target.Kind))
                        .OrderBy(target => target.Id, StringComparer.Ordinal)))
            errors.Add("Back and Tasks-to-Home do not track the same targets.");
        return Result(errors);
    }

    internal static UiCriterionResult EvaluateSpatialFunctionality(UiSpatialFunctionalEvidence? evidence)
    {
        var errors = new List<string>();
        if (evidence is null) return Result(["Spatial functional observation is missing."]);
        if (!evidence.LifetimeOnly)
        {
            var scan = evidence.FullScan;
            if (scan is null || scan.ExpectedRows is not (10_000 or 50_000 or 100_000) ||
                scan.Rows != scan.ExpectedRows || scan.VisibleRows != 100 || scan.Error != "")
                errors.Add("Full scan did not produce the requested rows and page.");
            var filter = evidence.Filter;
            if (filter is null || filter.Search != "Cell 00234" || filter.Filter != "Modified" || filter.Category != "blocks" ||
                string.IsNullOrEmpty(filter.ExpectedCounts) || filter.Counts != filter.ExpectedCounts || filter.Error != "" ||
                filter.Ids is null || !filter.Ids.SequenceEqual(["change-2342"]) || filter.Breakdown is null ||
                !filter.Breakdown.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(["Blocks 1", "Entities 1"]))
                errors.Add("The final filter observation is missing or incorrect.");
            errors.AddRange(EvaluateSpatialOverlays(filter?.Overlays).Failures);
        }
        var stop = evidence.Stop;
        if (stop is null || stop.Rows != 500 || stop.RowsAfterDelay != 500 || stop.ActiveComparisons != 0 ||
            stop.CanceledCalls < 1 || string.IsNullOrEmpty(stop.ExpectedStatus) || stop.Status != stop.ExpectedStatus || stop.Error != "")
            errors.Add("Stop did not drain into stable partial results.");
        var close = evidence.Close;
        if (close is null || close.Active || !close.ScanCompleted || close.ProviderGenerations != 0 ||
            close.ActiveComparisons != 0 || close.CanceledCalls <= close.PreviousCanceledCalls ||
            close.CanceledRenders <= close.PreviousCanceledRenders || !close.FixtureTreesDeleted ||
            !close.RowsStable || close.ChangesAfterClose != 0)
            errors.Add("Back did not drain and preserve a stable closed workspace.");
        return Result(errors);
    }

    internal static UiCriterionResult EvaluateSpatialOverlays(UiOverlayObservation? evidence)
    {
        var errors = new List<string>();
        if (evidence is null) return Result(["Final canvas overlay observations are missing."]);
        if (!evidence.Active || !evidence.Drained || evidence.World != "world" || evidence.Layer != "layer")
            errors.Add("The final overlays were not observed on the active, drained fixture view.");
        if (evidence.Rows is not { Length: 1 } rows || rows[0] is not { } row ||
            row.Id != "change-2342" || row.Status != "Modified" ||
            !SameValues(row.Components, ["blocks"]) || !SameValues(row.ComponentLabels, ["Blocks"]) ||
            row.Locations is not { Length: 1 } locations || locations[0] is not { } location ||
            location.World != evidence.World || location.Layer != evidence.Layer || !ValidBounds(location.Bounds))
            return Result(errors.Append("The final row identity, category or spatial location is missing or incorrect."));

        var left = CheckCanvas(evidence.Left, "Left");
        var right = CheckCanvas(evidence.Right, "Right");
        if (left is not null && right is not null &&
            (left.Bounds != right.Bounds || left.Status != right.Status || left.Count != right.Count ||
             left.Aggregate != right.Aggregate || !SameValues(left.States, right.States) ||
             !SameValues(left.Ids, right.Ids) || !SameValues(left.Components, right.Components)))
            errors.Add("The two canvases display different final overlays.");
        return Result(errors);

        UiOverlayMarker? CheckCanvas(UiCanvasOverlaySnapshot? canvas, string side)
        {
            if (canvas is null || canvas.RenderedMarkers != 1 || canvas.Regions is not { Length: 1 } regions || regions[0] is not { } marker)
            {
                errors.Add($"{side} canvas must contain and render exactly one final overlay marker.");
                return null;
            }
            if (marker.Count != 1 || marker.Status != row.Status || !SameValues(marker.States, [row.Status]) ||
                !SameValues(marker.Components, row.ComponentLabels))
                errors.Add($"{side} overlay count, status or categories do not match the final row.");
            if (!ValidBounds(marker.Bounds) || (marker.Aggregate
                ? !Contains(marker.Bounds!, location.Bounds!) : marker.Bounds != location.Bounds))
                errors.Add($"{side} overlay bounds do not match or contain the final row location.");
            // Aggregate markers legitimately omit member IDs; details must retain the actual row ID.
            if (marker.Ids is null || (!marker.Aggregate || marker.Ids.Length != 0) && !SameValues(marker.Ids, [row.Id]))
                errors.Add($"{side} overlay identities do not match the final row.");
            return marker;
        }
    }

    private static bool SameValues(string[]? left, string[]? right)
        => left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool ValidBounds(UiOverlayBounds? bounds) => bounds is not null &&
        double.IsFinite(bounds.MinX) && double.IsFinite(bounds.MinY) && double.IsFinite(bounds.MaxX) && double.IsFinite(bounds.MaxY) &&
        bounds.MinX < bounds.MaxX && bounds.MinY < bounds.MaxY;

    private static bool Contains(UiOverlayBounds outer, UiOverlayBounds inner)
        => outer.MinX <= inner.MinX && outer.MinY <= inner.MinY && outer.MaxX >= inner.MaxX && outer.MaxY >= inner.MaxY;

    private static bool CanvasCleared(UiCanvasRelease? canvas) => canvas is not null &&
        !canvas.HasSession && !canvas.HasLayer && !canvas.Active && !canvas.NavigationEnabled &&
        canvas.Bitmaps == 0 && canvas.Images == 0 && canvas.Pending == 0 && canvas.Running == 0 &&
        canvas.TileChildren == 0 && canvas.FallbackChildren == 0 && !canvas.TimerEnabled;

    private static bool FrameIs(UiFrameEvidence? frame, string expected) => frame is not null &&
        frame.CurrentPage == expected && frame.BackCount is >= 0 && frame.ForwardPages is not null &&
        frame.ForwardParameterTypes is not null && frame.ForwardPages.Length == frame.ForwardParameterTypes.Length &&
        frame.ForwardPages.All(page => !string.IsNullOrWhiteSpace(page));

    private static void CheckRounds(UiWeakRound[]? rounds, int pageCount, bool afterBack, List<string> errors)
    {
        string phase = afterBack ? "Back" : "Tasks-to-Home";
        if (rounds is not { Length: 3 }) { errors.Add($"{phase} requires exactly three GC rounds."); return; }
        Dictionary<string, UiWeakTarget>? previous = null;
        for (int i = 0; i < rounds.Length; i++)
        {
            var round = rounds[i];
            if (round is null || round.Round != i + 1 || round.Targets is null)
            { errors.Add($"{phase} round {i + 1} is missing."); continue; }
            var targets = round.Targets;
            if (targets.Any(target => target is null || string.IsNullOrWhiteSpace(target.Id)) ||
                targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Length)
            { errors.Add($"{phase} round {i + 1} has missing or duplicate target identities."); continue; }
            var current = targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
            if (previous is not null && (current.Count != previous.Count || previous.Any(pair =>
                !current.TryGetValue(pair.Key, out var next) || next.Generation != pair.Value.Generation ||
                next.Kind != pair.Value.Kind || (!pair.Value.Alive && next.Alive))))
                errors.Add($"{phase} round {i + 1} changed identities or gained survivors.");
            previous = current;
            if (pageCount < 2 || targets.Length != pageCount * 7 || targets.Any(target =>
                    target.Generation < 1 || target.Generation > pageCount || target.Kind is null || !ExpectedKinds.ContainsKey(target.Kind)))
            { errors.Add($"{phase} round {i + 1} does not include every expected target."); continue; }
            for (int generation = 1; generation <= pageCount; generation++)
            foreach (var kind in ExpectedKinds)
            {
                var group = targets.Where(target => target.Generation == generation && target.Kind == kind.Key).ToArray();
                if (group.Length != kind.Value)
                    errors.Add($"{phase} generation {generation} has an incorrect {kind.Key} target count.");
                int permitted = afterBack && generation == pageCount && kind.Key != "PluginV3SpatialPreview" ? kind.Value : 0;
                if (group.Count(target => target.Alive) > permitted)
                    errors.Add($"{phase} generation {generation} retains unexpected {kind.Key} targets.");
            }
        }
    }

    private static UiCriterionResult Result(IEnumerable<string> errors)
    {
        var failures = errors.ToArray();
        return new(failures.Length == 0 ? "PASS" : "FAIL", failures);
    }
}
#endif
