using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Services.KnotLink;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryVersionFileSelectorTests
{
    [TestMethod]
    public void RepeatedPayloadNamesCannotSelectAnArbitraryNativeVersion()
    {
        var first = VersionId.New(); var second = VersionId.New();
        var entries = new (VersionId, string?)[] { (first, "payload.7z"), (second, "payload.7z") };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => HistoryVersionFileSelector.FindUnique(entries, "PAYLOAD.7Z"));
        StringAssert.Contains(error.Message, "history.ambiguous-backup-name");
        Assert.AreEqual(first, HistoryVersionFileSelector.FindUnique([(first, "unique.7z"), (first, "UNIQUE.7Z")], "unique.7z"));
        Assert.IsNull(HistoryVersionFileSelector.FindUnique(entries, "missing.7z"));
    }

    [TestMethod]
    public void StableSelectorsAreAdvertisedAndCannotBeMixedWithLegacyNames()
    {
        var id = VersionId.New();
        Assert.IsNull(KnotLinkCommandValidator.ValidateOptions(KnotLinkCommandParser.Parse("cmd=RESTORE;version_id=" + id)));
        Assert.IsNotNull(KnotLinkCommandValidator.ValidateOptions(KnotLinkCommandParser.Parse("cmd=RESTORE;version_id=" + id + ";file=payload.7z")));
        Assert.IsNotNull(KnotLinkCommandValidator.ValidateOptions(KnotLinkCommandParser.Parse("cmd=RESTORE;version_id=not-an-id")));
        Assert.Contains("LIST_VERSIONS", FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.FolderCommands);
        foreach (var command in new[] { "RESTORE", "GET_IMPORTANCE", "MARK_IMPORTANT" })
            Assert.IsTrue(FolderRewind.Plugin.Abstractions.KnotLinkCoreCommands.Find(command)!.Arguments.Any(a => a.Name == "version_id"));
    }
}
