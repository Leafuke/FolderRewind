using FolderRewind.History.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Representation;

public sealed record PayloadVerificationResult(
    bool Success,
    string Evidence,
    string Diagnostic);

public sealed record ArchiveMaterializationInput(
    VersionRepresentation Representation,
    string LocalPath);

public interface IArchiveRepresentationBackend
{
    ValueTask<PayloadVerificationResult> VerifyAsync(
        VersionRepresentation representation,
        string localPath,
        CancellationToken cancellationToken);

    ValueTask MaterializeAsync(
        IReadOnlyList<ArchiveMaterializationInput> dependencyFirstInputs,
        string stagingDirectory,
        CancellationToken cancellationToken);
}

