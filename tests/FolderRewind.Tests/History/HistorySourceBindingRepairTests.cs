using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistorySourceBindingRepairTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ConfigService.CurrentConfig = new();
        ConfigService.SaveResults.Clear();
        ConfigService.BeforeSave = null;
        ConfigService.SaveAsyncOverride = null;
    }

    [TestMethod]
    public async Task GateWaitRechecksRevisionBeforeMutating()
    {
        var config = CreateConfig();
        var expected = config.ConfigRevision;
        var gate = await NativeHistoryConfigurationOperationGate.EnterAsync(config.Id);
        var operation = HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, Missing(), Path.GetTempPath(), expected);
        config.ConfigRevision = "concurrent";
        await gate.DisposeAsync();
        Assert.AreEqual(HistoryConfigurationRepairStatus.StaleConfig, (await operation).Status);
        Assert.IsEmpty(config.SourceFolders);
    }

    [TestMethod]
    public async Task BoundarySuccessKeepsTypedRuleIdentityAndRestoreWhitelist()
    {
        var config = CreateConfig();
        var folder = new ManagedFolder { Path = Path.GetTempPath() };
        config.SourceFolders.Add(folder);
        var retained = new PathFilterRule("keep", PathFilterTarget.Directory, 2);
        config.Filters.RestoreWhitelist.Add(retained);
        var rule = new PathFilterRule("cache", PathFilterTarget.Directory, 2);
        var historical = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.Include, ["region/**"],
            EffectiveBoundaryFilterMode.Whitelist, [rule], false);
        var revision = config.ConfigRevision;
        var result = await HistorySourceBindingRepairService.RestoreHistoricalBoundaryAsync(config,
            new(new(Guid.Parse(folder.Id)), historical, EffectiveSourceBoundarySnapshot.All), revision, true);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(BackupSourceScopeMode.Include, folder.SourceScope.Mode);
        CollectionAssert.AreEqual(new[] { "region/**" }, folder.SourceScope.IncludePatterns);
        Assert.AreEqual(BackupFilterMode.Whitelist, config.Filters.BackupFilterMode);
        Assert.AreEqual(rule, config.Filters.BackupWhitelist.Single());
        Assert.AreEqual(retained, config.Filters.RestoreWhitelist.Single());
        Assert.AreNotEqual(revision, config.ConfigRevision);
    }

    [TestMethod]
    public async Task ReplacedConfigWithSameRevisionIsRejected()
    {
        var config = CreateConfig();
        ConfigService.CurrentConfig.BackupConfigs[0] = new() { Id = config.Id, ConfigRevision = config.ConfigRevision };
        var result = await HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, Missing(), Path.GetTempPath(), config.ConfigRevision);
        Assert.AreEqual(HistoryConfigurationRepairStatus.StaleConfig, result.Status);
        Assert.IsEmpty(config.SourceFolders);
        Assert.IsEmpty(ConfigService.CurrentConfig.BackupConfigs[0].SourceFolders);
    }

    [TestMethod]
    public async Task CancellationAfterApplyWaitsForDurableResultAndKeepsGate()
    {
        var config = CreateConfig();
        var completion = new TaskCompletionSource<ConfigSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigService.SaveAsyncOverride = () => completion.Task;
        using var cancel = new CancellationTokenSource();
        var operation = HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, Missing(), Path.GetTempPath(), config.ConfigRevision, cancel.Token);
        Assert.HasCount(1, config.SourceFolders);
        cancel.Cancel();
        Assert.IsFalse(operation.IsCompleted);
        var next = NativeHistoryConfigurationOperationGate.EnterAsync(config.Id).AsTask();
        Assert.IsFalse(next.IsCompleted);
        completion.SetResult(new() { Success = true });
        Assert.IsTrue((await operation).Succeeded);
        await using var lease = await next;
    }

    [TestMethod]
    public async Task FailedBoundarySaveCompensatesOnlyItsOwnFields()
    {
        var config = CreateConfig();
        var folder = new ManagedFolder { Path = Path.GetTempPath() };
        config.SourceFolders.Add(folder);
        var scope = folder.SourceScope;
        var filters = config.Filters;
        var completion = new TaskCompletionSource<ConfigSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        ConfigService.SaveAsyncOverride = () => ++saves == 1 ? completion.Task : Task.FromResult(new ConfigSaveResult { Success = true });
        var operation = HistorySourceBindingRepairService.RestoreHistoricalBoundaryAsync(config, Mismatch(folder), config.ConfigRevision, true);
        config.DestinationPath = "concurrent destination";
        config.ConfigRevision = "concurrent revision";
        completion.SetResult(new() { ErrorMessage = "disk full" });
        Assert.AreEqual(HistoryConfigurationRepairStatus.SaveFailed, (await operation).Status);
        Assert.AreSame(scope, folder.SourceScope);
        Assert.AreSame(filters, config.Filters);
        Assert.AreEqual("concurrent destination", config.DestinationPath);
        Assert.AreEqual("concurrent revision", config.ConfigRevision);
        Assert.AreEqual(2, saves);
    }

    [TestMethod]
    public async Task FailedSavePreservesEditsToNewSourceAndConcurrentFilterRules()
    {
        var config = CreateConfig();
        var saves = 0;
        ConfigService.SaveAsyncOverride = () =>
        {
            if (++saves == 1)
            {
                config.SourceFolders.Single().DisplayName = "later name";
                return Task.FromResult(new ConfigSaveResult { ErrorMessage = "disk full" });
            }
            return Task.FromResult(new ConfigSaveResult { Success = true });
        };
        Assert.AreEqual(HistoryConfigurationRepairStatus.SaveFailed, (await HistorySourceBindingRepairService.RestoreMissingBindingAsync(
            config, Missing(), Path.GetTempPath(), config.ConfigRevision)).Status);
        Assert.AreEqual("later name", config.SourceFolders.Single().DisplayName);
        var added = PathFilterRule.Legacy("later-rule");
        ConfigService.SaveAsyncOverride = () =>
        {
            if (++saves == 3)
            {
                config.Filters.Blacklist.Add(added);
                return Task.FromResult(new ConfigSaveResult { ErrorMessage = "disk full" });
            }
            return Task.FromResult(new ConfigSaveResult { Success = true });
        };
        var folder = config.SourceFolders.Single();
        Assert.AreEqual(HistoryConfigurationRepairStatus.SaveFailed, (await HistorySourceBindingRepairService.RestoreHistoricalBoundaryAsync(
            config, Mismatch(folder), config.ConfigRevision, true)).Status);
        Assert.Contains(added, config.Filters.Blacklist);
    }

    [TestMethod]
    public async Task CompensationFailureReportsUncertainPersistence()
    {
        var config = CreateConfig();
        var saves = 0;
        ConfigService.SaveAsyncOverride = () => Task.FromResult(new ConfigSaveResult { ErrorMessage = "write " + ++saves + " failed" });
        var result = await HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, Missing(), Path.GetTempPath(), config.ConfigRevision);
        Assert.AreEqual(HistoryConfigurationRepairStatus.SaveFailed, result.Status);
        StringAssert.Contains(result.Diagnostic, "write 1 failed");
        StringAssert.Contains(result.Diagnostic, "write 2 failed");
        Assert.IsEmpty(config.SourceFolders);
    }

    [TestMethod]
    public async Task NormalizedAppliedPatternsStillCompensateAfterSaveFailure()
    {
        var config = CreateConfig();
        var folder = new ManagedFolder { Path = Path.GetTempPath(), SourceScope = new()
            { Mode = BackupSourceScopeMode.Include, IncludePatterns = ["saves/**"] } };
        config.SourceFolders.Add(folder);
        var previousScope = folder.SourceScope;
        var previousRevision = config.ConfigRevision;
        var boundary = EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.Include,
            ["region\\**", "REGION/**"], EffectiveBoundaryFilterMode.Blacklist, [], false);
        var saves = 0;
        ConfigService.SaveAsyncOverride = () =>
        {
            ConfigService.NormalizeSourceScopeForPersistence(folder.SourceScope);
            return Task.FromResult(++saves == 1 ? new ConfigSaveResult { ErrorMessage = "disk full" }
                : new ConfigSaveResult { Success = true });
        };
        var result = await HistorySourceBindingRepairService.RestoreHistoricalBoundaryAsync(config,
            new(new(Guid.Parse(folder.Id)), boundary, EffectiveSourceBoundarySnapshot.All), previousRevision, true);
        Assert.AreEqual(HistoryConfigurationRepairStatus.SaveFailed, result.Status);
        Assert.AreSame(previousScope, folder.SourceScope);
        CollectionAssert.AreEqual(new[] { "saves/**" }, folder.SourceScope.IncludePatterns);
        Assert.AreEqual(previousRevision, config.ConfigRevision);
        Assert.AreEqual(2, saves);
    }

    [TestMethod]
    public async Task MissingHistoricalIdentityAlreadyOwnedByAnotherConfigIsAConflict()
    {
        var config = CreateConfig();
        var missing = Missing();
        ConfigService.CurrentConfig.BackupConfigs.Add(new() { SourceFolders =
            [new ManagedFolder { Id = missing.SourceId.Value.ToString("D").ToUpperInvariant(), Path = Path.GetTempPath() }] });
        var saves = 0;
        ConfigService.BeforeSave = () => saves++;
        var result = await HistorySourceBindingRepairService.RestoreMissingBindingAsync(config, missing,
            Path.GetTempPath(), config.ConfigRevision);
        Assert.AreEqual(HistoryConfigurationRepairStatus.Conflict, result.Status);
        Assert.IsEmpty(config.SourceFolders);
        Assert.AreEqual(0, saves);
    }

    private static BackupConfig CreateConfig()
    {
        var config = new BackupConfig();
        ConfigService.CurrentConfig = new();
        ConfigService.CurrentConfig.BackupConfigs.Add(config);
        return config;
    }
    private static MissingHistoricalSource Missing() => new(SourceId.New(), new("historical", Path.GetTempPath()), Path.GetTempPath());
    private static HistorySourceBoundaryMismatch Mismatch(ManagedFolder folder) => new(new SourceId(Guid.Parse(folder.Id)),
        EffectiveSourceBoundarySnapshot.CreateTyped(EffectiveBoundaryScopeMode.Include, ["region/**"],
            EffectiveBoundaryFilterMode.Blacklist, [PathFilterRule.Legacy("historic-rule")], true), EffectiveSourceBoundarySnapshot.All);
}
