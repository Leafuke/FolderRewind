using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupInvocationOptionsTests
{
    [TestMethod]
    public void ConfigurationBackupCommentSurvivesInvocationOptionCopies()
    {
        var options = BackupInvocationOptions.ForManual("before update")
            .WithApplicationConsistentSnapshot()
            .WithComment("release checkpoint");

        Assert.AreEqual(BackupInvocationSource.Manual, options.Source);
        Assert.IsTrue(options.PreferApplicationConsistentSnapshot);
        Assert.AreEqual("release checkpoint", options.Comment);
    }
}
