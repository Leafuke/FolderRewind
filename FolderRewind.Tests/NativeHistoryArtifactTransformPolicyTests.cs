using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeHistoryArtifactTransformPolicyTests
{
    [TestMethod]
    public void ConfiguredTransformRemainsBlockedOutsideAcceptanceScope()
    {
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.MustBlock(null));
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(new ArtifactTransformPolicySettings()));
        var policy = new ArtifactTransformPolicySettings();
        using (NativeHistoryArtifactTransformPolicy.EnterAcceptance("isolated"))
        {
            Assert.IsFalse(NativeHistoryArtifactTransformPolicy.MustBlock(policy, "isolated"));
            Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy, "another-config"));
            Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy));
        }
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy, "isolated"));
    }
}
