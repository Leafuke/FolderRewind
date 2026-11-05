using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialReadCancellationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SupersededReadCancelsBeforeAWriterReleasesTheMutationGate(bool overlay)
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new SpatialIndexDiagnostics();
        using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"), diagnostics);
        using var cancellation = new CancellationTokenSource();
        var writer = Task.Run(() => index.Put(Rows()));
        Task? reader = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var started = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            reader = Task.Run(() =>
            {
                started.SetResult(Thread.CurrentThread);
                if (overlay) _ = index.Overlays(new("world", "layer"), new(0, 0, 32, 32), 1, 16,
                    out _, cancellation.Token);
                else _ = index.ReadPresentation(new("world", "layer"), 0, false, cancellation.Token);
            });
            var readerThread = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waitTimer = System.Diagnostics.Stopwatch.StartNew();
            var blockedState = System.Threading.ThreadState.WaitSleepJoin;
            var observedBlockedWait = false;
            while (waitTimer.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (diagnostics.PeakOperations == 2 && (readerThread.ThreadState & blockedState) != 0)
                {
                    observedBlockedWait = true;
                    break;
                }
                await Task.Delay(1);
            }
            Assert.AreEqual(2, diagnostics.PeakOperations, "The reader must enter the measured gate wait before cancellation.");
            Assert.IsTrue(observedBlockedWait,
                "The reader must have blocked in the mutation-gate wait before cancellation.");
            Assert.IsFalse(writer.IsCompleted, "The writer must still own the mutation gate.");
            Assert.IsFalse(reader.IsCompleted, "The reader must still be waiting for the mutation gate.");
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await reader.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(writer.IsCompleted, "Cancellation must not require the blocked writer to finish.");
            var sample = diagnostics.Operations.Single(operation => operation.Kind == (overlay ? "overlay" : "presentation"));
            Assert.IsTrue(sample.Canceled);
            Assert.AreEqual(0L, sample.AcquiredTimestamp, "The canceled reader must never acquire the mutation gate.");
            Assert.AreEqual(0, sample.Commands);
            Assert.AreEqual(0d, sample.HoldMilliseconds);
            release.SetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
            // A canceled read must not leave a progress handler that aborts a later write.
            index.Put([Row("after")]);
            Assert.AreEqual(3, index.ReadPresentation(new("world", "layer"), 0, false).Global.Total);
            Assert.HasCount(1, index.Overlays(new("world", "layer"), new(0, 0, 32, 32), 1, 16));
        }
        finally
        {
            release.TrySetResult();
            await writer;
            if (reader is not null)
                try { await reader; } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            index.Dispose();
            Directory.Delete(root, recursive: true);
        }

        IEnumerable<SpatialWorkspaceRow> Rows()
        {
            yield return Row("first");
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            yield return Row("second");
        }
        static SpatialWorkspaceRow Row(string id) => new(id, id, "", "Modified", true,
            [new SpatialSubjectLocation("world", "layer", new(0, 0, 16, 16))]);
    }
}
