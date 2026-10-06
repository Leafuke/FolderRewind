using FolderRewind.Plugin.Abstractions;

namespace FolderRewind.Plugin.Runtime.Activation;

internal sealed class PluginRuntimeSession
{
    private readonly object _sync = new();
    private TaskCompletionSource? _drained;
    private int _activeLeases;

    public PluginRuntimeSession(
        PluginId pluginId,
        IFolderRewindPlugin plugin,
        CapabilityRegistrationSet registrations,
        IPluginHostServices hostServices,
        CancellationTokenSource lifetime)
    {
        PluginId = pluginId;
        Plugin = plugin;
        Registrations = registrations;
        HostServices = hostServices;
        Lifetime = lifetime;
        State = PluginRuntimeState.Active;
    }

    public PluginId PluginId { get; }
    public IFolderRewindPlugin Plugin { get; }
    public CapabilityRegistrationSet Registrations { get; }
    public IPluginHostServices HostServices { get; }
    public CancellationTokenSource Lifetime { get; }
    public PluginRuntimeState State { get; private set; }

    public int ActiveLeases
    {
        get { lock (_sync) return _activeLeases; }
    }

    public PluginCapabilityLease<TCapability>? TryAcquire<TCapability>(CancellationToken operationCancellation)
        where TCapability : class, IPluginCapability
        => TryAcquire<TCapability>(_ => true, operationCancellation);

    public PluginCapabilityLease<TCapability>? TryAcquire<TCapability>(
        Func<TCapability, bool> selector,
        CancellationToken operationCancellation)
        where TCapability : class, IPluginCapability
    {
        ArgumentNullException.ThrowIfNull(selector);
        lock (_sync)
        {
            if (State != PluginRuntimeState.Active)
            {
                return null;
            }

            // 身份筛选与租约计数必须位于同一把锁内；selector 抛异常时不得增加租约计数。
            var capability = Registrations.Capabilities.OfType<TCapability>()
                .Where(selector)
                .SingleOrDefault();
            if (capability is null)
            {
                return null;
            }

            _activeLeases++;
            var context = new PluginInvocationContext(PluginId, HostServices, operationCancellation, Lifetime.Token);
            return new PluginCapabilityLease<TCapability>(capability, context, Release);
        }
    }

    public Task BeginDrain()
    {
        lock (_sync)
        {
            if (State != PluginRuntimeState.Active)
            {
                throw new InvalidOperationException($"Plugin '{PluginId}' cannot drain from state {State}.");
            }

            State = PluginRuntimeState.Draining;
            if (_activeLeases == 0)
            {
                return Task.CompletedTask;
            }

            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    public void ResumeActive()
    {
        lock (_sync)
        {
            if (State == PluginRuntimeState.Draining)
            {
                State = PluginRuntimeState.Active;
                _drained = null;
            }
        }
    }

    public void BeginDeactivation()
    {
        lock (_sync)
        {
            if (_activeLeases != 0)
            {
                throw new InvalidOperationException("A plugin cannot deactivate while leases are active.");
            }
            State = PluginRuntimeState.Deactivating;
        }
    }

    public void MarkFailed()
    {
        lock (_sync) State = PluginRuntimeState.Failed;
    }

    private void Release()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_activeLeases <= 0)
            {
                return;
            }

            _activeLeases--;
            if (_activeLeases == 0 && State == PluginRuntimeState.Draining)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }
}

public sealed class PluginCapabilityLease<TCapability> : IDisposable, IAsyncDisposable
    where TCapability : class, IPluginCapability
{
    private Action? _release;

    internal PluginCapabilityLease(TCapability capability, PluginInvocationContext context, Action release)
    {
        Capability = capability;
        Context = context;
        _release = release;
    }

    public TCapability Capability { get; }
    public PluginInvocationContext Context { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
