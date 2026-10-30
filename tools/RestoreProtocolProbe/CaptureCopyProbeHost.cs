using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderRewind.History.Capture;

namespace FolderRewind.Tests;

// Explicit console probe, intentionally not a TestMethod: scheduling observations are not a test gate.
public static class CaptureCopyProbeHost
{
    private const int BlockSize = 65536;
    private const int FileBytes = 16 * 1024 * 1024;
    private static string Parent => Path.Combine(Path.GetTempPath(), "FolderRewindCaptureCopyProbe");

    public static async Task RunAsync(string[] args)
    {
        if (args is ["--capture-copy-writer", var writerRoot]) { await WriteAsync(writerRoot); return; }
        if (args is not ["--capture-copy", "--run"]) throw new ArgumentException("Enable explicitly with --capture-copy --run.");
        var results = new List<object>();
        foreach (var fileCount in new[] { 1, 2 })
        for (var attempt = 0; attempt < 2; attempt++) results.Add(await CaptureAsync(fileCount, attempt));
        Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<object> CaptureAsync(int fileCount, int attempt)
    {
        var root = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        Process? writer = null;
        try
        {
            var block = Enumerable.Repeat((byte)0x11, BlockSize).ToArray();
            for (var i = 0; i < fileCount; i++)
            {
                using var file = File.Create(Path.Combine(source, $"file-{i}.bin"));
                for (var offset = 0; offset < FileBytes; offset += block.Length) file.Write(block);
            }
            var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--capture-copy-writer"); start.ArgumentList.Add(root);
            writer = Process.Start(start)!;
            var stderr = writer.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (await writer.StandardOutput.ReadLineAsync(timeout.Token) != "ready") throw new IOException("Writer did not initialize.");
            File.WriteAllText(Path.Combine(root, "start"), "start");
            if (await writer.StandardOutput.ReadLineAsync(timeout.Token) != "writing") throw new IOException("Writer did not start.");
            var before = Statistics(source);
            using var input = new SourceCaptureInput(source, before, [], null, "probe");
            var stdout = writer.StandardOutput.ReadToEndAsync();
            var windowStart = Stopwatch.GetTimestamp();
            SourceCaptureReadLease? copy = null;
            string? error = null;
            try { copy = await input.PrepareAsync(input.Files.Keys, copy: true, protect: true, Path.Combine(root, "copies"), timeout.Token); }
            catch (Exception ex) when (ex is CaptureInputException or OperationCanceledException) { error = ex.GetType().Name + ":" + ex.Message; }
            var windowEnd = Stopwatch.GetTimestamp();
            var after = Statistics(source);
            File.WriteAllText(Path.Combine(root, "window.json"), JsonSerializer.Serialize(new[] { windowStart, windowEnd }));
            File.WriteAllText(Path.Combine(root, "stop"), "stop");
            await writer.WaitForExitAsync(timeout.Token);
            var output = new List<object>();
            if (copy is not null)
                foreach (var path in Directory.GetFiles(copy.SourcePath).Order(StringComparer.Ordinal))
                {
                    var bytes = await File.ReadAllBytesAsync(path, timeout.Token);
                    output.Add(new { path = Path.GetFileName(path), length = bytes.Length,
                        aBytes = bytes.Count(b => b == 0x11), bBytes = bytes.Count(b => b == 0xee), otherBytes = bytes.Count(b => b is not (0x11 or 0xee)),
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
                }
            return new { fileCount, attempt, error, accepted = copy is not null, stableView = copy?.IsStable,
                statisticsUnchanged = before.All(p => after[p.Key] == p.Value), before, after, output,
                writerExitCode = writer.ExitCode, writer = await stdout, stderr = await stderr };
        }
        finally
        {
            if (writer is not null) { if (!writer.HasExited) { writer.Kill(entireProcessTree: true); await writer.WaitForExitAsync(); } writer.Dispose(); }
            RequireProbeRoot(root);
            Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, SourceCaptureFileState> Statistics(string source) => Directory.GetFiles(source)
        .ToDictionary(p => Path.GetFileName(p), p => new SourceCaptureFileState(new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)), StringComparer.Ordinal);

    private static async Task WriteAsync(string root)
    {
        RequireProbeRoot(root);
        var files = Directory.GetFiles(Path.Combine(root, "source")).Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 2 || files.Any(p => new FileInfo(p).Length != FileBytes)) throw new IOException("Unexpected synthetic fixture.");
        var streams = new List<FileStream>();
        var timestamps = new List<long>();
        try
        {
            foreach (var path in files) streams.Add(new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1));
            Console.WriteLine("ready");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!File.Exists(Path.Combine(root, "start"))) await Task.Delay(1, timeout.Token);
            var buffer = new byte[BlockSize];
            for (var sweep = 0; sweep < 64 && !timeout.IsCancellationRequested && !File.Exists(Path.Combine(root, "stop")); sweep++)
            {
                Array.Fill(buffer, sweep % 2 == 0 ? (byte)0xee : (byte)0x11);
                foreach (var stream in streams)
                {
                    stream.Position = 0;
                    for (var offset = 0; offset < FileBytes; offset += buffer.Length)
                    {
                        if (timeout.IsCancellationRequested || File.Exists(Path.Combine(root, "stop"))) break;
                        stream.Write(buffer); timestamps.Add(Stopwatch.GetTimestamp());
                    }
                }
                if (sweep == 0) Console.WriteLine("writing");
            }
            var windowPath = Path.Combine(root, "window.json");
            while (!File.Exists(Path.Combine(root, "stop"))) await Task.Delay(1, timeout.Token);
            var window = JsonSerializer.Deserialize<long[]>(await File.ReadAllTextAsync(windowPath, timeout.Token))!;
            Console.WriteLine(JsonSerializer.Serialize(new { writes = timestamps.Count,
                writesDuringPreparation = timestamps.Count(t => t >= window[0] && t <= window[1]), blockBytes = BlockSize,
                firstWrite = timestamps.FirstOrDefault(), lastWrite = timestamps.LastOrDefault() }));
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }

    private static void RequireProbeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Parent))
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidOperationException("Only isolated probe roots are allowed.");
    }
}
