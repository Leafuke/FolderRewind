namespace FolderRewind.Plugin.Runtime.Artifacts;

/// <summary>Only domain conversion and candidate-content failures may start a fresh Core capture.</summary>
public sealed class ArtifactCaptureTransformException(string message, Exception? inner = null) : Exception(message, inner)
{
    public static bool IsDomainFailure(Exception error)
        => error is not (OperationCanceledException or IOException or UnauthorizedAccessException or OutOfMemoryException)
            && (error is not AggregateException aggregate || aggregate.InnerExceptions.All(IsDomainFailure))
            && (error.InnerException is null || IsDomainFailure(error.InnerException));
}
