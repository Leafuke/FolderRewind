using FolderRewind.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>Coalesces requests before UI snapshot capture. Persistence is queued synchronously in capture order.</summary>
internal sealed class ConfigSnapshotCoordinator<T>(Action<Action> schedule, Func<T> capture,
    Func<T, bool, Task<ConfigSaveResult>> persist)
{
    private readonly object _sync = new();
    private List<Request> _pending = [];
    private bool _scheduled;
    private bool _sealed;

    public Task<ConfigSaveResult> EnqueueAsync(Action? mutation, bool publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new Request(mutation, publish, token);
        lock (_sync)
        {
            if (_sealed) { request.Dispose(); return Task.FromResult(Failure(new InvalidOperationException("Configuration saves are sealed."))); }
            _pending.Add(request);
            if (_scheduled) return request.Completion.Task;
            _scheduled = true;
        }
        try { schedule(Drain); }
        catch (Exception error)
        {
            List<Request> failed;
            lock (_sync) { failed = _pending; _pending = []; _scheduled = false; }
            foreach (var item in failed) { item.Completion.TrySetResult(Failure(error)); item.Dispose(); }
        }
        return request.Completion.Task;
    }

    // Both normal dispatch and the synchronous compatibility save call this on the UI owner.
    public void Drain()
    {
        List<Request> batch;
        lock (_sync) { batch = _pending; _pending = []; _scheduled = false; }
        var accepted = new List<Request>();
        foreach (var request in batch)
        {
            if (request.Completion.Task.IsCanceled) { request.Dispose(); continue; }
            try { request.Mutation?.Invoke(); accepted.Add(request); }
            catch (Exception error) { request.Completion.TrySetResult(Failure(error)); request.Dispose(); }
        }
        if (accepted.Count == 0) return;
        try
        {
            var snapshot = capture();
            var result = persist(snapshot, accepted.Exists(item => item.Publish));
            _ = CompleteAsync(accepted, result);
        }
        catch (Exception error)
        {
            foreach (var item in accepted) { item.Completion.TrySetResult(Failure(error)); item.Dispose(); }
        }
    }

    public void Seal()
    {
        lock (_sync) _sealed = true;
        Drain();
    }

    private static async Task CompleteAsync(List<Request> requests, Task<ConfigSaveResult> persistence)
    {
        ConfigSaveResult result;
        try { result = await persistence.ConfigureAwait(false); }
        catch (Exception error) { result = Failure(error); }
        foreach (var request in requests) { request.Completion.TrySetResult(result); request.Dispose(); }
    }

    private static ConfigSaveResult Failure(Exception error) => new() { ErrorMessage = error.Message, Exception = error };
    private sealed class Request : IDisposable
    {
        public Action? Mutation { get; }
        public bool Publish { get; }
        public TaskCompletionSource<ConfigSaveResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;
        public Request(Action? mutation, bool publish, CancellationToken token)
        {
            Mutation = mutation; Publish = publish;
            _registration = token.Register(() => Completion.TrySetCanceled(token));
        }
        public void Dispose() => _registration.Dispose();
    }
}
