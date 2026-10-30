using FolderRewind.Services;
using FolderRewind.Services.Plugins;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AppUpdatePolicyTests
{
    private const string ApiUrl = "https://api.github.com/repos/Leafuke/FolderRewind/releases/latest";
    private const string MirrorUrl = "https://mirror.invalid/" + ApiUrl;
    private const string ReleaseUrl = AppUpdatePolicy.ReleasesUrl + "/tag/v1.9.6";
    private const string X64Name = "FolderRewind_1.9.6.0_Setup_x64.exe";
    private const string X64Url = AppUpdatePolicy.ReleasesUrl + "/download/v1.9.6/" + X64Name;
    private static readonly Version Current = new(1, 9, 4, 0);

    private static string FixtureText => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppUpdate", "v1.9.6.json"));

    private static GitHubReleaseService.GitHubLatestReleaseResult Fixture(bool official = true)
    {
        using var json = JsonDocument.Parse(FixtureText);
        return GitHubReleaseService.ParseMetadata(json.RootElement,
            official ? ApiUrl : MirrorUrl, official ? ApiUrl : MirrorUrl, ApiUrl, official ? "Official" : "Mirror");
    }

    private static AppUpdatePolicy.Decision Resolve(GitHubReleaseService.GitHubLatestReleaseResult release,
        InstallChannel channel = InstallChannel.Setup, Architecture os = Architecture.X64)
    {
        var decision = AppUpdatePolicy.Resolve(release, Current, channel, os, out var reason);
        Assert.IsNotNull(decision, $"Expected an update decision; reason={reason}");
        Assert.AreEqual(reason, decision.Reason);
        return decision;
    }

    [TestMethod]
    [DataRow(Architecture.X64, "x64", true)]
    [DataRow(Architecture.Arm64, "arm64", true)]
    [DataRow(Architecture.X64, "x64", false)]
    [DataRow(Architecture.Arm64, "arm64", false)]
    public void PublishedFixtureSelectsExactSetupOnOfficialAndMirrorMetadata(Architecture os, string arch, bool official)
    {
        var release = Fixture(official);
        Assert.HasCount(4, release.Assets);
        Assert.AreEqual(official, release.OfficialMetadata);
        var target = Resolve(release, os: os);
        Assert.AreEqual(UpdatePrimaryAction.OpenSetupDownload, target.Action);
        Assert.AreEqual(UpdateDecisionReason.None, target.Reason);
        Assert.AreEqual(new Version(1, 9, 6, 0), target.LatestVersion);
        Assert.AreEqual($"FolderRewind_1.9.6.0_Setup_{arch}.exe", target.AssetName);
        Assert.AreEqual(AppUpdatePolicy.ReleasesUrl + "/download/v1.9.6/" + target.AssetName, target.Uri.AbsoluteUri);
        Assert.AreEqual("github.com", target.Uri.Host);
    }

    [TestMethod]
    public void Arm64SelectionDependsOnOperatingSystemAndDoesNotNeedProcessArchitecture()
    {
        // The policy accepts OS architecture only; an emulated x64 process cannot override it.
        var target = Resolve(Fixture(), os: Architecture.Arm64);
        Assert.AreEqual("arm64", target.ArchitectureTag);
        Assert.AreEqual("FolderRewind_1.9.6.0_Setup_arm64.exe", target.AssetName);
    }

    [TestMethod]
    [DataRow((int)InstallChannel.Sideload)]
    [DataRow((int)InstallChannel.Developer)]
    [DataRow((int)InstallChannel.Unknown)]
    [DataRow(999)]
    public void UnconfirmedInstallationsOpenReleaseWithoutPreparingOrSwitchingInstallation(int channel)
    {
        var target = Resolve(Fixture(), (InstallChannel)channel);
        Assert.AreEqual(UpdatePrimaryAction.OpenReleasePage, target.Action);
        Assert.AreEqual(ReleaseUrl, target.Uri.AbsoluteUri);
        Assert.AreEqual(UpdateDecisionReason.ManualMigration, target.Reason);
        Assert.IsNull(target.AssetName);
    }

    [TestMethod]
    public void StoreAlwaysKeepsItsBuiltInProductTargetEvenWithForgedGitHubLinks()
    {
        var release = Fixture(false);
        release.HtmlUrl = "javascript:alert(1)";
        release.Assets = [new() { Name = X64Name, DownloadUrl = "https://evil.invalid/setup.exe" }];
        var target = Resolve(release, InstallChannel.Store);
        Assert.AreEqual(UpdatePrimaryAction.OpenStorePage, target.Action);
        Assert.AreEqual(AppUpdatePolicy.StoreProductUrl, target.Uri.AbsoluteUri);
        Assert.IsNull(target.AssetName);
    }

    [TestMethod]
    [DataRow(Architecture.X86)]
    [DataRow(Architecture.Arm)]
    [DataRow((Architecture)999)]
    public void UnsupportedSystemsNeverGuessX64EvenWithOneAsset(Architecture os)
    {
        var release = Fixture();
        release.Assets = [new() { Name = X64Name, DownloadUrl = X64Url }];
        AssertFallback(Resolve(release, os: os), UpdateDecisionReason.UnsupportedArchitecture);
    }

    [TestMethod]
    [DataRow("OtherApp_1.9.6.0_Setup_x64.exe")]
    [DataRow("FolderRewind_1.9.5.0_Setup_x64.exe")]
    [DataRow("FolderRewind_1.9.6_Setup_x64.exe")]
    [DataRow("FolderRewind_1.9.6.1_Setup_x64.exe")]
    [DataRow("FolderRewind_1.9.6.0_Setup_arm64.exe")]
    [DataRow("FolderRewind_1.9.6.0_Setup_x64.exe.bak")]
    [DataRow("FolderRewind_1.9.6.0_Setup_x64.EXE")]
    [DataRow("FolderRewind_1.9.6.0_x64.msi")]
    [DataRow("FolderRewind_1.9.6.0_x64.msix")]
    [DataRow("FolderRewind_1.9.6.0_x64.7z")]
    [DataRow("FolderRewind_1.9.6.0_Setup_x64.exe.sha256")]
    [DataRow("FolderRewind_1.9.6.0_Setup_x64.exe/../setup.exe")]
    public void WrongNamesNeverBecomeInstallerTargets(string name)
    {
        var release = Fixture();
        release.Assets = [new() { Name = name, DownloadUrl = X64Url }];
        AssertFallback(Resolve(release), UpdateDecisionReason.MissingAsset);
    }

    [TestMethod]
    public void MissingArchitectureAndDuplicateNamesFailClosed()
    {
        var release = Fixture();
        release.Assets = release.Assets.Where(asset => !asset.Name.EndsWith("x64.exe", StringComparison.Ordinal)).ToArray();
        AssertFallback(Resolve(release), UpdateDecisionReason.MissingAsset);

        release.Assets = [new() { Name = X64Name, DownloadUrl = X64Url }, new() { Name = X64Name, DownloadUrl = X64Url }];
        AssertFallback(Resolve(release), UpdateDecisionReason.AmbiguousAsset);
        release.Assets = [];
        AssertFallback(Resolve(release), UpdateDecisionReason.MissingAsset);
    }

    [TestMethod]
    public void ParserPreservesNamedAssetsWithMissingUrlsSoDuplicatesRemainAmbiguous()
    {
        var node = JsonNode.Parse(FixtureText)!;
        node["assets"]!.AsArray().Add(new JsonObject { ["name"] = X64Name });
        using var json = JsonDocument.Parse(node.ToJsonString());
        var release = GitHubReleaseService.ParseMetadata(json.RootElement, ApiUrl, ApiUrl, ApiUrl, "Official");
        AssertFallback(Resolve(release), UpdateDecisionReason.AmbiguousAsset);
    }

    private static IEnumerable<string> AttackUrls(string valid)
    {
        var path = new Uri(valid).AbsolutePath;
        foreach (var authority in new[]
        {
            "http://github.com", "ftp://github.com", "file://github.com", "javascript://github.com",
            "https://github.com.evil.invalid", "https://github.com.", "https://evil.invalid",
            "https://github.com@evil.invalid", "https://user@github.com", "https://user:password@github.com",
            "https://github.com:444", "https://github.com:80", "https://github.com:0443",
            "https://gіthub.com", "//github.com", "https://mirror.invalid/https://github.com"
        }) yield return authority + path;
        foreach (var suffix in new[] { "?download=1", "?", "#fragment", "#", " ", "\n", "%0a", "/" })
            yield return valid + suffix;
        yield return " " + valid;
        yield return valid.Replace("/Leafuke/", "/OtherOwner/", StringComparison.Ordinal);
        yield return valid.Replace("/FolderRewind/", "/OtherRepo/", StringComparison.Ordinal);
        yield return valid.Replace("v1.9.6", "v1.9.5", StringComparison.Ordinal);
        yield return valid.Replace("/Leafuke/", "/%4Ceafuke/", StringComparison.Ordinal);
        yield return valid.Replace("/Leafuke/", "/Leafuke%2f", StringComparison.Ordinal);
        yield return valid.Replace("/Leafuke/", "/Leafuke%5c", StringComparison.Ordinal);
        yield return valid.Replace("/releases/", "/ignored/../releases/", StringComparison.Ordinal);
        yield return valid.Replace("/releases/", "/%2e%2e/releases/", StringComparison.Ordinal);
        yield return valid.Replace("/releases/", "/%252e%252e/releases/", StringComparison.Ordinal);
        yield return valid.Replace("/releases/", "\\releases/", StringComparison.Ordinal);
        yield return valid.Replace("/Leafuke/", "/leafuke/", StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> UnsafeDownloadUrls() => AttackUrls(X64Url)
        .Append(X64Url.Replace(X64Name, "FolderRewind_1.9.6.0_Setup_arm64.exe", StringComparison.Ordinal))
        .Select(url => new object[] { url });

    public static IEnumerable<object[]> UnsafeReleaseUrls() => AttackUrls(ReleaseUrl)
        .Append(AppUpdatePolicy.ReleasesUrl).Select(url => new object[] { url });

    [TestMethod]
    [DynamicData(nameof(UnsafeDownloadUrls))]
    public void ForgedDownloadUrlFallsBackInsteadOfRewritingOrLaunchingIt(string url)
    {
        var release = Fixture(false);
        release.Assets = [new() { Name = X64Name, DownloadUrl = url }];
        AssertFallback(Resolve(release), UpdateDecisionReason.UnsafeAssetUrl);
    }

    [TestMethod]
    [DynamicData(nameof(UnsafeReleaseUrls))]
    public void ForgedReleaseUrlCannotReachInstallerOrBrowserTarget(string url)
    {
        var release = Fixture(false);
        release.HtmlUrl = url;
        AssertFallback(Resolve(release), UpdateDecisionReason.UnsafeReleaseUrl);
    }

    [TestMethod]
    public void CanonicalHostCasingAndExplicitHttpsPortStillProduceBuiltInCanonicalTarget()
    {
        var release = Fixture(false);
        release.HtmlUrl = ReleaseUrl.Replace("https://github.com", "HTTPS://GITHUB.COM:443", StringComparison.Ordinal);
        release.Assets = [new() { Name = X64Name, DownloadUrl = X64Url.Replace("https://github.com", "HTTPS://GITHUB.COM:443", StringComparison.Ordinal) }];
        Assert.AreEqual(X64Url, Resolve(release).Uri.AbsoluteUri);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.9.6")]
    [DataRow("release-v1.9.6")]
    [DataRow("v1.9.6-beta")]
    [DataRow("v1.9.6.0")]
    [DataRow("v1.9")]
    [DataRow("v1")]
    [DataRow("v01.9.6")]
    [DataRow("v1.09.6")]
    [DataRow("v1.9.06")]
    [DataRow("v256.9.6")]
    [DataRow("v1.256.6")]
    [DataRow("v1.9.65536")]
    [DataRow("v999999999999999.9.6")]
    [DataRow("v-1.9.6")]
    [DataRow("v1.9.6\n")]
    [DataRow(" v1.9.6")]
    [DataRow("v١.9.6")]
    [DataRow("v1.9.6/../other")]
    public void InvalidOrNonProductionTagsDoNotGenerateUpdateNotifications(string tag)
    {
        var release = Fixture();
        release.TagName = tag;
        Assert.IsNull(AppUpdatePolicy.Resolve(release, Current, InstallChannel.Setup, Architecture.X64, out var reason));
        Assert.AreEqual(UpdateDecisionReason.InvalidMetadata, reason);
    }

    [TestMethod]
    [DataRow("v0.0.0", "0.0.0.0")]
    [DataRow("v255.255.65535", "255.255.65535.0")]
    public void VersionBoundsMatchReleaseWorkflow(string tag, string expected) =>
        Assert.AreEqual(new Version(expected), AppUpdatePolicy.ParseReleaseVersion(tag));

    [TestMethod]
    public void DraftPrereleaseMissingFlagsAndFetchFailureCannotGenerateNotifications()
    {
        var release = Fixture();
        release.IsDraft = true;
        AssertSuppressed(release, UpdateDecisionReason.DraftOrPrerelease);
        release.IsDraft = false;
        release.IsPrerelease = true;
        AssertSuppressed(release, UpdateDecisionReason.DraftOrPrerelease);
        release.IsPrerelease = null;
        AssertSuppressed(release, UpdateDecisionReason.InvalidMetadata);
        release.IsPrerelease = false;
        release.IsDraft = null;
        AssertSuppressed(release, UpdateDecisionReason.InvalidMetadata);
        release.IsDraft = false;
        release.TagName = null;
        AssertSuppressed(release, UpdateDecisionReason.InvalidMetadata);
        release = Fixture();
        release.ErrorMessage = "fetch-failed";
        AssertSuppressed(release, UpdateDecisionReason.InvalidMetadata);
    }

    [TestMethod]
    public void WrongBooleanTypesAreNotTreatedAsPublishedStableRelease()
    {
        var node = JsonNode.Parse(FixtureText)!;
        node["draft"] = "false";
        node.AsObject().Remove("prerelease");
        using var json = JsonDocument.Parse(node.ToJsonString());
        var release = GitHubReleaseService.ParseMetadata(json.RootElement, ApiUrl, ApiUrl, ApiUrl, "Official");
        AssertSuppressed(release, UpdateDecisionReason.InvalidMetadata);
    }

    [TestMethod]
    [DataRow("1.9.6")]
    [DataRow("1.9.6.0")]
    [DataRow("1.9.6.1")]
    [DataRow("1.9.7.0")]
    public void EqualAndOlderReleaseDoesNotNotify(string current)
    {
        Assert.IsNull(AppUpdatePolicy.Resolve(Fixture(), new Version(current), InstallChannel.Setup, Architecture.X64, out var reason));
        Assert.AreEqual(UpdateDecisionReason.NotNewer, reason);
    }

    [TestMethod]
    public void UnknownCurrentVersionDoesNotNotify()
    {
        Assert.IsNull(AppUpdatePolicy.Resolve(Fixture(), null, InstallChannel.Setup, Architecture.X64, out var reason));
        Assert.AreEqual(UpdateDecisionReason.InvalidMetadata, reason);
    }

    [TestMethod]
    public void OfficialMetadataRequiresBothOriginalAndFinalOfficialApiEndpoint()
    {
        Assert.IsTrue(GitHubReleaseService.IsOfficialMetadataSource(ApiUrl, ApiUrl, ApiUrl));
        foreach (var url in AttackUrls(ApiUrl).Append(MirrorUrl).Where(url => url != ApiUrl))
        {
            Assert.IsFalse(GitHubReleaseService.IsOfficialMetadataSource(ApiUrl, url, ApiUrl), "Final response: " + url);
            Assert.IsFalse(GitHubReleaseService.IsOfficialMetadataSource(url, ApiUrl, ApiUrl), "Original request: " + url);
        }
        Assert.IsFalse(GitHubReleaseService.IsOfficialMetadataSource(ApiUrl, null, ApiUrl));
        Assert.IsFalse(GitHubReleaseService.IsOfficialMetadataSource(ApiUrl,
            ApiUrl.Replace("/latest", "/tags/v1.9.6", StringComparison.Ordinal), ApiUrl));
    }

    [TestMethod]
    public void RedirectedResponseIsMarkedUntrustedByProductionParser()
    {
        using var json = JsonDocument.Parse(FixtureText);
        var redirected = GitHubReleaseService.ParseMetadata(json.RootElement, ApiUrl, MirrorUrl, ApiUrl, "Official");
        Assert.IsFalse(redirected.OfficialMetadata);
        Assert.AreEqual(X64Url, Resolve(redirected).Uri.AbsoluteUri);
    }

    [TestMethod]
    public void SharedParserDoesNotApplyFolderRewindPolicyToKnotLinkMetadata()
    {
        using var json = JsonDocument.Parse("""
            {"tag_name":"v1.2.3.4","name":"KnotLink","body":"notes","assets":[
            {"name":"KnotLinkService-1.2.3.4-windows-x86-Installer.exe",
             "browser_download_url":"https://github.com/KnotLink-Protocol/KnotLinkService/releases/download/v1.2.3.4/setup.exe",
             "size":123,"digest":"sha256:test"}]}
            """);
        const string api = "https://api.github.com/repos/KnotLink-Protocol/KnotLinkService/releases/latest";
        var release = GitHubReleaseService.ParseMetadata(json.RootElement, api, api, api, "Official");
        Assert.IsTrue(release.OfficialMetadata);
        Assert.AreEqual("v1.2.3.4", release.TagName);
        Assert.AreEqual("notes", release.Body);
        Assert.HasCount(1, release.Assets);
        Assert.AreEqual("KnotLinkService-1.2.3.4-windows-x86-Installer.exe", release.Assets[0].Name);
        Assert.AreEqual(123L, release.Assets[0].SizeBytes);
        Assert.AreEqual("sha256:test", release.Assets[0].Digest);
    }

    [TestMethod]
    public void ValidatedTargetIsIndependentOfLaterMetadataMutation()
    {
        var release = Fixture();
        var target = Resolve(release);
        release.HtmlUrl = "https://evil.invalid";
        release.TagName = "v9.9.9";
        foreach (var asset in release.Assets) asset.DownloadUrl = "file:///C:/malware.exe";
        Assert.AreEqual(X64Url, target.Uri.AbsoluteUri);
        Assert.AreEqual("v1.9.6", target.LatestTag);
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void InjectedDecisionsRenderActualLocalizedDialogAndChannelHints(string locale)
    {
        var strings = ReadResources(locale);
        string Get(string key) => strings[key];
        var setup = Resolve(Fixture(false));
        var content = AppUpdatePresentation.CreateContent(setup, "release notes", false, Get);
        StringAssert.Contains(content, "1.9.4.0");
        StringAssert.Contains(content, "1.9.6.0");
        StringAssert.Contains(content, "release notes");
        StringAssert.Contains(content, Get("Update_Dialog_MirrorMetadataHint"));
        StringAssert.Contains(content, Get("Update_Dialog_SetupExitHint"));
        Assert.IsFalse(content.Contains(Get("Update_Dialog_ManualMigrationHint"), StringComparison.Ordinal));
        Assert.AreEqual(Get("Update_Dialog_DownloadSetup"), Get(AppUpdatePresentation.PrimaryButtonResource(setup)));

        foreach (var channel in new[] { InstallChannel.Sideload, InstallChannel.Developer, InstallChannel.Unknown })
        {
            var manual = Resolve(Fixture(), channel);
            content = AppUpdatePresentation.CreateContent(manual, "", true, Get);
            StringAssert.Contains(content, Get("Update_Dialog_ManualMigrationHint"));
            StringAssert.Contains(content, Get("Update_Dialog_EmptyNotes"));
            Assert.AreEqual(Get("Update_Dialog_OpenRelease"), Get(AppUpdatePresentation.PrimaryButtonResource(manual)));
        }

        var store = Resolve(Fixture(), InstallChannel.Store);
        content = AppUpdatePresentation.CreateContent(store, "notes", true, Get);
        StringAssert.Contains(content, Get("Update_Dialog_StoreDelayHint"));
        Assert.IsFalse(content.Contains(Get("Update_Dialog_SetupExitHint"), StringComparison.Ordinal));
        Assert.AreEqual(Get("Update_Dialog_OpenStore"), Get(AppUpdatePresentation.PrimaryButtonResource(store)));

        var unsafeRelease = Fixture(false);
        unsafeRelease.HtmlUrl = "https://evil.invalid";
        content = AppUpdatePresentation.CreateContent(Resolve(unsafeRelease), "notes", false, Get);
        StringAssert.Contains(content, Get("Update_Dialog_UnsafeMetadataHint"));
        Assert.IsFalse(content.Contains("evil.invalid", StringComparison.Ordinal));
        var missing = Fixture();
        missing.Assets = [];
        StringAssert.Contains(AppUpdatePresentation.CreateContent(Resolve(missing), "notes", true, Get),
            Get("Update_Dialog_PackageUnavailableHint"));
        StringAssert.Contains(AppUpdatePresentation.CreateContent(Resolve(Fixture(), os: Architecture.X86), "notes", true, Get),
            Get("Update_Dialog_UnsupportedArchitectureHint"));
    }

    private static Dictionary<string, string> ReadResources(string locale)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx")))
            directory = directory.Parent;
        Assert.IsNotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", "FolderRewind", "Strings", locale, "Resources.resw"))
            .Root!.Elements("data").ToDictionary(element => (string)element.Attribute("name")!,
                element => element.Element("value")!.Value, StringComparer.Ordinal);
    }

    private static void AssertFallback(AppUpdatePolicy.Decision target, UpdateDecisionReason reason)
    {
        Assert.AreEqual(UpdatePrimaryAction.OpenReleasePage, target.Action);
        Assert.AreEqual(AppUpdatePolicy.ReleasesUrl, target.Uri.AbsoluteUri);
        Assert.AreEqual(reason, target.Reason);
        Assert.IsNull(target.AssetName);
    }

    private static void AssertSuppressed(GitHubReleaseService.GitHubLatestReleaseResult release, UpdateDecisionReason expected)
    {
        Assert.IsNull(AppUpdatePolicy.Resolve(release, Current, InstallChannel.Setup, Architecture.X64, out var reason));
        Assert.AreEqual(expected, reason);
    }
}
