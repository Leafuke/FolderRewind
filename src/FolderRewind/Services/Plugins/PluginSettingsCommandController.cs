using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed class PluginSettingsCommandController(IPluginSettingsActions actions, Action<Exception> report) : IDisposable
{
    private readonly AsyncCommandLifetime _lifetime = new(report);
    public bool IsBusy => _lifetime.IsBusy;
    public bool CanExecute => _lifetime.CanExecute;
    public event Action? Changed { add => _lifetime.StateChanged += value; remove => _lifetime.StateChanged -= value; }
    public Task ExecuteAsync(PluginSettingsRequest request, CancellationToken token = default)
        => _lifetime.RunAsync(ct => actions.ExecuteAsync(request, ct), token);
    public void Activate() => _lifetime.Activate();
    public void Deactivate() => _lifetime.Deactivate();
    public void Dispose() => _lifetime.Dispose();
}
