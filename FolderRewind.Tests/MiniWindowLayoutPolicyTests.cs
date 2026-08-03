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

    [TestMethod]
    public void ExpansionFlipsLeftNearRightEdge()
    {
        var result = MiniWindowLayoutPolicy.GetExpandedBounds(
            new MiniWindowPixelPoint(1840, 200),
            new MiniWindowPixelRect(0, 0, 1920, 1080),
            1d,
            MiniWindowLayoutDirection.Right);

        Assert.AreEqual(MiniWindowLayoutDirection.Left, result.Direction);
        Assert.AreEqual(1840, result.AnchorBounds.X);
        Assert.IsGreaterThanOrEqualTo(8, result.WindowBounds.X);
        Assert.IsLessThanOrEqualTo(1912, result.WindowBounds.Right);
    }

    [TestMethod]
    public void ExpansionFlipsRightNearLeftEdgeOnNegativeCoordinateDisplay()
    {
        var result = MiniWindowLayoutPolicy.GetExpandedBounds(
            new MiniWindowPixelPoint(-1910, 200),
            new MiniWindowPixelRect(-1920, 0, 1920, 1080),
            1d,
            MiniWindowLayoutDirection.Left);

        Assert.AreEqual(MiniWindowLayoutDirection.Right, result.Direction);
        Assert.AreEqual(-1910, result.AnchorBounds.X);
        Assert.IsGreaterThanOrEqualTo(-1912, result.WindowBounds.X);
        Assert.IsLessThanOrEqualTo(-8, result.WindowBounds.Right);
    }

    [TestMethod]
    public void ExpansionChoosesMoreSpaceAndClampsWhenNeitherSideFits()
    {
        var result = MiniWindowLayoutPolicy.GetExpandedBounds(
            new MiniWindowPixelPoint(220, 100),
            new MiniWindowPixelRect(0, 0, 500, 800),
            1d,
            MiniWindowLayoutDirection.Left);

        Assert.AreEqual(MiniWindowLayoutDirection.Right, result.Direction);
        Assert.AreEqual(216, result.WindowBounds.X);
        Assert.AreEqual(216, result.AnchorBounds.X);
        Assert.AreEqual(492, result.WindowBounds.Right);
    }

    [TestMethod]
    public void CollapsedWindowClampsWithinNegativeCoordinateWorkArea()
    {
        var result = MiniWindowLayoutPolicy.ClampCollapsedBounds(
            new MiniWindowPixelPoint(-2000, -30),
            new MiniWindowPixelRect(-1920, 0, 1920, 1080),
            1.5d);

        Assert.AreEqual(-1908, result.X);
        Assert.AreEqual(12, result.Y);
        Assert.AreEqual(72, result.Width);
        Assert.AreEqual(72, result.Height);
    }
}
