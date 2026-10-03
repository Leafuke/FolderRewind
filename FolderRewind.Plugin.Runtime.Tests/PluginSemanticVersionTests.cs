using FolderRewind.Plugin.Runtime.Packaging;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginSemanticVersionTests
{
    [TestMethod]
    [DataRow("2.0.0-beta.2", "2.0.0-beta.10")]
    [DataRow("2.0.0-beta.10", "2.0.0-rc.1")]
    [DataRow("2.0.0-rc.1", "2.0.0")]
    public void SemVerPrereleasePrecedenceIsApplied(string lower, string higher)
    {
        Assert.IsLessThan(0, PluginSemanticVersion.ComparePrecedence(lower, higher));
        Assert.IsGreaterThan(0, PluginSemanticVersion.ComparePrecedence(higher, lower));
    }

    [TestMethod]
    public void BuildMetadataDoesNotCreateAnUpdate()
    {
        Assert.AreEqual(
            0,
            PluginSemanticVersion.ComparePrecedence("2.0.0+build.1", "2.0.0+build.2"));
    }

    [TestMethod]
    [DataRow("v2.0.0")]
    [DataRow("2.0")]
    [DataRow("02.0.0")]
    [DataRow("2.00.0")]
    [DataRow("2.0.00")]
    [DataRow("2.0.0-beta.01")]
    [DataRow("2.0.0-")]
    [DataRow(" 2.0.0")]
    public void StrictParserRejectsNonSemVerValues(string value)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => PluginSemanticVersion.RequireStrict(value));
        Assert.IsFalse(PluginSemanticVersion.TryComparePrecedence(value, "2.0.0", out _));
    }
}
