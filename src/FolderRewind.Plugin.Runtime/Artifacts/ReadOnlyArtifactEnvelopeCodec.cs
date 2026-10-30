namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Operation-local credentials may decode candidates but can never produce persisted ciphertext.</summary>
public sealed class ReadOnlyArtifactEnvelopeCodec(IBoundedArtifactEnvelopeCodec decoder)
    : IBoundedArtifactEnvelopeCodec, IConcurrentArtifactEnvelopeCodec
{
    public int MaximumConcurrency => decoder is IConcurrentArtifactEnvelopeCodec concurrent ? concurrent.MaximumConcurrency : 1;
    public Task PackAsync(string source, string payload, bool encrypted, CancellationToken token)
        => throw new InvalidOperationException("Read-only Artifact verification cannot create envelopes.");
    public Task UnpackAsync(string payload, string target, bool encrypted, CancellationToken token)
        => throw new InvalidOperationException("Read-only Artifact verification requires an explicit decoded budget.");
    public Task UnpackAsync(string payload, string target, bool encrypted, long maximumBytes, ArtifactExtractionBudget budget, CancellationToken token)
        => decoder.UnpackAsync(payload, target, encrypted, maximumBytes, budget, token);
}
