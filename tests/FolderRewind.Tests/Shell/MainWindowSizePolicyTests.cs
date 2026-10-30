using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class MainWindowSizePolicyTests
{
    [TestMethod]
    [DataRow(1d, 1000, 600)]
    [DataRow(1.5d, 1500, 900)]
    [DataRow(2d, 2000, 1200)]
    public void PhysicalPixelPreferencesRespectTheDipMinimum(double scale, int width, int height)
    {
        var limits = MainWindowSizePolicy.GetLimits(scale, 3840, 2160);
        var actual = MainWindowSizePolicy.Normalize(1200, 600, scale, limits);
        Assert.AreEqual(Math.Max(1200, width), actual.Width);
        Assert.AreEqual(height, actual.Height);
        var valid = MainWindowSizePolicy.Normalize(2600, 1600, scale, limits);
        Assert.AreEqual(new WindowPixelSize(2600, 1600), valid);
    }

    [TestMethod]
    public void AWorkAreaSmallerThanTheDipMinimumStillContainsTheWholeWindow()
    {
        var limits = MainWindowSizePolicy.GetLimits(2, 1920, 1040);
        Assert.AreEqual(new WindowSizeLimits(1920, 1040, 1920, 1040), limits);
        Assert.AreEqual(new WindowPixelSize(1920, 1040), MainWindowSizePolicy.Normalize(3840, 2160, 2, limits));
    }

    [TestMethod]
    public void NonFiniteAndInvalidLegacyValuesBecomeUsableSizes()
    {
        var limits = MainWindowSizePolicy.GetLimits(1.5, 2560, 1440);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, -1 })
            Assert.AreEqual(new WindowPixelSize(1950, 1350), MainWindowSizePolicy.Normalize(value, value, 1.5, limits));
        Assert.AreEqual(new WindowSizeLimits(1000, 600, 3840, 2160), MainWindowSizePolicy.GetLimits(double.NaN, 0, 0));
    }
}
