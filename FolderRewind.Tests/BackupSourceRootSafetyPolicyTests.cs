using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class BackupSourceRootSafetyPolicyTests
{
    [TestMethod]
    public void VolumeAndUserProfileRootsAreBroadButNestedFolderIsNot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(userProfile))!;

        Assert.IsTrue(BackupSourceRootSafetyPolicy.IsBroadRoot(volumeRoot));
        Assert.IsTrue(BackupSourceRootSafetyPolicy.IsBroadRoot(userProfile));
        Assert.IsFalse(BackupSourceRootSafetyPolicy.IsBroadRoot(Path.Combine(userProfile, "FolderRewind", "Game")));
    }

    [TestMethod]
    public void CurrentDriveRootRemainsBroadRegardlessOfWorkingDirectory()
    {
        var volumeRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;

        Assert.IsTrue(BackupSourceRootSafetyPolicy.IsBroadRoot(volumeRoot));
        Assert.IsTrue(BackupSourceRootSafetyPolicy.IsBroadRoot(
            Path.Combine(volumeRoot, "FolderRewind-RootPolicyRegression", "..")));
        Assert.IsFalse(BackupSourceRootSafetyPolicy.IsBroadRoot(
            Path.Combine(volumeRoot, "FolderRewind-RootPolicyRegression", "Game")));
    }
}
