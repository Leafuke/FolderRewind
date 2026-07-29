using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestoreModePolicyTests
{
    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public void PartialBackupsAlwaysUseOverwrite(
        bool isPartialBackup,
        bool requestedClean,
        bool expectedOverwrite)
    {
        Assert.AreEqual(
            expectedOverwrite,
            RestoreModePolicy.UseOverwrite(isPartialBackup, requestedClean));
    }
}
