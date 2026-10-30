namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Reserves decoded bytes and directory entries for one closure before extraction.</summary>
public sealed class ArtifactExtractionBudget(long maximumBytes = 16L * 1024 * 1024 * 1024, int maximumEntries = 100_000)
{
    private readonly long _maximumBytes = maximumBytes is >= 0 and <= 16L * 1024 * 1024 * 1024 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private readonly int _maximumEntries = maximumEntries is >= 0 and <= 100_000 ? maximumEntries : throw new ArgumentOutOfRangeException(nameof(maximumEntries));
    private readonly object _sync = new();
    private long _bytes;
    private int _entries;
    public static int CountEntries(IEnumerable<string> files)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = ArtifactPathRules.NormalizeRelativePath(file); entries.Add(path);
            while (path.LastIndexOf('/') is var separator && separator >= 0)
            { path = path[..separator]; entries.Add(path); }
        }
        return entries.Count;
    }
    public void Reserve(long bytes, int entries)
    {
        lock (_sync)
        {
            if (bytes < 0 || entries < 0 || bytes > _maximumBytes - _bytes || entries > _maximumEntries - _entries)
                throw new InvalidDataException("Artifact decoded closure budget exceeded.");
            _bytes += bytes; _entries += entries;
        }
    }
}

public interface IBoundedArtifactEnvelopeCodec : IArtifactEnvelopeCodec
{
    Task UnpackAsync(string payload, string target, bool encrypted, long maximumBytes, ArtifactExtractionBudget budget, CancellationToken token);
}
