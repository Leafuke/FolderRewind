using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.Merge;
using FolderRewind.History.Storage;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SemanticMergeCoverageTests
{
    [TestMethod]
    public void TypedLockFileDoesNotOverlapTerrainAndIgnoredCacheDoesNotMaskAnotherExclusion()
    {
        var lockedFile = new PathFilterRule("session.lock", PathFilterTarget.File);
        var cache = new PathFilterRule("voxy", PathFilterTarget.Directory);
        EffectiveSourceBoundarySnapshot Boundary(params PathFilterRule[] rules)
            => EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, rules, false);
        Assert.AreEqual(MergeCoverageState.Complete, new SemanticMergeCoverage(Boundary(lockedFile), Root).Check(Region(), default).State);
        var proof = new SemanticMergeCoverage(Boundary(lockedFile, cache), Root);
        Assert.AreEqual(MergeCoverageState.Filtered, proof.Check(Region(), default).State);
        Assert.AreEqual(MergeCoverageState.Complete, proof.Check(Region() with { IgnoredBlacklistRules = [cache] }, default).State);
        Assert.AreEqual(MergeCoverageState.Filtered, proof.Check(Region(), default).State, "The ignored-rule set belongs in the cache key.");
        var actualExclusion = new PathFilterRule("region/*.mca", PathFilterTarget.File);
        proof = new(Boundary(cache, lockedFile, actualExclusion), Root);
        var blocked = proof.Check(Region() with { IgnoredBlacklistRules = [cache] }, default);
        Assert.AreEqual(MergeCoverageState.Filtered, blocked.State);
        Assert.AreEqual(actualExclusion.Pattern, blocked.Evidence.Single().Rule);
    }

    [TestMethod]
    public void LegacyLockExceptionRechecksOtherRulesAndNeverChangesWhitelistCoverage()
    {
        var legacy = PathFilterRule.Legacy("session.lock");
        var proof = new SemanticMergeCoverage(Blacklist("session.lock"), Root);
        Assert.AreEqual(MergeCoverageState.Complete, proof.Check(Region() with { IgnoredBlacklistRules = [legacy] }, default).State);
        proof = new(Blacklist("session.lock", "**/*.mcc"), Root);
        Assert.AreEqual("**/*.mcc", proof.Check(Region() with { IgnoredBlacklistRules = [legacy] }, default).Evidence.Single().Rule);
        var white = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Whitelist, ["session.lock"], false);
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(white, Root).Check(Region() with { IgnoredBlacklistRules = [legacy] }, default).State);
        Assert.ThrowsExactly<ArgumentException>(() => proof.Check(Region() with { IgnoredBlacklistRules = [new("session.lock", PathFilterTarget.File)] }, default));
    }

    [TestMethod]
    [DataRow("region")]
    [DataRow("entities")]
    [DataRow("poi")]
    [DataRow("*.mca")]
    [DataRow("*.mcc")]
    public void LegacyLockExceptionCannotHideRealMinecraftDataExclusions(string exclusion)
    {
        var proof = new SemanticMergeCoverage(Blacklist("session.lock", exclusion), Root);
        var query = new MergeCoverageQuery("minecraft-data", "", ["region/*.mca", "region/*.mcc", "entities/*.mca", "poi/*.mca"])
        { IgnoredBlacklistRules = [PathFilterRule.Legacy("session.lock")] };
        var blocked = proof.Check(query, default);
        Assert.AreEqual(MergeCoverageState.Filtered, blocked.State);
        Assert.AreEqual(exclusion, blocked.Evidence.Single().Rule);
    }

    [TestMethod]
    public void VersionOneFingerprintAndTypedBoundaryRoundTripsRemainStable()
    {
        var original = Blacklist("session.lock");
        const string json = "{\"SchemaVersion\":1,\"ScopeMode\":0,\"ScopeRules\":[],\"FilterMode\":0,\"FilterRules\":[\"session.lock\"],\"UseRegex\":false}";
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "folderrewind/effective-source-boundary/v1\n" + json))).ToLowerInvariant();
        Assert.AreEqual(json, System.Text.Json.JsonSerializer.Serialize(original));
        Assert.AreEqual(expected, original.Fingerprint);
        Assert.AreEqual(expected, System.Text.Json.JsonSerializer.Deserialize<EffectiveSourceBoundarySnapshot>(json)!.Fingerprint);
        var typed = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist,
            [new("session.lock", PathFilterTarget.File)], false);
        var copy = System.Text.Json.JsonSerializer.Deserialize<EffectiveSourceBoundarySnapshot>(System.Text.Json.JsonSerializer.Serialize(typed))!;
        Assert.AreEqual(typed.Fingerprint, copy.Fingerprint);
        Assert.AreEqual(PathFilterTarget.File, copy.Rules.Single().Target);
        Assert.AreNotEqual(expected, copy.Fingerprint);
        var scope = new SemanticMergeCoverage(copy, Root, []);
        Assert.AreEqual(MergePathPresence.Unknown, scope.Inspect("session.lock"));
        Assert.AreEqual(MergePathPresence.Absent, scope.Inspect("session.lock", isDirectory: true));
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "MergeCoverageWorld");
    private static MergeCoverageQuery Region(string root = "") => new("terrain", root, ["region/*.mca", "region/*.mcc", "dimensions/*/**/region/*.mca", "DIM-1/region/*.mca"]);
    private static EffectiveSourceBoundarySnapshot Blacklist(params string[] rules) => new(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, rules, false);

    [TestMethod]
    public void DomainFamiliesAllowUnrelatedUserFiltersAndDetectUnobservedDependencies()
    {
        var boundary = Blacklist("session.lock", "**/session.lock", "voxy/**", "**/distanthorizons.sqlite", "**/distanthorizons.sqlite-shm", "**/distanthorizons.sqlite-wal",
            "*.log", "screenshots/**", "notes.txt", "cache/**");
        var proof = new SemanticMergeCoverage(boundary, Root, ["region", "DIM-1", "DIM-1/region"]);
        var result = proof.Check(Region(), default);
        Assert.AreEqual(MergeCoverageState.Complete, result.State, System.Text.Json.JsonSerializer.Serialize(result));
        CollectionAssert.Contains(proof.RequiredAbsentPaths, "dimensions");
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(boundary, Root, ["region", "dimensions"]).Check(Region(), default).State,
            "A potentially filtered namespace cannot be inferred absent under an existing dimensions directory.");
        var live = Path.Combine(Path.GetTempPath(), "MergeCoverageGuard", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(live);
            HistoryMergeCommitBuilder.RequireCoveragePreconditions(live, proof.RequiredAbsentPaths, default);
            Directory.CreateDirectory(Path.Combine(live, "dimensions"));
            Assert.ThrowsExactly<HistoryMergeBlockedException>(() => HistoryMergeCommitBuilder.RequireCoveragePreconditions(live, proof.RequiredAbsentPaths, default));
        }
        finally { Directory.Delete(live, true); }
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(Blacklist("**/*.mcc"), Root).Check(Region(), default).State);
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(Blacklist("region/r.9.9.mca"), Root).Check(Region(), default).State,
            "A file absent from all three inventories must still participate in the coverage proof.");
        proof = new(Blacklist("world-b/entities/**"), Root);
        Assert.AreEqual(MergeCoverageState.Complete, proof.Check(new("entities", "world-a", ["entities/*.mca"]), default).State);
        var blocked = proof.Check(new("entities", "world-b", ["entities/*.mca"]), default);
        Assert.AreEqual(MergeCoverageState.Filtered, blocked.State);
        Assert.AreEqual("world-b/entities/**", blocked.Evidence.Single().Rule);
    }

    [TestMethod]
    public void InclusionProofAndUnknownRulesDoNotInventCompleteness()
    {
        var scope = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.Include, ["world-a/**"], EffectiveBoundaryFilterMode.Blacklist, [], false);
        Assert.AreEqual(MergeCoverageState.Complete, new SemanticMergeCoverage(scope, Root).Check(Region("world-a"), default).State);
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(scope, Root).Check(Region("world-b"), default).State);
        var white = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Whitelist, ["region/*.mca", "region/*.mcc"], false);
        Assert.AreEqual(MergeCoverageState.Complete, new SemanticMergeCoverage(white, Root).Check(new("region", "", ["region/*.mca", "region/*.mcc"]), default).State);
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(white, Root).Check(Region(), default).State);
        var regex = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Blacklist, ["regex:.*\\.log$"], true);
        Assert.AreEqual(MergeCoverageState.Unknown, new SemanticMergeCoverage(regex, Root).Check(Region(), default).State);
        Assert.AreEqual(MergeCoverageState.Unknown, new SemanticMergeCoverage(new(EffectiveBoundaryScopeMode.Include, ["region/r.[01].*.mca"], EffectiveBoundaryFilterMode.Blacklist, [], false), Root).Check(Region(), default).State);
    }

    [TestMethod]
    public async Task PathFactsDistinguishPresentAbsentFilteredAndUnknownAndExpire()
    {
        var tree = MergeTreeManifest.Create(new Dictionary<string, MergeFileValue> { ["document.txt"] = new("unused", "digest", 3) }) with { HasCompleteDirectoryInventory = true };
        using var view = new SemanticMergeSourceView(tree, Root, default, new(Blacklist("private.txt"), Root));
        Assert.AreEqual(MergePathPresence.Present, (await view.InspectPathAsync("document.txt", default)).Presence);
        Assert.AreEqual(MergePathPresence.Absent, (await view.InspectPathAsync("removed.txt", default)).Presence);
        Assert.AreEqual(MergePathPresence.Filtered, (await view.InspectPathAsync("private.txt", default)).Presence);
        using var fileOnly = new SemanticMergeSourceView(tree with { HasCompleteDirectoryInventory = false }, Root, default, new(Blacklist(), Root));
        Assert.AreEqual(MergePathPresence.Unknown, (await fileOnly.InspectPathAsync("dimensions", default)).Presence);
        using var unknown = new SemanticMergeSourceView(MergeTreeManifest.Empty, Root, default);
        Assert.AreEqual(MergePathPresence.Unknown, (await unknown.InspectPathAsync("removed.txt", default)).Presence);
        view.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => view.CheckCoverageAsync(Region(), default));
    }

    [TestMethod]
    public void ProofAccountsForHistoricalMatcherFilenameSegmentsAncestorsAndUnicodeCase()
    {
        var literalWhite = new EffectiveSourceBoundarySnapshot(EffectiveBoundaryScopeMode.All, [], EffectiveBoundaryFilterMode.Whitelist, ["region/r.0.0.mcK"], false);
        Assert.AreEqual(MergeCoverageState.Filtered, new SemanticMergeCoverage(literalWhite, Root).Check(new("literal", "", ["region/r.0.0.mck"]), default).State);
        foreach (var rule in new[] { "region", "*.mca", "r.?.?.mca", "MergeCoverageWorld/region", "REGION/*.MCA", "region/*.mcK" })
        {
            var boundary = Blacklist(rule); var proof = new SemanticMergeCoverage(boundary, Root);
            var query = new MergeCoverageQuery("test", "", ["region/*.mca", "region/*.mck"]);
            Assert.AreEqual(MergeCoverageState.Filtered, proof.Check(query, default).State, rule);
            var matcher = new HistoryRestoreTreeScope(new(SourceId.New(), Root, boundary));
            Assert.IsTrue(new[] { "region/r.0.0.mca", "region/r.0.0.mck" }.Any(p => !matcher.Includes(p)), rule);
        }
    }
}
