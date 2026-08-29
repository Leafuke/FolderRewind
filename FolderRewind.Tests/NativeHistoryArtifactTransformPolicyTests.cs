using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeHistoryArtifactTransformPolicyTests
{
    [TestMethod]
    public void ConfiguredTransformIsBlockedUntilNativeHistoryIntegrationExists()
    {
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.MustBlock(null));
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(new ArtifactTransformPolicySettings()));
    }
}
