using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// Shared by the application and real-process integration tests.
internal class SevenZipArchiveProcessBackend(Func<string?> executablePath, Func<string?> credential,
    bool encrypted, string restoreMarkerDirectory) : IArchiveRepresentationBackend, IHistoryCompactionBackend
{
    public async ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
            return new(false, string.Empty, "Archive payload is missing.");
        var result = await RunAsync("t", localPath, outputDirectory: null, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
        {
            var listing = await RunAsync("l", localPath, outputDirectory: null, workingDirectory: null, cancellationToken).ConfigureAwait(false);
            if (!listing.Success || !SevenZipArchiveListingParser.TryParse(listing.Output, out _))
                return new(false, string.Empty, "Archive entries are unsafe or could not be validated.");
        }
        return result.Success
            ? new(true, $"7z-test:{new FileInfo(localPath).Length}", string.Empty)
            : new(false, string.Empty, result.Diagnostic);
    }

    public async ValueTask MaterializeAsync(
        IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        foreach (var input in dependencyFirstInputs)
        {
            var result = await RunAsync(
                "x",
                input.LocalPath,
                stagingDirectory,
                workingDirectory: null,
                cancellationToken).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidDataException(result.Diagnostic);
            ApplyDeletedFiles(input.Representation, stagingDirectory);
        }
        var marker = Path.Combine(stagingDirectory, restoreMarkerDirectory);
        if (Directory.Exists(marker)) Directory.Delete(marker, recursive: true);
    }

    public async Task<HistoryCompactionPayload> CreateFullAsync(
        SourceVersion version,
        string materializedDirectory,
        RepresentationId replacementRepresentationId,
        string durableOutputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(durableOutputDirectory);
        var path = Path.Combine(durableOutputDirectory, "payload.7z");
        var result = await RunAsync(
            "a",
            path,
            outputDirectory: null,
            workingDirectory: materializedDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException(result.Diagnostic);
        return new(
            "7z",
            path,
            new FileInfo(path).Length,
            null,
            version.StateFingerprint,
            ImmutableDictionary<string, string>.Empty);
    }

    public ValueTask<PayloadVerificationResult> DeepVerifyAsync(
        VersionRepresentation representation,
        string payloadPath,
        CancellationToken cancellationToken)
        => VerifyAsync(representation, payloadPath, cancellationToken);

    private async Task<(bool Success, string Diagnostic, string Output)> RunAsync(
        string operation,
        string archivePath,
        string? outputDirectory,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var executable = executablePath();
        if (string.IsNullOrWhiteSpace(executable))
            return (false, "7-Zip executable is unavailable.", "");
        var password = encrypted ? credential() : null;
        if (encrypted && string.IsNullOrEmpty(password))
            return (false, "Encrypted archive credential is unavailable.", "");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            CreateNoWindow = true,
            // CreateProcess rejects long current directories even when file APIs accept them.
            // Absolute input paths keep archive entries relative to the selected source root.
            WorkingDirectory = Path.GetPathRoot(Path.GetFullPath(executable))!
        };
        start.ArgumentList.Add(operation);
        if (operation == "l") { start.ArgumentList.Add("-slt"); start.ArgumentList.Add("-sccUTF-8"); }
        start.ArgumentList.Add(archivePath);
        if (operation == "x") start.ArgumentList.Add("-o" + outputDirectory);
        if (operation == "a") start.ArgumentList.Add(Path.Combine(Path.GetFullPath(workingDirectory!), "*"));
        start.ArgumentList.Add("-y");
        if (!string.IsNullOrEmpty(password)) start.ArgumentList.Add("-p" + password);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return (false, "7z process did not start.", "");
        process.StandardInput.Close(); // 缺少密码时失败关闭，不等待不可见的交互输入。
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? (true, string.Empty, output)
            : (false, $"7-Zip {operation} exited with code {process.ExitCode}: " +
                Redact(output + "\n" + error, password), "");
    }

    internal static string Redact(string text, string? password)
        => string.IsNullOrEmpty(password) ? text : text.Replace(password, "[redacted]", StringComparison.Ordinal);

    private static void ApplyDeletedFiles(VersionRepresentation representation, string stagingDirectory)
    {
        if (!representation.RepresentationSpecificMetadata.TryGetValue("deletedFiles", out var encoded)) return;
        var root = Path.GetFullPath(stagingDirectory);
        foreach (var relative in encoded.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = relative.Replace('\\', '/');
            if (!FolderRewind.History.Storage.HistoryRepositoryPaths.IsSafeRepositoryRelativePath(normalized))
                throw new InvalidDataException("Smart deletion path is unsafe.");
            var target = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var relation = Path.GetRelativePath(root, target);
            if (relation == ".." || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Smart deletion escapes the materialization root.");
            if (File.Exists(target)) File.Delete(target);
            else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
