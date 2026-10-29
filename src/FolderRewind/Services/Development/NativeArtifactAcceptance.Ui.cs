#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    internal static async Task PauseUiFixtureAsync(string point, CancellationToken token)
    {
        var data = Environment.GetEnvironmentVariable("FOLDERREWIND_TEST_DATA_ROOT");
        var root = data is null ? null : Directory.GetParent(Path.GetFullPath(data))?.FullName;
        if (root is null || !File.Exists(Path.Combine(root, "acceptance-owner"))) return;
        var hold = Path.Combine(root, "hold-" + point);
        if (!File.Exists(hold)) return;
        await File.WriteAllTextAsync(Path.Combine(root, point + "-entered"), "ready", token);
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (File.Exists(hold) && start.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50, token);
        token.ThrowIfCancellationRequested();
    }
}
#endif
