using FolderRewind.Services;
using System.Text.RegularExpressions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PathRuleMatcherTests
{
    [TestMethod]
    public void LiteralRulesMatchOnlyAtPathBoundaries()
    {
        string root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForBackup(["voxy"], root, root, enableRegexRules: false);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "voxy", "data.bin")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "VoxyFab", "data.bin")));
    }

    [TestMethod]
    public void LargeExactRuleSetFindsSelectedRegion()
    {
        string root = CreateRoot();
        var rules = Enumerable.Range(0, 36_900)
            .Select(index => $"dimensions/minecraft/overworld/region/r.{index}.0.mca")
            .ToArray();
        var matcher = PathRuleMatcher.CreateForBackup(rules, root, root, enableRegexRules: false);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(
            root,
            "dimensions",
            "minecraft",
            "overworld",
            "region",
            "r.36899.0.mca")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "region", "r.36900.0.mca")));
    }

    [TestMethod]
    public void WildcardsMatchFileNameAndBackupRelativePath()
    {
        string root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForBackup(
            ["region/c.*.*.mcc"],
            root,
            root,
            enableRegexRules: false);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "region", "c.1.-2.mcc")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "entities", "c.1.-2.mcc")));
    }

    [TestMethod]
    public void RootedRuleIsRemappedForSnapshotSource()
    {
        string originalRoot = CreateRoot();
        string snapshotRoot = CreateRoot();
        string protectedDirectory = Path.Combine(originalRoot, "playerdata");
        var matcher = PathRuleMatcher.CreateForBackup(
            [protectedDirectory],
            snapshotRoot,
            originalRoot,
            enableRegexRules: false);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(snapshotRoot, "playerdata", "player.dat")));
    }

    [TestMethod]
    public void RestoreWildcardUsesFileNameOnlySemantics()
    {
        string root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForRestore(["*.dat"], root);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "nested", "level.dat")));

        var pathMatcher = PathRuleMatcher.CreateForRestore(["nested/*.dat"], root);
        Assert.IsFalse(pathMatcher.IsMatch(Path.Combine(root, "nested", "level.dat")));
    }

    [TestMethod]
    public void InvalidEnabledRegexIsRejectedDuringCompilation()
    {
        Assert.ThrowsExactly<PathRuleValidationException>(() =>
            PathRuleMatcher.CreateForBackup(
                ["regex:(unclosed"],
                CreateRoot(),
                CreateRoot(),
                enableRegexRules: true));
    }

    [TestMethod]
    public void DisabledRegexRuleIsIgnoredDuringMatching()
    {
        var matcher = PathRuleMatcher.CreateForBackup(
            ["regex:(unclosed"],
            CreateRoot(),
            CreateRoot(),
            enableRegexRules: false);

        Assert.IsFalse(matcher.IsMatch(Path.Combine(CreateRoot(), "anything.txt")));
    }

    [TestMethod]
    public void InvalidDisabledRegexIsStillRejectedByConfigValidation()
    {
        Assert.ThrowsExactly<PathRuleValidationException>(() =>
            PathRuleMatcher.ValidateBackupRules(
                ["regex:(unclosed"],
                enableRegexRules: false));
    }

    [TestMethod]
    public void RestoreRulesDoNotGainRegexPrefixSemantics()
    {
        string root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForRestore(["regex:literal"], root);

        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "regex:literal")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "literal")));
    }

    [TestMethod]
    public void ExcessiveRuleCountIsRejected()
    {
        var rules = Enumerable.Range(0, PathRuleMatcher.MaxRuleCount + 1)
            .Select(index => $"rule-{index}");

        Assert.ThrowsExactly<PathRuleValidationException>(() =>
            PathRuleMatcher.ValidateBackupRules(rules, enableRegexRules: false));
    }

    [TestMethod]
    public void CatastrophicRegexTimesOut()
    {
        string root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForBackup(
            ["regex:^(a+)+$"],
            root,
            root,
            enableRegexRules: true);
        string adversarialName = new string('a', 20_000) + "!";

        Assert.ThrowsExactly<RegexMatchTimeoutException>(() =>
            matcher.IsMatch(Path.Combine(root, adversarialName)));
    }

    private static string CreateRoot()
        => Path.Combine(Path.GetTempPath(), "FolderRewindTests", Guid.NewGuid().ToString("N"));
}
