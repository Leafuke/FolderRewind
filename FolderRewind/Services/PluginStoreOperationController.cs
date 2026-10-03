using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>All store commands share one lifetime, including file selection and runtime transitions.</summary>
internal sealed class PluginStoreOperationController : IDisposable
{
    private readonly AsyncCommandLifetime _lifetime;
    private long _generation;
    private bool _active;

    public PluginStoreOperationController(Action<Exception> reportError)
    {
        _lifetime = new AsyncCommandLifetime(reportError);
        _lifetime.Deactivate();
        _lifetime.StateChanged += OnStateChanged;
    }

    public bool IsBusy => _lifetime.IsBusy;
    public bool CanExecute => _lifetime.CanExecute;
    public event Action? StateChanged;

    public void Activate()
    {
        _active = true;
        _generation++;
        _lifetime.Activate();
    }

    public void Deactivate()
    {
        _active = false;
        _generation++;
        _lifetime.Deactivate();
    }

    public void Cancel() => _lifetime.Cancel();

    public Task RunAsync(Func<CancellationToken, Func<bool>, Task> operation, CancellationToken token = default)
    {
        var generation = _generation;
        return _lifetime.RunAsync(inner => operation(inner, () => _active && generation == _generation), token);
    }

    public void Dispose()
    {
        Deactivate();
        _lifetime.StateChanged -= OnStateChanged;
        _lifetime.Dispose();
    }

    private void OnStateChanged() => StateChanged?.Invoke();
}
