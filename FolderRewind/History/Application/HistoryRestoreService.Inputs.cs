using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Application;

public sealed partial class HistoryRestoreService
{
    internal ArtifactSpaceBudget SpaceBudget { get; init; } = ArtifactSpaceBudget.Shared;
    internal IDisposable? EnterInputScope()
    {
        if (VerificationContext is not null) return null;
        var context = new ArchiveRestoreVerificationContext(Models.RestoreArchivePreflightMode.Strict);
        VerificationContext = context;
        return new InputScope(this, context);
    }
    private sealed class InputScope(HistoryRestoreService restore, ArchiveRestoreVerificationContext context) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(restore.VerificationContext, context)) restore.VerificationContext = null;
            context.Dispose();
        }
    }

    internal async Task ReservePreparationSpaceAsync(IEnumerable<(SourceVersion Version, HistoryRestoreSourceBinding Binding, MaterializationFidelity Fidelity)> selections,
        HistoryRestorePreparationSession? session, CancellationToken token)
    {
        if (session is null || VerificationContext is not { } context) return;
        var graph = await _history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = await _environmentFactory(token).ConfigureAwait(false);
        foreach (var selection in selections)
        {
            var assessment = await _representations.AssessVersionAsync(selection.Version.VersionId, graph, environment,
                AssessmentDepth.Deep, selection.Fidelity, token, context).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
                throw new IOException(HistoryQuickRestoreResolver.AssessmentDiagnostic(assessment, "Restore input is unavailable."));
            var staging = Path.Combine(session.Journal.Workspaces.Single(w => w.Location.SourceId == selection.Binding.SourceId).Location.Container, "staging");
            context.ReserveMaterializationSpace(assessment.Selected.RepresentationId, graph, staging, SpaceBudget);
        }
    }
}
