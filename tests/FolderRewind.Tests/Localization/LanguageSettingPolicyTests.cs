using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class LanguageSettingPolicyTests
{
    [TestMethod]
    [DataRow(null, "system")]
    [DataRow("", "system")]
    [DataRow("   ", "system")]
    [DataRow("system", "system")]
    [DataRow(" SYSTEM ", "system")]
    [DataRow("en", "en-US")]
    [DataRow("en-US", "en-US")]
    [DataRow("EN-us", "en-US")]
    [DataRow("en_US", "en-US")]
    [DataRow(" zh ", "zh-CN")]
    [DataRow("zh-CN", "zh-CN")]
    [DataRow("ZH-cn", "zh-CN")]
    [DataRow("zh_CN", "zh-CN")]
    [DataRow("fr-FR", "system")]
    [DataRow("not_a_language", "system")]
    [DataRow("en-US,zh-CN", "system")]
    public void NormalizeReturnsCanonicalSupportedSetting(string? input, string expected)
    {
        Assert.AreEqual(expected, LanguageSettingPolicy.Normalize(input));
    }

    [TestMethod]
    [DataRow(null, "")]
    [DataRow("system", "")]
    [DataRow("unexpected", "")]
    [DataRow("en_US", "en-US")]
    [DataRow("zh_CN", "zh-CN")]
    public void ToOverrideReturnsSafePlatformValue(string? input, string expected)
    {
        Assert.AreEqual(expected, LanguageSettingPolicy.ToOverride(input));
    }

    [TestMethod]
    [DataRow("system", 0)]
    [DataRow("invalid", 0)]
    [DataRow("en_US", 1)]
    [DataRow("zh_CN", 2)]
    public void SelectionIndexUsesCanonicalPolicy(string? input, int expected)
    {
        Assert.AreEqual(expected, LanguageSettingPolicy.ToSelectionIndex(input));
    }

    [TestMethod]
    [DataRow(-1, "system")]
    [DataRow(0, "system")]
    [DataRow(1, "en-US")]
    [DataRow(2, "zh-CN")]
    [DataRow(3, "system")]
    public void SelectionIndexProducesCanonicalSetting(int input, string expected)
    {
        Assert.AreEqual(expected, LanguageSettingPolicy.FromSelectionIndex(input));
    }

    [TestMethod]
    [DataRow("zh", true)]
    [DataRow("zh_CN", true)]
    [DataRow("zh-CN", true)]
    [DataRow("en-US", false)]
    [DataRow("invalid", false)]
    public void ChineseDetectionUsesCanonicalPolicy(string? input, bool expected)
    {
        Assert.AreEqual(expected, LanguageSettingPolicy.IsChinese(input));
    }
}
