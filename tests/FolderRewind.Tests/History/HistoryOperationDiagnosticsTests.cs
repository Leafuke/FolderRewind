using FolderRewind.Services;

namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryOperationDiagnosticsTests
{
    [TestMethod]
    public async Task ConcurrentOperationsDoNotMixCountersAndExceptionStillPublishes()
    {
        var names = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
        await Task.WhenAll(names.Select(name => Task.Run(async () =>
        {
            try
            {
                using var scope = HistoryOperationDiagnostics.Begin(name);
                using var phase = scope.Measure("failed-phase");
                scope.Count("7z-t", name == names[0] ? 2 : 7);
                await Task.Yield();
                throw new IOException("injected");
            }
            catch (IOException) { }
        })));
        var results = HistoryOperationDiagnostics.GetRecent().Where(r => names.Contains(r.Operation)).ToArray();
        Assert.HasCount(2, results);
        foreach (var r in results)
        {
            Assert.AreEqual(r.Operation == names[0] ? 2L : 7L, r.Counters["7z-t"]);
            Assert.IsTrue(r.InclusivePhases.ContainsKey("failed-phase"));
        }
        Assert.IsNull(HistoryOperationDiagnostics.Current);
    }
}
