using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolderRewind.History.Capture;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.History.Representation;

public sealed record IncrementalCaptureEvidence(int Schema, string ConfigId, Guid SourceId, string VersionId,
    string RepresentationId, string BoundaryFingerprint, string DetectionPolicy,
    IReadOnlyList<ArtifactFileEntry> Files, IReadOnlyDictionary<string, SourceCaptureFileState> FileStates,
    int CapturesSinceCheckpoint)
{
    public const string Path = "_host/capture-evidence.json";
    public string Fingerprint()
    {
        var text = new StringBuilder("folderrewind/incremental-files/1\0");
        foreach (var f in Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
            text.Append(f.RelativePath.Length).Append(':').Append(f.RelativePath).Append(':').Append(f.Length)
                .Append(':').Append(f.Sha256.ToUpperInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    public static async Task<IncrementalCaptureEvidence> ReadAsync(NativeArtifactReadLease read, ArtifactId root, CancellationToken token)
    {
        var node = read.Artifacts.Single(a => a.ArtifactId == root);
        var files = await read.Read.ListFilesAsync(node.Content, token);
        if (files.Single(f => f.RelativePath == Path).Length > 64L * 1024 * 1024) throw new InvalidDataException("Capture evidence quota exceeded.");
        await using var input = await read.Read.OpenReadAsync(node.Content, Path, token);
        var result = await JsonSerializer.DeserializeAsync<IncrementalCaptureEvidence>(input, cancellationToken: token)
            ?? throw new InvalidDataException("Missing capture evidence.");
        if (result.Schema != 1 || result.ConfigId != node.ConfigId || result.SourceId != node.FolderId
            || result.DetectionPolicy != "size-mtime-v1" || result.Files.Count > 100_000
            || result.Files.Select(f => ArtifactPathRules.NormalizeRelativePath(f.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Files.Count
            || result.Files.Any(f => f.Length < 0 || f.Sha256.Length != 64)
            || !result.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal).SetEquals(result.FileStates.Keys))
            throw new InvalidDataException("Invalid capture evidence.");
        return result;
    }

    public async Task ValidateViewAsync(IArtifactFileView view, IArtifactFileView? baseline,
        ArtifactIncrementalInput capture, CancellationToken token)
    {
        var expected = Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var actual = view.Files.ToDictionary(f => ArtifactPathRules.NormalizeRelativePath(f.RelativePath), StringComparer.Ordinal);
        if (actual.Count != expected.Count || !expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys))
            throw new InvalidDataException("Candidate file directory differs from the captured boundary.");
        var prior = baseline?.Files.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var changed = capture.AddedFiles.Concat(capture.ModifiedFiles).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, evidence) in expected)
        {
            token.ThrowIfCancellationRequested(); var file = actual[path];
            if (file.Length != evidence.Length || !StringComparer.OrdinalIgnoreCase.Equals(file.Sha256, evidence.Sha256))
                throw new InvalidDataException("Candidate byte evidence differs from capture: " + path);
            if (!capture.Checkpoint && !changed.Contains(path))
            {
                if (prior is null || !prior.TryGetValue(path, out var old) || old != file)
                    throw new InvalidDataException("Unchanged file did not inherit its immutable recipe: " + path);
                continue;
            }
            await using var stream = await view.OpenReadAsync(path, token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long length = 0; int count;
            while ((count = await stream.ReadAsync(buffer, token)) > 0)
            { length = checked(length + count); if (length > evidence.Length) throw new InvalidDataException("Materialized stream exceeded evidence."); hash.AppendData(buffer, 0, count); }
            if (length != evidence.Length || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), evidence.Sha256))
                throw new InvalidDataException("Materialized file differs from captured bytes: " + path);
        }
    }

    public async Task CopyViewToAsync(IArtifactFileView view, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var actual = view.Files.ToDictionary(f => ArtifactPathRules.NormalizeRelativePath(f.RelativePath), StringComparer.Ordinal);
        if (!Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal).SetEquals(actual.Keys))
            throw new InvalidDataException("Restore directory differs from committed evidence.");
        foreach (var file in Files)
        {
            var declared = actual[file.RelativePath];
            if (declared.Length != file.Length || !StringComparer.OrdinalIgnoreCase.Equals(declared.Sha256, file.Sha256))
                throw new InvalidDataException("Restore entry differs from committed evidence.");
            var path = ArtifactPathRules.ResolveUnderRoot(directory, file.RelativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await using var source = await view.OpenReadAsync(file.RelativePath, token);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long size = 0; int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    size = checked(size + count); if (size > file.Length) throw new InvalidDataException("Restore stream exceeds evidence.");
                    hash.AppendData(buffer, 0, count); await target.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (size != file.Length || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256))
                    throw new InvalidDataException("Restore content differs from committed evidence.");
            }
            File.SetLastWriteTimeUtc(path, FileStates[file.RelativePath].LastWriteTimeUtc);
        }
    }
}
