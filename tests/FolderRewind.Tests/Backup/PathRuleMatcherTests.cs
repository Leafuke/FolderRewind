using FolderRewind.Services;
using System.Text.RegularExpressions;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class PathRuleMatcherTests
{
    [TestMethod]
    [DataRow(PathFilterTarget.File, true, false, false)]
    [DataRow(PathFilterTarget.Directory, false, true, true)]
    [DataRow(PathFilterTarget.Any, true, true, true)]
    public void TypedRulesDistinguishAFileFromTheSameNamedDirectory(PathFilterTarget target, bool file, bool directory, bool descendant)
    {
        var root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForBackupRules([new("session.lock", target)], root, root, false);
        Assert.AreEqual(file, matcher.IsMatch(Path.Combine(root, "session.lock")));
        Assert.AreEqual(directory, matcher.IsMatch(Path.Combine(root, "session.lock"), isDirectory: true));
        Assert.AreEqual(descendant, matcher.IsMatch(Path.Combine(root, "nested", "session.lock", "region", "r.0.0.mca")));
        Assert.AreEqual(directory, matcher.ExcludesWholeSubtree(Path.Combine(root, "nested", "session.lock")));
    }

    [TestMethod]
    public void NewBackupAndRestoreRulesShareAnchoringAndGlobSemantics()
    {
        var root = CreateRoot();
        PathFilterRule[] rules = [new("session.lock", PathFilterTarget.File), new("region/*.mca", PathFilterTarget.File),
            new("**/cache/**", PathFilterTarget.Directory), new("./root.dat", PathFilterTarget.File)];
        var backup = PathRuleMatcher.CreateForBackupRules(rules, root, root, false);
        var restore = PathRuleMatcher.CreateForRestoreRules(rules, root);
        foreach (var (path, expected) in new[] { ("session.lock", true), ("deep/session.lock", true), ("region/r.0.0.mca", true),
            ("region/nested/r.0.0.mca", false), ("deep/region/r.0.0.mca", false), ("cache/file.bin", true),
            ("deep/cache/more/file.bin", true), ("root.dat", true), ("deep/root.dat", false) })
        {
            Assert.AreEqual(expected, backup.IsMatch(Path.Combine(root, path)), path);
            Assert.AreEqual(expected, restore.IsMatch(Path.Combine(root, path)), path);
        }
    }

    [TestMethod]
    [DataRow("K")]
    [DataRow("./K")]
    [DataRow("cache/**")]
    [DataRow("**/cache/**")]
    public void HistoricalTypedNormalizationPreservesRuntimeMatching(string pattern)
    {
        var root = CreateRoot();
        var rule = new PathFilterRule(pattern, PathFilterTarget.Directory);
        var historical = PathFilterPattern.Parse(rule, root).ToBoundaryRule();
        var live = PathRuleMatcher.CreateForBackupRules([rule], root, root, false);
        var restored = PathRuleMatcher.CreateForBackupRules([historical], root, root, false);
        foreach (var path in new[] { "k/file", "K/file", "nested/K/file", "cache/file", "nested/cache/file" })
            Assert.AreEqual(live.IsMatch(Path.Combine(root, path)), restored.IsMatch(Path.Combine(root, path)), path);
        if (pattern.Contains('K')) StringAssert.Contains(historical.Pattern, "K");
    }

    [TestMethod]
    public void NewAbsoluteFileRulesRemapOnlyInsideTheirOriginalSource()
    {
        var original = CreateRoot(); var snapshot = CreateRoot();
        var rule = new PathFilterRule(Path.Combine(original, "session.lock"), PathFilterTarget.File);
        var matcher = PathRuleMatcher.CreateForBackupRules([rule], snapshot, original, false);
        Assert.IsTrue(matcher.IsMatch(Path.Combine(snapshot, "session.lock")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(snapshot, "nested", "session.lock")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(snapshot, "session.lock", "child")));
        Assert.IsFalse(PathRuleMatcher.CreateForBackupRules([rule], snapshot, CreateRoot(), false).IsMatch(Path.Combine(snapshot, "session.lock")));
    }

    [TestMethod]
    public void RedundantDirectorySubtreeSuffixDoesNotLoseRootAnchoring()
    {
        var root = CreateRoot();
        var matcher = PathRuleMatcher.CreateForBackupRules([new("cache/**", PathFilterTarget.Directory)], root, root, false);
        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "cache", "file")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "deep", "cache", "file")));
        var both = PathRuleMatcher.CreateForBackupRules([new("cache/**", PathFilterTarget.Any)], root, root, false);
        Assert.IsFalse(both.IsMatch(Path.Combine(root, "cache")));
        Assert.IsTrue(both.IsMatch(Path.Combine(root, "cache", "file")));
    }

    [TestMethod]
    public void NewRegexRulesUseRelativePathsAndRespectTheirTargetInRestoreToo()
    {
        var root = CreateRoot();
        var rule = new PathFilterRule("regex:^cache$", PathFilterTarget.Directory);
        var enabled = PathRuleMatcher.CreateForRestoreRules([rule], root, true);
        Assert.IsTrue(enabled.IsMatch(Path.Combine(root, "cache", "a.bin")));
        Assert.IsFalse(enabled.IsMatch(Path.Combine(root, "nested", "cache", "a.bin")));
        Assert.IsFalse(PathRuleMatcher.CreateForRestoreRules([rule], root, false).IsMatch(Path.Combine(root, "cache", "a.bin")));
    }

    [TestMethod]
    public void LargeTypedExactRulesDoNotChangeTheSelectedFileFamily()
    {
        var root = CreateRoot();
        var rules = Enumerable.Range(0, 36_900).Select(index => new PathFilterRule($"region/r.{index}.0.mca", PathFilterTarget.File));
        var matcher = PathRuleMatcher.CreateForBackupRules(rules, root, root, false);
        Assert.IsTrue(matcher.IsMatch(Path.Combine(root, "region", "r.36899.0.mca")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "region", "r.36900.0.mca")));
        Assert.IsFalse(matcher.IsMatch(Path.Combine(root, "region", "r.36899.0.mca", "child.bin")));
    }

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
