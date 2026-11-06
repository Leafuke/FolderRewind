using FolderRewind.Services.Development;

namespace FolderRewind.Tests;

[TestClass]
public sealed class UiAcceptanceCriteriaTests
{
    [TestMethod]
    public void PostRunNeedsThreeEmptyTypedRoundsAndDrainedProvider()
    {
        var rounds = Enumerable.Range(1, 3).Select(i => new UiManagerGcRound(i, 0, 0, 0)).ToArray();
        var good = new UiPostRunEvidence(10, 0, rounds);
        Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluatePostRun(good).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluatePostRun(good with { TrackedReferences = 0 }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluatePostRun(good with { ProviderGenerations = 1 }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluatePostRun(good with { Rounds = rounds[..2] }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluatePostRun(good with
        { Rounds = [rounds[0], new(2, 1, 1, 0), rounds[2]] }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluatePostRun(null).Status);
    }

    [TestMethod]
    public void SpatialAllowsOnlyTheBoundedLatestGenerationAfterBack()
    {
        foreach (int count in new[] { 2, 5, 20 })
            Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluateSpatial(Spatial(count)).Status, $"{count} pages");
    }

    [TestMethod]
    public void SameTotalCannotHideAnOlderPageOrPreviewSession()
    {
        var good = Spatial(5);
        var back = good.BackRounds!.Select(round => round with
        {
            Targets = round.Targets!.Select(target => target with
            {
                Alive = target.Generation == 4 && target.Kind == "SpatialWorkspacePage" ? true :
                    target.Generation == 5 && target.Kind == "SpatialWorkspacePage" ? false : target.Alive
            }).ToArray()
        }).ToArray();
        Assert.AreEqual(5, back[0].Targets!.Count(target => target.Alive));
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { BackRounds = back }).Status);

        back = good.BackRounds!.Select(round => round with
        {
            Targets = round.Targets!.Select(target => target with
            {
                Alive = target.Generation == 5 && target.Kind == "PluginV3SpatialPreview" && target.Id.EndsWith("-0")
                    ? true : target.Generation == 5 && target.Kind == "SpatialWorkspacePage" ? false : target.Alive
            }).ToArray()
        }).ToArray();
        Assert.AreEqual(5, back[0].Targets!.Count(target => target.Alive));
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { BackRounds = back }).Status);
    }

    [TestMethod]
    public void NavigationAndResourceEvidenceAreRequired()
    {
        var good = Spatial(2);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(null).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { AtTasks = null }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with
        { AfterNavigation = good.AfterNavigation! with { ForwardPages = ["retained"], ForwardParameterTypes = [null] } }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with
        { Releases = [good.Releases![0] with { IndexDisposed = false }, good.Releases[1]] }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with
        { Releases = [good.Releases![0], good.Releases[1] with { LeftCanvas = good.Releases[1].LeftCanvas! with { Pending = 1 } }] }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { NavigationRounds = null }).Status);
    }

    [TestMethod]
    public void NavigationMustTrackTheSameTargetsAndCollectEveryGeneration()
    {
        var good = Spatial(2);
        var surviving = good.NavigationRounds!.Select(round => round with
        { Targets = round.Targets!.Select(target => target.Generation == 2 && target.Kind == "SpatialWorkspaceIndex"
            ? target with { Alive = true } : target).ToArray() }).ToArray();
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { NavigationRounds = surviving }).Status);
        var changed = good.NavigationRounds!.Select(round => round with
        { Targets = round.Targets!.Select((target, i) => i == 0 ? target with { Id = "different-target" } : target).ToArray() }).ToArray();
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatial(good with { NavigationRounds = changed }).Status);
    }

    [TestMethod]
    public void SpatialFunctionalityUsesObservedValuesAndRejectsMissingStages()
    {
        var scan = new UiFullScanObservation(10_000, 10_000, 100, "");
        var filter = new UiFilterObservation("Cell 00234", "Modified", "blocks", "1/1", "1/1",
            ["Blocks 1", "Entities 1"], ["change-2342"], "", Overlays(aggregate: true));
        var stop = new UiStopObservation(500, 500, 0, 1, "partial", "partial", "");
        var close = new UiCloseObservation(false, true, 0, 0, 2, 1, 3, 2, true, true, 0);
        var good = new UiSpatialFunctionalEvidence(false, scan, filter, stop, close);
        Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with { FullScan = null }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with { Filter = filter with { Ids = ["stale"] } }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with { Filter = filter with { Overlays = null } }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with { Stop = stop with { RowsAfterDelay = 501 } }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with { Close = close with { ChangesAfterClose = 1 } }).Status);
        Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluateSpatialFunctionality(good with
        { LifetimeOnly = true, FullScan = null, Filter = null }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialFunctionality(null).Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FinalOverlayAcceptsActualDetailOrContainingAggregateOnBothCanvases(bool aggregate)
    {
        var good = Overlays(aggregate);
        Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluateSpatialOverlays(good).Status);
        if (aggregate)
        {
            Assert.IsEmpty(good.Left!.Regions![0].Ids!);
            Assert.AreNotEqual(good.Rows![0].Locations![0].Bounds, good.Left.Regions[0].Bounds,
                "A valid aggregate covers a larger cell than the actual row location.");
        }
    }

    [TestMethod]
    public void FinalOverlayRejectsMissingUndrainedOrUnrenderedEvidence()
    {
        var good = Overlays(aggregate: true);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(null).Status);
        foreach (var incomplete in new[]
        {
            good with { Active = false }, good with { Drained = false }, good with { World = null },
            good with { Rows = null }, good with { Rows = [] }, good with { Rows = [good.Rows![0], good.Rows[0]] },
            good with { Left = null }, good with { Right = null },
            good with { Left = good.Left! with { Regions = null } },
            good with { Left = good.Left! with { Regions = [] } },
            good with { Right = good.Right! with { RenderedMarkers = 0 } },
            good with { Right = good.Right! with { Regions = [good.Right.Regions![0], good.Right.Regions[0]] } },
            good with { Rows = [good.Rows![0] with { Id = "change-2340" }] },
            good with { Rows = [good.Rows![0] with { Components = ["entities"] }] },
            good with { Rows = [good.Rows![0] with { ComponentLabels = null }] },
            good with { Rows = [good.Rows![0] with { Locations = [] }] }
        }) Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(incomplete).Status);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FinalOverlayRejectsStaleIdsStatusCategoriesCountsAndBounds(bool aggregate)
    {
        var good = Overlays(aggregate);
        var marker = good.Left!.Regions![0];
        foreach (var stale in new[]
        {
            marker with { Ids = ["change-2340"] }, marker with { Ids = null },
            marker with { Status = "Deleted" }, marker with { States = ["Deleted"] },
            marker with { States = [] }, marker with { Components = ["Entities"] },
            marker with { Components = null }, marker with { Count = 2 },
            marker with { Bounds = new(0, 0, 16, 16) }, marker with { Bounds = new(5473, 32, 5488, 48) },
            marker with { Bounds = new(double.NaN, 0, 6000, 64) }, marker with { Bounds = null }
        })
        {
            // Both sides agree on the wrong result; matching canvases alone cannot prove freshness.
            var canvas = new UiCanvasOverlaySnapshot(1, [stale]);
            Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(good with { Left = canvas, Right = canvas }).Status);
        }
        if (!aggregate)
        {
            var missingIds = new UiCanvasOverlaySnapshot(1, [marker with { Ids = [] }]);
            Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(good with { Left = missingIds, Right = missingIds }).Status);
        }
    }

    [TestMethod]
    public void FinalOverlayRejectsDifferentCanvasesEvenWhenEachAggregateContainsTheRow()
    {
        var good = Overlays(aggregate: true);
        var other = new UiCanvasOverlaySnapshot(1, [good.Right!.Regions![0] with { Bounds = new(4096, 0, 8192, 4096) }]);
        Assert.AreEqual("PASS", UiAcceptanceCriteria.EvaluateSpatialOverlays(good with { Left = other, Right = other }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(good with { Right = other }).Status);
        Assert.AreEqual("FAIL", UiAcceptanceCriteria.EvaluateSpatialOverlays(good with { Right = Overlays(aggregate: false).Right }).Status);
    }

    private static UiOverlayObservation Overlays(bool aggregate)
    {
        var bounds = new UiOverlayBounds(5472, 32, 5488, 48);
        var row = new UiOverlaySourceRow("change-2342", "Modified", ["blocks"], ["Blocks"], [new("world", "layer", bounds)]);
        var marker = new UiOverlayMarker(aggregate ? new(4096, 0, 6144, 2048) : bounds,
            "Modified", 1, aggregate, ["Modified"], aggregate ? [] : [row.Id!], ["Blocks"]);
        var canvas = new UiCanvasOverlaySnapshot(1, [marker]);
        return new(true, true, "world", "layer", [row], canvas, canvas);
    }

    private static UiSpatialLifetimeEvidence Spatial(int count)
    {
        var canvas = new UiCanvasRelease(false, false, false, false, 0, 0, 0, 0, 0, 0, false);
        var releases = Enumerable.Range(1, count).Select(i => new UiSpatialRelease(i, true, canvas, canvas, true, 0, 0, true, false, "Disabled")).ToArray();
        var targets = Enumerable.Range(1, count).SelectMany(generation => new[]
        {
            ("SpatialWorkspacePage", 1), ("SpatialWorkspaceViewModel", 1), ("SpatialWorkspaceIndex", 1),
            ("SpatialPreviewCanvas", 2), ("PluginV3SpatialPreview", 2)
        }.SelectMany(kind => Enumerable.Range(0, kind.Item2).Select(i => new UiWeakTarget(generation, kind.Item1,
            $"{generation}-{kind.Item1}-{i}", generation == count && kind.Item1 != "PluginV3SpatialPreview")))).ToArray();
        UiWeakRound[] Rounds(bool alive) => Enumerable.Range(1, 3).Select(i => new UiWeakRound(i,
            targets.Select(target => target with { Alive = alive && target.Alive }).ToArray())).ToArray();
        return new(count, releases,
            new("FolderRewind.Views.HomePage", 1, ["FolderRewind.Views.SpatialWorkspacePage"], [null]),
            new("FolderRewind.Views.BackupTasksPage", 1, [], []), new("FolderRewind.Views.HomePage", 1, [], []),
            Rounds(true), Rounds(false));
    }
}
