using FolderRewind.History.Domain;

namespace FolderRewind.Tests;

[TestClass]
public sealed class EffectiveSourceBoundaryTests
{
    [TestMethod]
    public void CanonicalBoundaryFingerprintIgnoresRuleOrder()
    {
        var first = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["region/**", "playerdata/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            ["session.lock", "cache/**"],
            false);
        var second = new EffectiveSourceBoundarySnapshot(
            EffectiveBoundaryScopeMode.Include,
            ["playerdata/**", "region/**"],
            EffectiveBoundaryFilterMode.Blacklist,
            ["cache/**", "session.lock"],
            false);

        Assert.AreEqual(first.Fingerprint, second.Fingerprint);
        Assert.AreEqual(CaptureScope.FullSource, CaptureScopePolicy.Determine(first, second));
    }
}
