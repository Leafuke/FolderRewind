using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class KnotLinkCommandMatcherTests
{
    private static readonly KnotLinkCommandDescriptor CurrentSaveBackup = new(
        "BACKUP",
        "Back up the active world")
    {
        RequiredArguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["current_save"] = "true"
        }
    };

    [TestMethod]
    [DataRow("BACKUP", "true")]
    [DataRow("backup", "1")]
    [DataRow("Backup", "YES")]
    public void MatchingCommandAndBooleanSelectorAreAccepted(string command, string value)
    {
        Assert.IsTrue(KnotLinkCommandMatcher.Matches(
            CurrentSaveBackup,
            command,
            new Dictionary<string, string> { ["CURRENT_SAVE"] = value }));
    }

    [TestMethod]
    [DataRow("BACKUP", "false")]
    [DataRow("BACKUP", "0")]
    [DataRow("RESTORE", "true")]
    public void UnrelatedCoreRequestsAreNotClaimed(string command, string value)
    {
        Assert.IsFalse(KnotLinkCommandMatcher.Matches(
            CurrentSaveBackup,
            command,
            new Dictionary<string, string> { ["current_save"] = value }));
    }

    [TestMethod]
    public void MissingSelectorIsNotClaimed()
        => Assert.IsFalse(KnotLinkCommandMatcher.Matches(
            CurrentSaveBackup,
            "BACKUP",
            new Dictionary<string, string>()));
}
