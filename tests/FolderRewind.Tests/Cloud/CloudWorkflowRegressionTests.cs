using FolderRewind.Services;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.History.Cloud;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[TestCategory("ExternalTool")]
public sealed class CloudWorkflowRegressionTests
{
    [TestMethod]
    public void FinishedCredentialOwnerCleanupIsNotAnUnknownSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindCredentialCleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.conf"); File.WriteAllText(source, "[test]\ntype = local\n");
            // Recovery enumerated the owner while it still had a snapshot. By the time
            // it acquires ownership, that owner has removed both snapshot and registration.
            RcloneCredentialRefresh.RecoverIdleSnapshot(root, source);
            File.WriteAllText(Path.Combine(root, "rclone.conf"), "unique unregistered bytes");
            Assert.ThrowsExactly<InvalidOperationException>(() => RcloneCredentialRefresh.RecoverIdleSnapshot(root, source));
            Assert.AreEqual("unique unregistered bytes", File.ReadAllText(Path.Combine(root, "rclone.conf")));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task JobCompletionUsesTheTransitionTimeAndLegacyTimesStayUnknown()
    {
        await using var fixture = new HistoryChainRewriteFixture(); await fixture.InitializeAsync();
        var full = await fixture.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "test.txt"), "content"));
        var pack = (await fixture.History.Repository.ReadAllPacksAsync()).First(p => p.Pack.Objects.Any(o => o.Id == full.Representation.RepresentationId.ToString())).Pack;
        var journal = fixture.History.CloudJobs;
        var job = journal.RegisterManual(pack, new("profile", "revision", "binding", "test:remote", fixture.Config.Value), [full.Representation.RepresentationId]);
        Assert.IsNull(job.CompletedUtc);
        var before = DateTimeOffset.UtcNow;
        journal.Update(job with { Phase = CloudJobPhase.Succeeded, Protection = CloudProtectionDisposition.Released });
        var saved = journal.Read().Jobs.Single();
        Assert.IsTrue(saved.CompletedUtc >= before && saved.UpdatedUtc >= before);
        Assert.AreEqual(job.CreatedUtc, saved.CreatedUtc);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(saved, CloudJobJsonContext.Default.HistoryCloudJob))!.AsObject();
        legacy.Remove("CompletedUtc"); legacy.Remove("UpdatedUtc");
        var loaded = JsonSerializer.Deserialize(legacy.ToJsonString(), CloudJobJsonContext.Default.HistoryCloudJob)!;
        Assert.IsNull(loaded.CompletedUtc); Assert.IsNull(loaded.UpdatedUtc);
    }

    [TestMethod]
    public async Task DisplayPublishingRenamesWithoutChangingRepositoryIdentityAndBadDisplayIsOptional()
    {
        var executable = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_RCLONE");
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable)) Assert.Inconclusive("Set FOLDERREWIND_TEST_RCLONE.");
        await using var fixture = new HistoryChainRewriteFixture(); await fixture.InitializeAsync();
        var full = await fixture.AddAsync(null, p => File.WriteAllText(Path.Combine(p, "test.txt"), "content"));
        var config = Path.Combine(fixture.Root, "rclone.conf"); await File.WriteAllTextAsync(config, "[test]\ntype = local\n");
        using var context = new RcloneExecutionContext(executable, config, fixture.Root, "test:remote", Path.Combine(fixture.Root, "tasks"));
        var descriptor = HistoryRepositoryDescriptor.Create(fixture.Config).ToCanonicalBytes();
        await CloudRepositoryDisplayService.PublishAsync(fixture.History, context, "Original name", [full.Representation.RepresentationId], default);
        var first = await CloudRepositoryDisplayService.ReadAsync(context, fixture.Config, default);
        Assert.AreEqual("Original name", first?.ProjectName);
        await CloudRepositoryDisplayService.PublishAsync(fixture.History, context, "Renamed project", [full.Representation.RepresentationId], default);
        Assert.AreEqual("Renamed project", (await CloudRepositoryDisplayService.ReadAsync(context, fixture.Config, default))?.ProjectName);
        CollectionAssert.AreEqual(descriptor, HistoryRepositoryDescriptor.Create(fixture.Config).ToCanonicalBytes());
        var file = Directory.EnumerateFiles(Path.Combine(fixture.Root, "remote"), "display.v1.json", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(file, "broken");
        Assert.IsNull(await CloudRepositoryDisplayService.ReadAsync(context, fixture.Config, default));
        await File.WriteAllBytesAsync(file, new byte[8193]);
        Assert.IsNull(await CloudRepositoryDisplayService.ReadAsync(context, fixture.Config, default));
    }
    [TestMethod]
    [DataRow("cloud.tool-unavailable", "", "Tool")]
    [DataRow("cloud.source-config-unavailable", "", "Configuration")]
    [DataRow("", "HTTP 401 Unauthorized", "Authentication")]
    [DataRow("", "403 Forbidden", "Permission")]
    [DataRow("", "dial tcp: connectex: connection refused", "Offline")]
    [DataRow("", "context deadline exceeded", "Timeout")]
    [DataRow("", "[cloud.payload-missing]", "History")]
    [DataRow("", "[cloud.target-changed]", "Connection")]
    [DataRow("", "unknown failure", "Unknown")]
    public void CloudFailureHasSpecificRecovery(string code, string detail, string expected)
        => Assert.AreEqual(expected, CloudFailurePresentation.Kind(code, detail));

    [TestMethod]
    public async Task ConnectionObservationsSurviveReentryAndRemainBoundToTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "FolderRewindCloudObservation", Guid.NewGuid().ToString("N"));
        var key = CloudConnectionObservationStore.Key("exe", "config", "working", "remote:path", "revision");
        var changed = CloudConnectionObservationStore.Key("exe", "config", "working", "remote:elsewhere", "revision");
        var latest = new CloudConnectionObservation(key, true, false, DateTimeOffset.UtcNow, "Offline");
        try
        {
            await CloudConnectionObservationStore.SaveAsync(root, latest);
            await CloudConnectionObservationStore.SaveAsync(root, latest with { Succeeded = true, CheckedAtUtc = latest.CheckedAtUtc.AddMinutes(-1) });
            Assert.AreEqual(latest, CloudConnectionObservationStore.Load(root, key));
            Assert.IsNull(CloudConnectionObservationStore.Load(root, changed));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RepositoryDisplayIsAdvisoryAndRejectsForeignOrDamagedData()
    {
        var id = new HistoryConfigId(Guid.NewGuid().ToString("N"));
        var display = new CloudRepositoryDisplay(1, id.Value, "Same project", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        byte[] Bytes(CloudRepositoryDisplay value) => JsonSerializer.SerializeToUtf8Bytes(value, CloudDisplayJsonContext.Default.CloudRepositoryDisplay);
        Assert.AreEqual(display, CloudRepositoryDisplayService.Parse(Bytes(display), id));
        Assert.ThrowsExactly<InvalidDataException>(() => CloudRepositoryDisplayService.Parse(Bytes(display with { ConfigId = Guid.NewGuid().ToString("N") }), id));
        Assert.ThrowsExactly<InvalidDataException>(() => CloudRepositoryDisplayService.Parse(Bytes(display with { ProjectName = "bad\nname" }), id));
        Assert.ThrowsExactly<InvalidDataException>(() => CloudRepositoryDisplayService.Parse(Bytes(display with { SchemaVersion = 99 }), id));
        Assert.ThrowsExactly<InvalidDataException>(() => CloudRepositoryDisplayService.Parse(new byte[8193], id));
        Assert.ThrowsExactly<JsonException>(() => CloudRepositoryDisplayService.Parse("broken"u8.ToArray(), id));
    }
}
