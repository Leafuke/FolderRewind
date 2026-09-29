using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed class HistoryAssessmentService
{
    private readonly HistoryQueryService _query;
    private readonly RepresentationRuntime _runtime;
    private readonly IRepresentationEnvironment _environment;

    public HistoryAssessmentService(
        HistoryQueryService query,
        RepresentationRuntime runtime,
        IRepresentationEnvironment environment)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public async Task<VersionAssessment> AssessVersionAsync(
        SourceVersion version,
        AssessmentDepth depth,
        MaterializationFidelity requiredFidelity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        var representations = await _query.GetAllRepresentationsAsync(cancellationToken).ConfigureAwait(false);
        return await _runtime.AssessVersionAsync(
            version.VersionId,
            representations,
            _environment,
            depth,
            requiredFidelity,
            cancellationToken).ConfigureAwait(false);
    }
}

