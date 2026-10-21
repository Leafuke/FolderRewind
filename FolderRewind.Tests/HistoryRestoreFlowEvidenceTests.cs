using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.History.LocalState;
using FolderRewind.History.Storage;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryRestoreFlowEvidenceTests
{
    [TestMethod]
    public async Task MetadataDifferenceIsReportedBeforeOpeningUnrelatedLockedFileContents()
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        File.WriteAllText(Path.Combine(f.Target, "new-file"), "low-cost difference");
        using var busy = new FileStream(Path.Combine(f.Target, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var probe = new HistoryExactWorkingStateProbe(f.History, f.Restore());
        var compared = await probe.CompareAsync(new(f.Source, f.Target), f.Before.GetSourceState(f.Source), default);
        Assert.AreEqual(HistoryWorkingStateComparisonStatus.Different, compared.Status, compared.Reason);
    }
    [TestMethod]
    [DataRow("equal", 1L, false)]
    [DataRow("different-tip", 2L, false)]
    [DataRow("different", 2L, true)]
    [DataRow("unknown", 1L, true)]
    public async Task CheckoutPreparesOnceAndProtectsOnlyWhenEqualityCannotBeProved(string scenario, long extractions, bool protectedWork)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
        if (scenario == "different")
        {
            var path = Path.Combine(f.Target, "a.txt"); var time = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(path, "edited"); File.SetLastWriteTimeUtc(path, time);
        }
        if (scenario == "unknown")
        {
            var next = workspace.WithSourceStates([workspace.GetSourceState(f.Source) with { RequiresFullCapture = true }]);
            await f.History.WorkspaceStore.SaveAsync(next, workspace.StateRevision); workspace = next;
        }
        var protector = new CapturingProtector(f);
        var restore = f.Restore(targetVolume: true, prepare: (_, _, _) => throw new AssertFailedException("Checkout must remain Exact; no ordinary patch."));
        var selected = scenario == "equal" ? f.Ours.NewBranchUpdates.Single() : f.Theirs.NewBranchUpdates.Single();
        var stats = HistoryOperationDiagnostics.Begin("checkout-protection-proof");
        HistoryRestoreResult result;
        using (stats)
            result = await new HistoryCheckoutService(f.History, restore, protector).CheckoutAsync(selected.UpdateId,
                [new(f.Source, f.Target), new(f.Other, f.OtherTarget)], workspace, HistoryCheckoutProtectionMode.ProtectCurrentWork);
        Assert.IsTrue(result.Succeeded, result.Diagnostic);
        Assert.AreEqual(protectedWork ? 1 : 0, protector.Calls);
        Assert.AreEqual(extractions, stats.Summary!.Counters.GetValueOrDefault("7z-x"));
        Assert.AreEqual(scenario == "unknown" ? 0L : 1L, stats.Summary.Counters.GetValueOrDefault("baseline-comparisons"));
        if (scenario == "equal") Assert.AreEqual(0L, stats.Summary.Counters.GetValueOrDefault("file-moves"));
        Assert.AreEqual(selected.BranchId, (await f.History.WorkspaceStore.LoadAsync()).Value!.GetSourceState(f.Source).ActiveBranchId);
        Assert.AreEqual("untouched", File.ReadAllText(Path.Combine(f.OtherTarget, "keep.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeldEqualityEvidenceSupportsNonemptyDirectoryRetirementAndRollback(bool fail)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var shape = Path.Combine(f.Target, "shape"); Directory.CreateDirectory(Path.Combine(shape, "sub"));
        File.WriteAllText(Path.Combine(shape, "sub", "old"), "original subtree");
        var baseline = await f.CaptureAsync(f.Source);
        Directory.Delete(shape, true); File.WriteAllText(shape, "replacement");
        var target = await f.CaptureAsync(f.Source);
        File.Delete(shape); Directory.CreateDirectory(Path.Combine(shape, "sub")); File.WriteAllText(Path.Combine(shape, "sub", "old"), "original subtree");
        var original = WindowsRestoreFileSystem.Identity(Path.Combine(shape, "sub", "old"));
        var workspace = (await f.History.WorkspaceStore.LoadAsync()).Value!;
        var current = workspace.WithSourceStates([workspace.GetSourceState(f.Source) with
        { BaseVersionId = baseline.NewVersions.Single().VersionId, Relation = WorkspaceBaselineRelation.Exact, RequiresFullCapture = false }]);
        await f.History.WorkspaceStore.SaveAsync(current, workspace.StateRevision);
        try
        {
            if (fail) HistoryRestoreFileSystemPlanStore.TestFault = point => { if (point == "after-move-0") throw new IOException("interrupted retirement"); };
            var stats = HistoryOperationDiagnostics.Begin("held-equality-delta");
            HistoryRestoreResult result;
            using (stats)
                result = await new HistoryCheckoutService(f.History, f.Restore(targetVolume: true), new RejectProtection()).CheckoutAsync(
                    target.NewBranchUpdates.Single().UpdateId, [new(f.Source, f.Target)], current, HistoryCheckoutProtectionMode.ProtectCurrentWork);
            Assert.AreEqual(fail ? HistoryRestoreStatus.MutationFailedRolledBack : HistoryRestoreStatus.Committed, result.Status, result.Diagnostic);
            Assert.AreEqual(1L, stats.Summary!.Counters.GetValueOrDefault("file-delta-sources"));
            Assert.AreEqual(0L, stats.Summary.Counters.GetValueOrDefault("directory-swap-sources"));
            if (fail)
            {
                Assert.AreEqual("original subtree", File.ReadAllText(Path.Combine(shape, "sub", "old")));
                Assert.AreEqual(original, WindowsRestoreFileSystem.Identity(Path.Combine(shape, "sub", "old")));
            }
            else Assert.AreEqual("replacement", File.ReadAllText(shape));
        }
        finally { HistoryRestoreFileSystemPlanStore.TestFault = null; }
    }

    [TestMethod]
    [DataRow("metadata", 0L)]
    [DataRow("first", 131072L)]
    [DataRow("last", 8388608L)]
    [DataRow("equal", 8388608L)]
    public async Task ComparisonRejectsCheapDifferencesAndStopsAtFirstChangedBytes(string scenario, long readBytes)
    {
        await using var f = new HistoryMergeArchiveFixture(); await f.SeedAsync();
        var stage = Path.Combine(f.Root, "comparison-staging"); Directory.CreateDirectory(stage);
        var binding = new HistoryRestoreSourceBinding(f.Source, f.Target);
        foreach (var file in Directory.GetFiles(f.Target)) File.Delete(file);
        var bytes = new byte[1024 * 1024];
        for (int i = 0; i < 4; i++)
        {
            File.WriteAllBytes(Path.Combine(f.Target, i + ".bin"), bytes);
            File.WriteAllBytes(Path.Combine(stage, i + ".bin"), bytes);
        }
        var changed = Path.Combine(f.Target, scenario == "last" ? "3.bin" : "0.bin");
        var stamp = File.GetLastWriteTimeUtc(changed);
        if (scenario == "metadata") File.AppendAllText(changed, "extra");
        else if (scenario != "equal")
        {
            using var file = new FileStream(changed, FileMode.Open, FileAccess.Write);
            if (scenario == "last") file.Position = file.Length - 1;
            file.WriteByte(1);
        }
        File.SetLastWriteTimeUtc(changed, stamp);
        var prepared = new HistoryRestoreService.PreparedRestoreSource(binding, f.Ours.NewVersions.Single(), MaterializationFidelity.Exact, HistoryRestoreApplyMode.Clean, stage);
        var stats = HistoryOperationDiagnostics.Begin("stream-comparison");
        using (stats)
        using (var evidence = await HistoryExactWorkingStateProbe.ComparePreparedWithEvidenceAsync(prepared, binding, default))
        {
            Assert.AreEqual(scenario == "equal", evidence.Comparison.IsEqual);
            if (scenario == "equal")
            {
                evidence.Validate();
                Assert.Throws<IOException>(() => File.WriteAllText(changed, "must remain locked"));
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => HistoryExactWorkingStateProbe.MatchesPreparedAsync(prepared, binding, cancel.Token));
            }
        }
        Assert.AreEqual(readBytes, stats.Summary!.Counters.GetValueOrDefault("working-compare-bytes"));
        File.WriteAllText(changed, "resources released");
    }

    private sealed class RejectProtection : IHistoryWorkingStateProtector
    {
        public Task<HistoryWorkspace> ProtectAsync(HistoryWorkspace expectedWorkspace, CancellationToken cancellationToken)
            => throw new AssertFailedException("Stable equality must avoid a snapshot.");
    }
    private sealed class CapturingProtector(HistoryMergeArchiveFixture fixture) : IHistoryWorkingStateProtector, IHistoryWorkingStateProtectorInsideOperation
    {
        internal int Calls;
        public Task<HistoryWorkspace> ProtectAsync(HistoryWorkspace expectedWorkspace, CancellationToken cancellationToken)
            => throw new AssertFailedException("The same configuration lease must be passed.");
        public async Task<HistoryWorkspace> ProtectInsideOperationAsync(HistoryWorkspace expectedWorkspace,
            NativeHistoryConfigurationOperationGate.Lease operation, CancellationToken token,
            IReadOnlyList<HistoryRestoreSourceBinding>? physicalBindings = null)
        {
            operation.Require(fixture.History.ConfigId); Calls++;
            Assert.IsNotNull(physicalBindings);
            Assert.IsTrue(physicalBindings.Single().TargetDirectory.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(fixture.Target, physicalBindings.Single().ComparisonRoot);
            await fixture.CaptureAsync(fixture.Source);
            return (await fixture.History.WorkspaceStore.LoadAsync(token)).Value!;
        }
    }
}
