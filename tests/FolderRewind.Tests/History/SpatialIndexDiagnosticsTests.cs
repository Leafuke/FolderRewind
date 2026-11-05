using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class SpatialIndexDiagnosticsTests
{
    [TestMethod]
    public async Task CollectorKeepsReentrantReadsInOneOperationAndPreservesAtomicBatchVisibility()
    {
        var root = Path.Combine(Path.GetTempPath(), "fr-spatial-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new SpatialIndexDiagnostics();
        using var index = new SpatialWorkspaceIndex(Path.Combine(root, "index.db"), diagnostics);
        Task writer = Task.CompletedTask;
        Task<SpatialWorkspacePresentation>? reader = null;
        try
        {
            writer = Task.Run(() => index.Put(Rows()));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var readerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader = Task.Run(() => { readerStarted.SetResult(); return index.ReadPresentation(new("world", "layer"), 0, false); });
            await readerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(reader.IsCompleted, "A reader cannot observe an unfinished write batch.");
            release.SetResult();
            await writer.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await reader.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, result.Global.Total);
            Assert.AreEqual(1, result.Total);
            Assert.AreEqual(1L, result.IndexVersion);
            Assert.HasCount(2, result.Rows.Single().MemberIds);
            var samples = diagnostics.Operations;
            Assert.HasCount(2, samples, "Nested Query/Count must not create extra gate observations.");
            var write = samples.Single(sample => sample.Kind == "put");
            var read = samples.Single(sample => sample.Kind == "presentation");
            Assert.AreEqual(0L, write.BeginVersion); Assert.AreEqual(1L, write.EndVersion);
            Assert.AreEqual(1L, read.BeginVersion); Assert.AreEqual(1L, read.EndVersion);
            Assert.IsTrue(samples.All(sample => sample.WaitMilliseconds >= 0 && sample.HoldMilliseconds >= 0 && sample.Commands > 0));
            Assert.IsTrue(read.Phases.Any(phase => phase.Name == "presentation.rows" && phase.Commands > 0));
        }
        finally
        {
            release.TrySetResult();
            await writer;
            if (reader is not null) await reader;
            index.Dispose();
            Directory.Delete(root, recursive: true);
        }

        IEnumerable<SpatialWorkspaceRow> Rows()
        {
            yield return Row("blocks", "blocks");
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            yield return Row("entities", "entities");
        }
        static SpatialWorkspaceRow Row(string id, string component) => new(id, id, "", "Modified", false,
            [new SpatialSubjectLocation("world", "layer", new(0, 0, 16, 16))]) { Components = [component] };
    }
}
