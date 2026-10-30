using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.History.Merge;

public enum MergeOperationStage { Idle, Loading, Analyzing, Downloading, Saving, Building, Validating, Protecting, Writing, Committing, Finishing, Recovering }

public sealed record MergeOperationSnapshot(MergeSession? Session = null, MergeOperationStage Stage = MergeOperationStage.Idle,
    bool IsBusy = false, bool CanStop = false, bool IsSaved = true, HistoryRestoreResult? Result = null,
    string? Error = null, string? Notice = null);

/// <summary>Application-owned operation lifetime. Observers may detach without cancelling work.</summary>
public sealed class MergeOperationTracker
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _task = Task.CompletedTask;
    private MergeOperationSnapshot _snapshot = new();
    public MergeOperationSnapshot Snapshot { get { lock (_sync) return _snapshot; } }
    public Task PendingTask { get { lock (_sync) return _task; } }
    public event Action? Changed;

    public void Update(Func<MergeOperationSnapshot, MergeOperationSnapshot> update)
    {
        lock (_sync) _snapshot = update(_snapshot);
        // A detached/broken UI observer must never change the outcome of an operation.
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action)subscriber)(); } catch { }
    }

    public Task RunAsync(MergeOperationStage stage, Func<CancellationToken, Task> action, bool canStop = true)
    {
        lock (_sync)
        {
            if (_snapshot.IsBusy) return _task;
            _cancellation = new();
            var cancellation = _cancellation;
            _snapshot = _snapshot with { IsBusy = true, CanStop = canStop, Stage = stage, Error = null, Notice = null };
            _task = Task.Run(async () =>
            {
                Update(s => s);
                try { await action(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                { Update(s => s with { Notice = "Merge_Cancelled" }); }
                catch (Exception ex)
                { Update(s => s with { Error = ex.Message }); }
                finally
                {
                    lock (_sync) { _cancellation = null; cancellation.Dispose(); }
                    Update(s => s with { IsBusy = false, CanStop = false, Stage = MergeOperationStage.Idle });
                }
            });
            return _task;
        }
    }

    public void Stop()
    {
        lock (_sync) if (_snapshot.CanStop) _cancellation?.Cancel();
    }

    public CancellationToken EnterCritical(CancellationToken token)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            _snapshot = _snapshot with { CanStop = false };
        }
        Update(s => s);
        return CancellationToken.None;
    }

    public void ReportResult(HistoryRestoreResult result) => Update(s => s with { Result = result });
    public void ReportStage(MergeOperationStage stage) => Update(s => s with { Stage = stage });
}
