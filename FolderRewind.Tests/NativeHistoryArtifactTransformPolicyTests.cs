using FolderRewind.Models;
using FolderRewind.Services.Plugins.V3;
using System.Text.Json;
using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Tests;

[TestClass]
public sealed class NativeHistoryArtifactTransformPolicyTests
{
    [TestMethod]
    public void OnlyExplicitCompatibleExperimentalPolicyEnablesCapture()
    {
        var policy = new ArtifactTransformPolicySettings
        {
            Transformer = new() { PluginId = "com.example.domain", TransformerId = "incremental" },
            Parameters = new() { ["representation"] = JsonSerializer.SerializeToElement("incremental-v1") }
        };
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy));
        policy.ExperimentalEnabled = true;
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.MustBlock(policy));
        var persisted = JsonSerializer.Deserialize<ArtifactTransformPolicySettings>(JsonSerializer.Serialize(policy))!;
        Assert.IsTrue(persisted.ExperimentalEnabled);
        Assert.IsFalse(JsonSerializer.Deserialize<ArtifactTransformPolicySettings>("{}")!.ExperimentalEnabled);
        policy.Parameters["representation"] = JsonSerializer.SerializeToElement("other");
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy));
    }

    [TestMethod]
    public void RollingAndPartialSourcesCannotEnableSemanticCapture()
    {
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.SupportsCapture(CoreCaptureMode.Smart, 1, false, false));
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.SupportsCapture(CoreCaptureMode.Rolling, 1, false, false));
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.SupportsCapture(CoreCaptureMode.Full, 1, false, true));
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.SupportsCapture(CoreCaptureMode.Full, 1, true, false));
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.SupportsCapture(CoreCaptureMode.Full, 0, false, false));
    }
    [TestMethod]
    public void LegacyPolicyCannotBypassExplicitOptIn()
    {
        Assert.IsFalse(NativeHistoryArtifactTransformPolicy.MustBlock(null));
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(new ArtifactTransformPolicySettings()));
        var policy = new ArtifactTransformPolicySettings();
        Assert.IsTrue(NativeHistoryArtifactTransformPolicy.MustBlock(policy));
    }
}
