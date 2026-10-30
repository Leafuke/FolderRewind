using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupSourceAvailabilityPolicyTests
{
    [TestMethod]
    public void EmptyIncludeSelectionIsUnavailableButEmptyWholeDirectoryIsNot()
    {
        var include = new BackupSourceScope { Mode = BackupSourceScopeMode.Include };
        var all = new BackupSourceScope { Mode = BackupSourceScopeMode.All };

        Assert.IsTrue(BackupSourceAvailabilityPolicy.IsUnavailable(include, 0));
        Assert.IsFalse(BackupSourceAvailabilityPolicy.IsUnavailable(all, 0));
        Assert.IsFalse(BackupSourceAvailabilityPolicy.IsUnavailable(include, 1));
    }
}
