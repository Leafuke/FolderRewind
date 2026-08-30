using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class RestoreModePolicyTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void RequestedModeAloneDeterminesOverwrite(
        bool requestedClean,
        bool expectedOverwrite)
    {
        Assert.AreEqual(
            expectedOverwrite,
            RestoreModePolicy.UseOverwrite(requestedClean));
    }
}
