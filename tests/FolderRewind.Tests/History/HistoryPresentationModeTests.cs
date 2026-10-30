using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPresentationModeTests
{
    [TestMethod]
    public void ReturningToNormalAlwaysDiscardsHiddenBranchFilter()
    {
        var active = BranchId.New();
        var selected = BranchId.New();
        BranchSummary[] branches = [Branch(active, true), Branch(selected, false)];
        Assert.AreEqual(selected, HistoryPresentationPolicy.ResolveBranch(HistoryPresentationMode.Advanced, branches, selected));
        Assert.AreEqual(active, HistoryPresentationPolicy.ResolveBranch(HistoryPresentationMode.Normal, branches, selected));
    }

    [TestMethod]
    public void RemovedBranchFallsBackToActiveBranch()
    {
        var active = BranchId.New();
        Assert.AreEqual(active, HistoryPresentationPolicy.ResolveBranch(HistoryPresentationMode.Advanced, [Branch(active, true)], BranchId.New()));
    }

    private static BranchSummary Branch(BranchId id, bool active)
        => new(id, "test", [], false, false, false, false, active, true, true, true, !active);
}
