using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Retention;
using FolderRewind.History.Migration;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

// Shared by the application and real-process integration tests.
internal class SevenZipArchiveProcessBackend(Func<string?> executablePath, Func<string?> credential,
    bool encrypted, string restoreMarkerDirectory) : IArchiveRepresentationBackend, IHistoryChainRewriteArchiveBackend, IArchiveRestorePreflightBackend,
    FolderRewind.Plugin.Runtime.Artifacts.IConcurrentArtifactEnvelopeCodec, FolderRewind.Plugin.Runtime.Artifacts.IBoundedArtifactEnvelopeCodec
{
    private readonly Dictionary<string, ArchiveSafeListing> _safeListings = new(StringComparer.OrdinalIgnoreCase);
    public bool TryGetListing(string path, out ArchiveSafeListing? listing) => _safeListings.TryGetValue(Path.GetFullPath(path), out listing);
    // 密码只在操作内存中参与输入绑定，不写入证据、日志或元数据。
    public object InputPolicyIdentity => (encrypted, executablePath(), encrypted ? credential() : null);
    public async ValueTask<PayloadVerificationResult> PreflightAsync(VersionRepresentation representation, string path,
        bool fullContentTest, CancellationToken token)
    {
        if (fullContentTest) return await VerifyAsync(representation, path, token).ConfigureAwait(false);
        if (representation.RepresentationSpecificMetadata.ContainsKey("legacyFileName"))
            return await VerifyAsync(representation, path, token).ConfigureAwait(false);
        var listing = await RunAsync("l", path, null, null, token).ConfigureAwait(false);
        if (!listing.Success) return new(false, string.Empty, listing.Diagnostic);
        if (!SevenZipArchiveListingParser.TryParse(listing.Output, out var files, out var directories))
            return new(false, string.Empty, "Archive entries are unsafe or could not be validated.");
        _safeListings[Path.GetFullPath(path)] = new(files, directories);
        return new(true, "safe-listing; full content not tested", string.Empty);
    }
    // Bound independently of world size. Envelope jobs operate on disjoint immutable packs.
    public int MaximumConcurrency => Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));
    public Task PackAsync(string source, string payload, bool requiresEncryption, CancellationToken token)
    {
        if (requiresEncryption != encrypted) throw new InvalidOperationException("Artifact encryption policy mismatch.");
        return PackLogicalTreeAsync(source, payload, token);
    }

    public Task UnpackAsync(string payload, string target, bool requiresEncryption, CancellationToken token)
    {
        if (requiresEncryption != encrypted) return new SevenZipArchiveProcessBackend(executablePath, credential, requiresEncryption,
            restoreMarkerDirectory).UnpackLogicalTreeAsync(payload, target, token);
        return UnpackLogicalTreeAsync(payload, target, token);
    }
    public async Task PackLogicalTreeAsync(string source, string payload, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(payload))!);
        if (File.Exists(payload)) throw new IOException("Envelope output already exists.");
        var result = await RunAsync("a", payload, null, source, token, protectHeaders: true).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException(result.Diagnostic);
    }
    public Task UnpackAsync(string payload, string target, bool requiresEncryption, long maximumBytes,
        FolderRewind.Plugin.Runtime.Artifacts.ArtifactExtractionBudget budget, CancellationToken token)
    {
        if (requiresEncryption != encrypted) return new SevenZipArchiveProcessBackend(executablePath, credential, requiresEncryption,
            restoreMarkerDirectory).UnpackLogicalTreeAsync(payload, target, token, maximumBytes, budget);
        return UnpackLogicalTreeAsync(payload, target, token, maximumBytes, budget);
    }
    public async Task UnpackLogicalTreeAsync(string payload, string target, CancellationToken token,
        long maximumBytes = 16L * 1024 * 1024 * 1024,
        FolderRewind.Plugin.Runtime.Artifacts.ArtifactExtractionBudget? budget = null)
    {
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("Envelope workspace must be empty.");
        var list = await RunAsync("l", payload, null, null, token).ConfigureAwait(false);
        // Extraction validates CRC/decryption too; avoid a redundant complete test pass.
        if (!list.Success || !SevenZipArchiveListingParser.TryParse(list.Output, out var entries, out var directories)
            || entries.Count + directories.Count > FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumFiles
            || entries.Values.Sum(e => e.Size) > Math.Min(maximumBytes, FolderRewind.Plugin.Runtime.Artifacts.ArtifactTransformLimits.Default.MaximumBytes))
            throw new InvalidDataException("Invalid or inaccessible Artifact envelope.");
        var bytes = entries.Values.Sum(e => e.Size);
        (budget ?? new()).Reserve(bytes, entries.Count + directories.Count);
        using var space = FolderRewind.Plugin.Runtime.Artifacts.ArtifactSpaceBudget.Shared.Reserve(new FolderRewind.Plugin.Runtime.Artifacts.ArtifactSpaceRequest(target, checked(bytes + directories.Count * 4096L)));
        FolderRewind.Plugin.Runtime.Artifacts.HostArtifactWorkspace.CreatePrivateDirectory(target);
        var result = await RunAsync("x", payload, target, null, token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidDataException(result.Diagnostic);
    }
    public async ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken)
    {
        if (representation.RepresentationSpecificMetadata.ContainsKey("legacyFileName")
            && (!representation.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.ContractKey, out var contract) || contract != "1"))
            return new(false, string.Empty, "Unsupported development-era legacy representation. Reimport preserved 1.8.2 data into a separate repository.");
        if (!File.Exists(localPath))
            return new(false, string.Empty, "Archive payload is missing.");
        var result = await RunAsync("t", localPath, outputDirectory: null, workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
        {
            var listing = await RunAsync("l", localPath, outputDirectory: null, workingDirectory: null, cancellationToken).ConfigureAwait(false);
            if (!listing.Success || !SevenZipArchiveListingParser.TryParse(listing.Output, out var entries, out var directories))
                return new(false, string.Empty, "Archive entries are unsafe or could not be validated.");
            if (!representation.RepresentationSpecificMetadata.ContainsKey(LegacySmartPlan.OwnersKey)
                && representation.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.FilesKey, out var manifest)
                && !LegacySmartPlan.Paths(JsonSerializer.Deserialize<string[]>(manifest)).SetEquals(entries.Keys))
                return new(false, string.Empty, "Legacy archive disagrees with its historical manifest.");
            _safeListings[Path.GetFullPath(localPath)] = new(entries, directories);
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
        if (dependencyFirstInputs.Count > 0
            && dependencyFirstInputs[^1].Representation.RepresentationSpecificMetadata.ContainsKey(LegacySmartPlan.ContractKey))
        {
            await MaterializeLegacyAsync(dependencyFirstInputs, stagingDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }
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

    public async ValueTask MaterializeRestoreAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging,
        ArchiveRestoreVerificationContext context, CancellationToken token)
    {
        var listings = inputs.Select(context.RequireListing).ToArray();
        var plan = NativeSmartOwnerPlanner.TryPlan(inputs, listings, restoreMarkerDirectory, out var fallback);
        long estimated = plan is null ? listings.Sum(listing => listing.Files.Values.Sum(file => file.Size)) : plan.Files.Values.Sum(file => file.File.Size);
        var volume = FolderRewind.History.Storage.WindowsRestoreFileSystem.RequireLocalNtfs(Path.GetDirectoryName(Path.GetFullPath(staging))!);
        if ((ulong)estimated > volume.FreeBytes || volume.FreeBytes - (ulong)estimated < (ulong)Math.Max(8 * 1024 * 1024, estimated / 20))
            throw new IOException("Insufficient free space on the restore staging volume.");
        HistoryOperationDiagnostics.Current?.Observe("workspace-volume", volume.Identity.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (plan is null)
        {
            HistoryOperationDiagnostics.Current?.Observe("smart-fallback", fallback);
            await MaterializeAsync(inputs, staging, token).ConfigureAwait(false);
            HistoryOperationDiagnostics.Current?.Count("uncompressed-bytes-written-estimate", estimated);
            return;
        }
        Directory.CreateDirectory(staging);
        if (Directory.EnumerateFileSystemEntries(staging).Any()) throw new InvalidDataException("Selective materialization requires empty staging.");
        using var timing = HistoryOperationDiagnostics.Current?.Measure("smart-selective-materialize");
        foreach (var group in plan.Files.Values.GroupBy(file => file.ArchiveIndex).OrderBy(group => group.Key))
        {
            var selected = group.Select(file => file.File.RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var list = Path.Combine(Path.GetDirectoryName(staging)!, "smart-files-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                await File.WriteAllLinesAsync(list, selected, new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
                var result = await RunAsync("x", inputs[group.Key].LocalPath, staging, null, token, list).ConfigureAwait(false);
                if (!result.Success) throw new InvalidDataException(result.Diagnostic);
                HistoryOperationDiagnostics.Current?.Count("uncompressed-bytes-written-estimate", group.Sum(file => file.File.Size));
            }
            finally { File.Delete(list); }
        }
        var actual = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(staging);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Unexpected link in Smart staging.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (!actual.TryAdd(Path.GetRelativePath(staging, entry).Replace('\\', '/'), new FileInfo(entry).Length))
                    throw new InvalidDataException("Smart staging contains a case collision.");
            }
        }
        if (actual.Count != plan.Files.Count || plan.Files.Any(pair => !actual.TryGetValue(pair.Key, out var length) || length != pair.Value.File.Size))
            throw new InvalidDataException($"Smart staging disagrees with the final owner plan: expected {plan.Files.Count}, actual {actual.Count}; missing "
                + string.Join(", ", plan.Files.Keys.Where(path => !actual.ContainsKey(path)).Take(3)));
        HistoryOperationDiagnostics.Current?.Count("smart-owner-archives", inputs.Count);
    }

    private async Task MaterializeLegacyAsync(IReadOnlyList<ArchiveMaterializationInput> inputs, string staging, CancellationToken token)
    {
        if (Directory.EnumerateFileSystemEntries(staging).Any())
            throw new InvalidDataException("Legacy materialization requires an empty staging directory.");
        var root = inputs[^1].Representation;
        if (root.RepresentationSpecificMetadata.ContainsKey("legacy182Support"))
            throw new InvalidDataException("A legacy dependency payload cannot be restored independently.");
        Dictionary<string, string>? owners = null;
        if (root.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.OwnersKey, out var encoded))
        {
            owners = JsonSerializer.Deserialize<Dictionary<string, string>>(encoded)
                ?? throw new InvalidDataException("Legacy ownership manifest is absent.");
            _ = LegacySmartPlan.Paths(owners.Keys);
            if (owners.Values.Any(name => !LegacySmartPlan.SafeArchive(name)))
                throw new InvalidDataException("Unsafe legacy owner archive.");
        }
        var names = inputs.Select(i => i.Representation.RepresentationSpecificMetadata["legacyFileName"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (owners is not null && owners.Values.Any(name => !names.Contains(name)))
            throw new InvalidDataException("Legacy ownership plan references an absent dependency.");
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var verification = await VerifyAsync(input.Representation, input.LocalPath, token).ConfigureAwait(false);
            if (!verification.Success) throw new InvalidDataException(verification.Diagnostic);
            string? list = null;
            try
            {
                if (owners is not null)
                {
                    var name = input.Representation.RepresentationSpecificMetadata["legacyFileName"];
                    var selected = owners.Where(p => p.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
                    if (selected.Length == 0) continue;
                    var listing = await RunAsync("l", input.LocalPath, null, null, token).ConfigureAwait(false);
                    if (!listing.Success || !SevenZipArchiveListingParser.TryParse(listing.Output, out var actual)
                        || selected.Any(file => !actual.ContainsKey(file)))
                        throw new InvalidDataException("Legacy owner archive is missing a required file.");
                    list = Path.Combine(Path.GetDirectoryName(staging)!, "legacy-files-" + Guid.NewGuid().ToString("N") + ".txt");
                    await File.WriteAllLinesAsync(list, selected, new System.Text.UTF8Encoding(false), token).ConfigureAwait(false);
                }
                var result = await RunAsync("x", input.LocalPath, staging, null, token, list).ConfigureAwait(false);
                if (!result.Success) throw new InvalidDataException(result.Diagnostic);
            }
            finally { if (list is not null) File.Delete(list); }
        }
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(staging, p).Replace('\\', '/')).ToArray();
        var expected = owners?.Keys.AsEnumerable();
        if (expected is null && root.RepresentationSpecificMetadata.TryGetValue(LegacySmartPlan.FilesKey, out var manifest))
            expected = JsonSerializer.Deserialize<string[]>(manifest);
        if (expected is not null && !LegacySmartPlan.Paths(expected).SetEquals(files))
            throw new InvalidDataException("Materialized legacy files disagree with the target manifest.");
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
        // A marker makes an empty state a valid archive and is removed on materialization.
        var emptyMarker = !Directory.EnumerateFileSystemEntries(materializedDirectory).Any()
            ? Path.Combine(materializedDirectory, restoreMarkerDirectory, "empty") : null;
        if (emptyMarker is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(emptyMarker)!);
            await File.WriteAllTextAsync(emptyMarker, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var result = await RunAsync("a", path, outputDirectory: null,
                workingDirectory: materializedDirectory, cancellationToken).ConfigureAwait(false);
            if (!result.Success) throw new InvalidDataException(result.Diagnostic);
            return new("7z", path, new FileInfo(path).Length, null, version.StateFingerprint,
                ImmutableDictionary<string, string>.Empty);
        }
        finally
        {
            if (emptyMarker is not null && Directory.Exists(Path.GetDirectoryName(emptyMarker)))
                Directory.Delete(Path.GetDirectoryName(emptyMarker)!, recursive: true);
        }
    }

    public ValueTask<PayloadVerificationResult> DeepVerifyAsync(
        VersionRepresentation representation,
        string payloadPath,
        CancellationToken cancellationToken)
        => VerifyAsync(representation, payloadPath, cancellationToken);

    public async Task<HistoryCompactionPayload> CreateDeltaAsync(SourceVersion version,
        string targetDirectory, ImmutableArray<string> changedFiles, RepresentationId replacementId,
        string durableOutputDirectory, CancellationToken token)
    {
        var selected = Path.Combine(durableOutputDirectory, "delta-input");
        Directory.CreateDirectory(selected);
        try
        {
            foreach (var relative in changedFiles)
            {
                token.ThrowIfCancellationRequested();
                if (!FolderRewind.History.Storage.HistoryRepositoryPaths.IsSafeRepositoryRelativePath(relative))
                    throw new InvalidDataException("Unsafe delta path.");
                var destination = Path.Combine(selected, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar)), destination);
            }
            return await CreateFullAsync(version, selected, replacementId, durableOutputDirectory, token).ConfigureAwait(false);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(selected, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(selected, recursive: true);
        }
    }

    private async Task<(bool Success, string Diagnostic, string Output)> RunAsync(
        string operation,
        string archivePath,
        string? outputDirectory,
        string? workingDirectory,
        CancellationToken cancellationToken, string? includeList = null, bool protectHeaders = false)
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
        if (includeList is not null)
        {
            start.ArgumentList.Add("-i@" + includeList);
            start.ArgumentList.Add("-scsUTF-8");
            start.ArgumentList.Add("-spd");
            start.ArgumentList.Add("-r-");
        }
        if (operation == "a") start.ArgumentList.Add(Path.Combine(Path.GetFullPath(workingDirectory!), "*"));
        if (operation == "a" && encrypted && protectHeaders) start.ArgumentList.Add("-mhe=on");
        start.ArgumentList.Add("-y");
        if (!string.IsNullOrEmpty(password)) start.ArgumentList.Add("-p" + password);
        using var process = new Process { StartInfo = start };
        using var phase = HistoryOperationDiagnostics.Current?.Measure("archive-" + operation);
        if (!process.Start()) return (false, "7z process did not start.", "");
        var processHandle = process.Handle; // 保持内核进程对象至统计完成，短命子进程退出后不再查询 Process 属性。
        HistoryOperationDiagnostics.Current?.Count("7z-" + operation);
        if (operation == "t") HistoryOperationDiagnostics.Current?.Count("archive-bytes-tested", new FileInfo(archivePath).Length);
        using var observation = FolderRewind.Plugin.Runtime.Artifacts.ArtifactDiagnostics.ObserveProcess(process, operation);
        process.StandardInput.Close(); // 缺少密码时失败关闭，不等待不可见的交互输入。
        var outputTask = ReadBoundedOutputAsync(process.StandardOutput, process, 64 * 1024 * 1024, cancellationToken);
        var errorTask = ReadBoundedOutputAsync(process.StandardError, process, 1024 * 1024, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            HistoryOperationDiagnostics.ObserveNativeProcess(processHandle);
            return process.ExitCode == 0
                ? (true, string.Empty, output)
                : (false, $"7-Zip {operation} exited with code {process.ExitCode}: " + Redact(output + "\n" + error, password), "");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, Process process, int maximumCharacters, CancellationToken token)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > maximumCharacters)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new InvalidDataException("Archive process output quota exceeded.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
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
