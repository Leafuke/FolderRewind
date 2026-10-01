using FolderRewind.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal enum SourceScopePreviewState { Ready, Running, Invalid, Skipped, Completed, Failed, Canceled }
internal sealed record SourceScopePreviewResult(int FileCount, long Bytes);

/// <summary>Only the latest active preview can publish. The owner calls from its UI context.</summary>
internal sealed class SourceScopePreviewController : IDisposable
{
    private readonly LatestRequestCoordinator _requests = new();
    private readonly Func<string, BackupSourceScope, CancellationToken, Task<SourceScopePreviewResult>> _enumerate;
    private bool _disposed;

    public SourceScopePreviewController()
        : this((root, scope, token) => Task.Run(() =>
        {
            var files = BackupSourceFileEnumerator.Enumerate(root, scope, cancellationToken: token);
            return new SourceScopePreviewResult(files.Count, files.Sum(file => file.Size));
        }, token)) { }

    internal SourceScopePreviewController(Func<string, BackupSourceScope, CancellationToken, Task<SourceScopePreviewResult>> enumerate)
        => _enumerate = enumerate;

    public bool IsPreviewing => State == SourceScopePreviewState.Running;
    public SourceScopePreviewState State { get; private set; }
    public SourceScopePreviewResult? Result { get; private set; }
    public string Error { get; private set; } = string.Empty;
    public event Action? StateChanged;

    public async Task RefreshAsync(string root, BackupSourceScope? scope, string validationError = "")
    {
        if (_disposed) return;
        using var request = _requests.Begin();
        Result = null;
        if (scope is null) { SetState(SourceScopePreviewState.Invalid, validationError); return; }
        if (scope.Mode == BackupSourceScopeMode.All && BackupSourceRootSafetyPolicy.IsBroadRoot(root))
        {
            SetState(SourceScopePreviewState.Skipped);
            return;
        }
        SetState(SourceScopePreviewState.Running);
        try
        {
            var result = await _enumerate(root, scope, request.Token).ConfigureAwait(true);
            if (!request.IsCurrent) return;
            Result = result;
            SetState(SourceScopePreviewState.Completed);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (request.IsCurrent) SetState(SourceScopePreviewState.Failed, ex.Message);
        }
        finally
        {
            // External enumeration may acknowledge cancellation without returning a result.
            if (request.IsCurrent && IsPreviewing) SetState(SourceScopePreviewState.Canceled);
        }
    }

    public void Cancel()
    {
        if (_disposed) return;
        _requests.CancelCurrent();
        Result = null;
        SetState(SourceScopePreviewState.Canceled);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requests.Dispose();
        State = SourceScopePreviewState.Canceled;
        Result = null;
        Error = string.Empty;
        StateChanged = null;
    }

    private void SetState(SourceScopePreviewState state, string error = "")
    {
        State = state;
        Error = error;
        StateChanged?.Invoke();
    }
}
