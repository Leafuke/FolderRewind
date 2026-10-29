using FolderRewind.History.Retention;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryChainRewriteStrategyTests
{
    [TestMethod]
    public void AutomaticCostFallbackCannotBypassStructuralOrDepthConstraints()
    {
        Assert.IsTrue(HistoryChainRewriteStrategy.PreferFull(true, 80, 100, 2, 5, false));
        Assert.IsFalse(HistoryChainRewriteStrategy.PreferFull(true, 80, 100, 2, 5, false, true));
        Assert.IsTrue(HistoryChainRewriteStrategy.PreferFull(true, 1, 100, 6, 5, false, true));
        Assert.IsTrue(HistoryChainRewriteStrategy.PreferFull(true, 1, 100, 2, 5, true, true));
        Assert.IsFalse(HistoryChainRewriteStrategy.PreferFull(true, 1, 100, 100, 0, false));
    }

    [TestMethod]
    public void MissingBaseAndEmptyTargetAlwaysBecomeFull()
    {
        Assert.IsTrue(HistoryChainRewriteStrategy.PreferFull(false, 1, 100, 1, 5, false));
        Assert.IsTrue(HistoryChainRewriteStrategy.PreferFull(true, 0, 0, 1, 5, false));
    }
}
