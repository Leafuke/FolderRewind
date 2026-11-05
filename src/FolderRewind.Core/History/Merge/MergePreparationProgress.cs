using System;

namespace FolderRewind.History.Merge;

internal enum MergePreparationStage { PreparingHistory, AnalyzingWorld, OrganizingResults, InitializingVisualization }

/// <summary>Presentation-only progress. It never changes the persisted Session state.</summary>
internal sealed record MergePreparationProgress(MergePreparationStage Stage, long Completed = 0, long? Total = null,
    Guid? SessionId = null, Guid? PlanRevision = null);

internal sealed class MergePreparationReporter(Action<MergePreparationProgress>? observer, TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private MergePreparationStage? _stage;
    private long _last;
    internal Guid? SessionId { get; set; }
    internal Guid? PlanRevision { get; set; }

    internal void Report(MergePreparationStage stage, long completed = 0, long? total = null)
    {
        if (observer is null) return;
        lock (_gate)
        {
            if (_stage == stage && _clock.GetElapsedTime(_last) < TimeSpan.FromMilliseconds(100)) return;
            _stage = stage; _last = _clock.GetTimestamp();
            // UI observers must not alter preparation, cancellation or data safety outcomes.
            try { observer(new(stage, completed, total, SessionId, PlanRevision)); } catch { }
        }
    }
}
