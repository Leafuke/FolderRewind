namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>One cumulative byte budget for a closure transfer, including readback of newly uploaded objects.</summary>
public sealed class ArtifactTransferBudget(long maximumBytes)
{
    private long _bytes;
    private readonly long _maximumBytes = maximumBytes is >= 0 and <= 16L * 1024 * 1024 * 1024
        ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    public long BytesRead => Interlocked.Read(ref _bytes);

    public async Task CopyAsync(Stream input, Stream output, long expectedLength, CancellationToken token)
    {
        if (expectedLength < 0 || expectedLength > _maximumBytes) throw new InvalidDataException("Object exceeds its transfer budget.");
        var buffer = new byte[65536]; long length = 0; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            length = checked(length + count);
            if (length > expectedLength) throw new InvalidDataException("Object exceeds its expected length.");
            if (Interlocked.Add(ref _bytes, count) > _maximumBytes) throw new InvalidDataException("Closure transfer budget exceeded.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (length != expectedLength) throw new InvalidDataException("Object ended before its expected length.");
    }
}

public static class NativeArtifactClosureLimits
{
    public static ArtifactClosurePlan Validate(ArtifactLedgerDocument graph, FolderRewind.Plugin.Abstractions.ArtifactId root)
    {
        var closure = ArtifactLedgerValidator.BuildClosure(graph, [root]);
        long logical = 0, storage = 0;
        foreach (var node in graph.Artifacts.Where(n => closure.Reachable.Contains(n.ArtifactId)))
        {
            if (node.LogicalSize > ArtifactTransformLimits.Default.MaximumBytes - logical
                || node.StorageSize > ArtifactTransformLimits.Default.MaximumBytes - storage)
                throw new InvalidDataException("Native closure exceeds its logical or storage byte budget.");
            logical += node.LogicalSize; storage += node.StorageSize;
        }
        return closure;
    }
}
