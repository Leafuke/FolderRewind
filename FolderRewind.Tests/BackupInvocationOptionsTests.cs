using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;
using FolderRewind.Services;
using FolderRewind.Services.Plugins.V3;

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

    [TestMethod]
    public void AutomaticFullBackupRawFallbackIsSuccessfulWithWarnings()
    {
        // ADR 0004 deliberately treats an unattended full raw fallback as a
        // completed run. Selected-region and required-consistency requests
        // remain blocked by the operation resolver's existing policies.
        var invocation = BackupInvocationOptions.ForAutomatic();
        var kind = new ConfigKindDeclaration(
            new ConfigKindRef(new OwnerId("com.folderrewind.minerewind"), "minecraft-saves"),
            new LocalizedText("Minecraft Saves", new Dictionary<string, string>()),
            new LocalizedText("Minecraft saves", new Dictionary<string, string>()),
            "minecraft",
            BackupFallbackPolicy.RawWithWarnings,
            RestoreCoordinationPolicy.Required);

        var resolution = PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
            kind,
            PluginOperationKind.Backup,
            RuntimeState: null,
            ProviderScopeSelected: false,
            ScopeCapabilityAvailable: false,
            ConsistencyIntent.Prefer,
            ConsistencyCapabilityAvailable: false,
            RestoreCoordinatorAvailable: false));
        var outcome = PluginOperationResolver.Complete(resolution, OperationOutcome.Success);
        var hostResult = PluginBackupRequestResult.FromSource(
            BackupRunSourceStatus.NewArchive,
            outcome,
            hasWarnings: true);

        Assert.AreEqual(BackupInvocationSource.Automatic, invocation.Source);
        Assert.AreEqual(OperationReadiness.Degraded, resolution.Readiness);
        Assert.AreEqual(OperationOutcome.SuccessWithWarnings, hostResult.Outcome);
        Assert.IsTrue(hostResult.CreatedNewArchive, "Automation treats the raw archive as a completed run and does not retry it.");
    }
}
