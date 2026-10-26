using FolderRewind.History.Domain;
using FolderRewind.History.Representation;
using FolderRewind.History.Storage;
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
    private PreparationInputs? _preparationInputs;
    internal Func<IReadOnlyList<VersionRepresentation>, CancellationToken, Task<IRepresentationEnvironment>>? PreparationEnvironmentFactory { get; init; }
    private sealed class PreparationInputs(Services.NativeHistoryConfigurationOperationGate.Lease operation)
    {
        internal Services.NativeHistoryConfigurationOperationGate.Lease Operation { get; } = operation;
        internal (IReadOnlyList<VersionRepresentation> Graph, IRepresentationEnvironment Environment)? Value;
    }
    internal IDisposable? EnterPreparationInputs(Services.NativeHistoryConfigurationOperationGate.Lease operation)
    {
        operation.Require(_history.ConfigId);
        if (ReferenceEquals(_preparationInputs?.Operation, operation)) return null;
        var previous = _preparationInputs;
        var current = _preparationInputs = new(operation);
        return new PreparationInputScope(this, current, previous);
    }
    private sealed class PreparationInputScope(HistoryRestoreService restore, PreparationInputs current, PreparationInputs? previous) : IDisposable
    {
        public void Dispose()
        { current.Value = null; if (ReferenceEquals(restore._preparationInputs, current)) restore._preparationInputs = previous; }
    }
    internal void InvalidatePreparationInputs() { if (_preparationInputs is { } inputs) inputs.Value = null; }
    private async Task<(IReadOnlyList<VersionRepresentation> Graph, IRepresentationEnvironment Environment)> GetPreparationInputsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var inputs = _preparationInputs;
        inputs?.Operation.Require(_history.ConfigId);
        if (inputs?.Value is { } cached)
        { Services.HistoryOperationDiagnostics.Current?.Count("restore-input-snapshot-reuses"); return cached; }
        using var timing = Services.HistoryOperationDiagnostics.Current?.Measure("restore-input-snapshot-load");
        var graph = await _history.Query.GetAllRepresentationsAsync(token).ConfigureAwait(false);
        var environment = await (PreparationEnvironmentFactory is { } factory ? factory(graph, token) : _environmentFactory(token)).ConfigureAwait(false);
        Services.HistoryOperationDiagnostics.Current?.Count("restore-input-snapshot-loads");
        if (inputs is not null) inputs.Value = (graph, environment);
        return (graph, environment);
    }
    internal ArtifactSpaceBudget SpaceBudget { get; init; } = ArtifactSpaceBudget.Shared;
    internal IReadOnlyDictionary<SourceId, HistoryWorkingTreeReadLease> CurrentInputs { get; private set; }
        = new Dictionary<SourceId, HistoryWorkingTreeReadLease>();
    private IDisposable EnterCurrentInputScope(IEnumerable<HistoryRestoreSourceBinding> bindings) => new CurrentInputScope(this, bindings);
    private sealed class CurrentInputScope : IDisposable
    {
        private readonly HistoryRestoreService _restore;
        private readonly List<HistoryWorkingTreeReadLease> _leases = [];
        private readonly bool _previousProtection;
        private readonly Action? _previousValidation;
        private readonly IReadOnlyDictionary<SourceId, HistoryWorkingTreeReadLease> _previousInputs;
        internal CurrentInputScope(HistoryRestoreService restore, IEnumerable<HistoryRestoreSourceBinding> bindings)
        {
            _restore = restore; _previousProtection = restore.HoldCurrentReadProtection; _previousValidation = restore.ValidateCurrentInputs;
            _previousInputs = restore.CurrentInputs;
            try
            {
                var inputs = new Dictionary<SourceId, HistoryWorkingTreeReadLease>();
                foreach (var binding in bindings) { var read = new HistoryWorkingTreeReadLease(binding); _leases.Add(read); inputs.Add(binding.SourceId, read); }
                restore.CurrentInputs = inputs;
                restore.HoldCurrentReadProtection = true;
                restore.ValidateCurrentInputs = () => { _previousValidation?.Invoke(); foreach (var lease in _leases) lease.Validate(); };
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            _restore.HoldCurrentReadProtection = _previousProtection; _restore.ValidateCurrentInputs = _previousValidation;
            _restore.CurrentInputs = _previousInputs;
            foreach (var lease in _leases) lease.Dispose(); _leases.Clear();
        }
    }
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
        var (graph, environment) = await GetPreparationInputsAsync(token).ConfigureAwait(false);
        foreach (var selection in selections)
        {
            var assessment = await _representations.AssessVersionAsync(selection.Version.VersionId, graph, environment,
                _representations.CanPrepareNativeInline(selection.Version, graph) ? AssessmentDepth.Fast : AssessmentDepth.Deep,
                selection.Fidelity, token, context).ConfigureAwait(false);
            if (assessment.Readiness != HistoryReadiness.Ready || assessment.Selected is null)
                throw new IOException(HistoryQuickRestoreResolver.AssessmentDiagnostic(assessment, "Restore input is unavailable."));
            var staging = Path.Combine(session.Journal.Workspaces.Single(w => w.Location.SourceId == selection.Binding.SourceId).Location.Container, "staging");
            context.ReserveMaterializationSpace(assessment.Selected.RepresentationId, graph, staging, SpaceBudget);
        }
    }
}
