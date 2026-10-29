using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class ThemeSettingPolicyTests
{
    [TestMethod]
    [DataRow(null, 2)]
    [DataRow(-1, 2)]
    [DataRow(3, 2)]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    public void MissingAndInvalidSettingsFollowSystemWhileValidChoicesArePreserved(int? value, int expected)
        => Assert.AreEqual(expected, ThemeSettingPolicy.Normalize(value));
}
