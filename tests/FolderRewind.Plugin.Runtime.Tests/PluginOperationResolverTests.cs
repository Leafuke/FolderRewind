using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Operations;

namespace FolderRewind.Plugin.Runtime.Tests;

[TestClass]
public sealed class PluginOperationResolverTests
{
    private static readonly ConfigKindDeclaration Minecraft = new(
        new ConfigKindRef(new OwnerId("com.folderrewind.minerewind"), "minecraft-saves"),
        Text("Minecraft Saves"),
        Text("Minecraft save folders"),
        "minecraft",
        BackupFallbackPolicy.RawWithWarnings,
        RestoreCoordinationPolicy.Required);

    private static readonly ConfigKindDeclaration Core = new(
        new ConfigKindRef(new OwnerId("folderrewind.core"), "default"),
        Text("Folder"),
        Text("Folder backup"),
        "folder",
        BackupFallbackPolicy.Block,
        RestoreCoordinationPolicy.None);

    private static LocalizedText Text(string value)
        => new(value, new Dictionary<string, string>());

    [TestMethod]
    public void CoreOperationWithoutPluginIsReady()
    {
        var resolution = Resolve(Core, PluginOperationKind.Backup, runtime: null);

        Assert.AreEqual(OperationReadiness.Ready, resolution.Readiness);
        Assert.IsEmpty(resolution.Diagnostics);
    }

    [TestMethod]
    public void FullMinecraftBackupWithoutOwnerDegradesToRaw()
    {
        var resolution = Resolve(Minecraft, PluginOperationKind.Backup, runtime: null);

        Assert.AreEqual(OperationReadiness.Degraded, resolution.Readiness);
        Assert.AreEqual("plugin.backup_raw_fallback", resolution.Diagnostics.Single().Code);
        Assert.AreEqual(
            OperationOutcome.SuccessWithWarnings,
            PluginOperationResolver.Complete(resolution, OperationOutcome.Success));
    }

    [TestMethod]
    public void ProviderScopeNeverFallsBackToFull()
    {
        var resolution = Resolve(
            Minecraft,
            PluginOperationKind.Backup,
            PluginRuntimeState.Failed,
            providerScopeSelected: true);

        Assert.AreEqual(OperationReadiness.Blocked, resolution.Readiness);
        Assert.AreEqual("plugin.backup_scope_unavailable", resolution.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void RequiredConsistencyNeverDegrades()
    {
        var resolution = Resolve(
            Minecraft,
            PluginOperationKind.Backup,
            PluginRuntimeState.Active,
            consistency: ConsistencyIntent.Require,
            consistencyAvailable: false);

        Assert.AreEqual(OperationReadiness.Blocked, resolution.Readiness);
        Assert.AreEqual("plugin.backup_consistency_required", resolution.Diagnostics.Single().Code);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(PluginRuntimeState.Inactive)]
    [DataRow(PluginRuntimeState.Failed)]
    [DataRow(PluginRuntimeState.Draining)]
    public void RestoreOwnerUnavailableAlwaysBlocks(PluginRuntimeState? state)
    {
        var resolution = Resolve(Minecraft, PluginOperationKind.Restore, state);

        Assert.AreEqual(OperationReadiness.Blocked, resolution.Readiness);
        Assert.AreEqual("plugin.restore_owner_unavailable", resolution.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void RequiredRestoreCoordinatorMissingBlocksActiveOwner()
    {
        var resolution = Resolve(
            Minecraft,
            PluginOperationKind.Restore,
            PluginRuntimeState.Active,
            coordinatorAvailable: false);

        Assert.AreEqual(OperationReadiness.Blocked, resolution.Readiness);
        Assert.AreEqual("plugin.restore_coordinator_unavailable", resolution.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void ActiveOwnerWithCapabilitiesIsReady()
    {
        var backup = Resolve(Minecraft, PluginOperationKind.Backup, PluginRuntimeState.Active);
        var restore = Resolve(Minecraft, PluginOperationKind.Restore, PluginRuntimeState.Active);

        Assert.AreEqual(OperationReadiness.Ready, backup.Readiness);
        Assert.AreEqual(OperationReadiness.Ready, restore.Readiness);
    }

    [TestMethod]
    public void CleanupWarningPromotesSuccessfulOutcome()
    {
        var resolution = Resolve(Minecraft, PluginOperationKind.Restore, PluginRuntimeState.Active);
        var warning = new PluginDiagnostic(
            "plugin.restore_rejoin_failed",
            DiagnosticSeverity.Warning,
            "Restore",
            "com.folderrewind.minerewind",
            new Dictionary<string, string>());

        Assert.AreEqual(
            OperationOutcome.SuccessWithWarnings,
            PluginOperationResolver.Complete(resolution, OperationOutcome.Success, [warning]));
    }

    private static OperationResolution Resolve(
        ConfigKindDeclaration kind,
        PluginOperationKind operation,
        PluginRuntimeState? runtime,
        bool providerScopeSelected = false,
        ConsistencyIntent consistency = ConsistencyIntent.Prefer,
        bool consistencyAvailable = true,
        bool coordinatorAvailable = true)
        => PluginOperationResolver.Resolve(new PluginOperationResolutionRequest(
            kind,
            operation,
            runtime,
            providerScopeSelected,
            ScopeCapabilityAvailable: runtime == PluginRuntimeState.Active,
            consistency,
            consistencyAvailable,
            coordinatorAvailable));
}
