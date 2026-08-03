using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MiniWindowLayoutPolicyTests
{
    [TestMethod]
    [DataRow(1d, 48, 276)]
    [DataRow(1.25d, 60, 345)]
    [DataRow(1.5d, 72, 414)]
    [DataRow(1.75d, 84, 483)]
    [DataRow(2d, 96, 552)]
    public void SizesScaleFromSingleDipMetrics(double scale, int collapsed, int expanded)
    {
        Assert.AreEqual(collapsed, MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.CardSizeDip, scale));
        Assert.AreEqual(expanded, MiniWindowLayoutPolicy.DipToPixels(MiniWindowMetrics.ExpandedWidthDip, scale));
    }

    [TestMethod]
    public void ExpansionKeepsAnchorWhenPreferredSideFits()
    {
        var workArea = new MiniWindowPixelRect(0, 0, 1920, 1080);
        var anchor = new MiniWindowPixelPoint(600, 300);

        var right = MiniWindowLayoutPolicy.GetExpandedBounds(
            anchor, workArea, 1d, MiniWindowLayoutDirection.Right);
        var left = MiniWindowLayoutPolicy.GetExpandedBounds(
            anchor, workArea, 1d, MiniWindowLayoutDirection.Left);

        Assert.AreEqual(MiniWindowLayoutDirection.Right, right.Direction);
        Assert.AreEqual(anchor, new MiniWindowPixelPoint(right.AnchorBounds.X, right.AnchorBounds.Y));
        Assert.AreEqual(MiniWindowLayoutDirection.Left, left.Direction);
        Assert.AreEqual(anchor, new MiniWindowPixelPoint(left.AnchorBounds.X, left.AnchorBounds.Y));
    }
}
