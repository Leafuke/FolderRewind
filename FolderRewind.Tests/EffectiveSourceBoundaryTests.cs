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
        Assert.AreEqual(
            "4a082db8a73e1ba30878364fe9f6bc3a874cb2155b01620a036bbd2f38a88eeb",
            first.Fingerprint);
        Assert.AreEqual(CaptureScope.FullSource, CaptureScopePolicy.Determine(first, second));
    }
}
