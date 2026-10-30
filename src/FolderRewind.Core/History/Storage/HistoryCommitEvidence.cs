using System;
using System.IO;
using System.Linq;

namespace FolderRewind.History.Storage;

internal enum HistoryCommitState { NotCommitted, Committed, Unknown }

/// <summary>Publication evidence, never inferred from the exception that interrupted a caller.</summary>
internal sealed record HistoryCommitEvidence(HistoryCommitState State, Exception? Error = null)
{
    internal static HistoryCommitEvidence Read(HistoryRepositoryPaths paths, byte[] intendedBytes)
    {
        try
        {
            var codec = new HistoryPackCodec();
            var intended = codec.Decode(intendedBytes).Pack;
            var path = paths.GetPackPath(intended.PackId);
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing/offline repository is not proof that publication never happened.
                var parent = Path.GetDirectoryName(path)!;
                var directories = Directory.GetDirectories(paths.PacksRoot);
                if (directories.Contains(parent, StringComparer.OrdinalIgnoreCase))
                {
                    if (Directory.GetFileSystemEntries(parent).Contains(path, StringComparer.OrdinalIgnoreCase))
                        return new(HistoryCommitState.Unknown, error);
                }
                return new(HistoryCommitState.NotCommitted);
            }
            _ = codec.Decode(bytes);
            return bytes.AsSpan().SequenceEqual(intendedBytes)
                ? new(HistoryCommitState.Committed)
                : new(HistoryCommitState.Unknown, new InvalidDataException("Published Pack differs from transaction intent."));
        }
        catch (Exception error)
        {
            return new(HistoryCommitState.Unknown, error);
        }
    }
}
