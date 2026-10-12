using FolderRewind.Models;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class CloudConnectionModelTests
{
    [TestMethod]
    [DataRow("remote:", "remote:_folderrewind/history")]
    [DataRow("remote:/", "remote:/_folderrewind/history")]
    [DataRow("remote:path", "remote:path/_folderrewind/history")]
    [DataRow("remote:/path", "remote:/path/_folderrewind/history")]
    public void ObjectPathsPreserveOriginalRemoteSemantics(string root, string expected)
    {
        Assert.AreEqual(expected, CloudRemotePath.Append(root, "_folderrewind", "history"));
        var (remote, suffix) = CloudRemotePath.Split(root);
        Assert.AreEqual(root, remote + ":" + suffix);
    }

    [TestMethod]
    public void SharedConnectionHasIndependentProjectRootsAndStableTargetRevision()
    {
        var profile = new CloudConnectionProfile { Scope = CloudConnectionScope.Shared, RemoteName = "new_alias",
            CredentialReference = Path.GetFullPath("credential.conf") };
        var first = CloudConnectionResolver.Resolve(profile, "first", "/original", "binding-1");
        var second = CloudConnectionResolver.Resolve(profile, "second", "another", "binding-2");
        Assert.AreEqual("new_alias:/original", first.RemoteRoot);
        Assert.AreEqual("new_alias:another", second.RemoteRoot);
        Assert.AreEqual(first.ProfileRevision, second.ProfileRevision);
        profile.Scope = CloudConnectionScope.Project; profile.OwnerProjectId = "first";
        Assert.ThrowsExactly<InvalidOperationException>(() => CloudConnectionResolver.Resolve(profile, "second", "another", "binding-2"));
    }

    [TestMethod]
    [DataRow("C:\\local")]
    [DataRow(":local")]
    [DataRow("remote:path\nother:secret")]
    public void AmbiguousPathsAreRejectedInsteadOfGuessed(string path)
        => Assert.ThrowsExactly<InvalidDataException>(() => CloudRemotePath.Split(path));
}
