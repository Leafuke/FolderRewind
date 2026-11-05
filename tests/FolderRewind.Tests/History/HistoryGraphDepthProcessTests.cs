using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FolderRewind.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HistoryGraphDepthProcessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(5000, "representations")]
    [DataRow(10000, "representations")]
    [DataRow(5000, "partial")]
    [DataRow(10000, "partial")]
    public async Task ProductionCoreValidatesAdmitsAndQueriesDeepHistoryWithoutRecursiveStack(int depth, string scenario)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "HistoryGraphProbe", "HistoryGraphProbe.dll");
        Assert.IsTrue(File.Exists(probe), "Build must stage the production HistoryGraphProbe and its runtime dependencies: " + probe);
        var root = Path.Combine(Path.GetTempPath(), "fr-history-graph-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[] { probe, depth.ToString(CultureInfo.InvariantCulture), scenario, root }) start.ArgumentList.Add(value);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start history graph probe.");
        var output = ReadBoundedAsync(child.StandardOutput);
        var errors = ReadBoundedAsync(child.StandardError);
        var exited = false;
        try
        {
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
            catch (TimeoutException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Fail($"History graph probe timed out: depth={depth}, scenario={scenario}");
            }
            var stdout = await output.WaitAsync(TimeSpan.FromSeconds(10));
            var stderr = await errors.WaitAsync(TimeSpan.FromSeconds(10));
            TestContext.WriteLine(stdout);
            Assert.AreEqual(0, child.ExitCode, $"depth={depth}, scenario={scenario}; stdout={stdout}; stderr={stderr}");
            foreach (var stage in new[] { "VALIDATED", "ADMITTED", "QUERIED", "PAGED" })
                StringAssert.Contains(stdout, stage + ": depth=" + depth);
            var json = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            using var result = JsonDocument.Parse(json);
            Assert.AreEqual(depth, result.RootElement.GetProperty("depth").GetInt32());
            Assert.AreEqual(scenario, result.RootElement.GetProperty("scenario").GetString());
            Assert.AreEqual("Ready", result.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("Ready", result.RootElement.GetProperty("readiness").GetString());
            Assert.AreEqual(scenario == "partial" ? 1L : depth, result.RootElement.GetProperty("dependencyBytes").GetInt64());
            Assert.AreEqual(1, result.RootElement.GetProperty("rows").GetInt32());
            Assert.AreEqual("FolderRewind.Core", result.RootElement.GetProperty("coreAssembly").GetString());
            Assert.AreEqual("HistoryGraphProbe", result.RootElement.GetProperty("probeAssembly").GetString());
        }
        finally
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (child.HasExited) { }
            }
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); exited = true; }
            catch (TimeoutException) { TestContext.WriteLine("Retaining fixture because probe exit was not confirmed: " + root); }
            if (exited)
            {
                await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(10));
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        // Keep draining a crashing child's repeated stack trace, without retaining an
        // unbounded log or allowing a full stderr pipe to deadlock process termination.
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
}
