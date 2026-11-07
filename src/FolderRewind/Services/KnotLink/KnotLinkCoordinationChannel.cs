using FolderRewind.Plugin.Runtime.Operations;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services.KnotLink;

internal static class KnotLinkCoordinationChannel
{
    internal static CoordinationSignalGate Gate { get; } = new();
    private static readonly AsyncLocal<Guid?> Ambient = new();
    private static readonly AsyncLocal<Interaction?> ActiveInteraction = new();
    private static int _activeInteractions;
    internal static bool HasActiveInteractions => Volatile.Read(ref _activeInteractions) != 0;
    internal static Guid? Owner => Ambient.Value;
    internal static IDisposable EnterScope(Guid owner) => new Scope(owner);
    internal static Interaction EnterInteraction(Guid owner) => new(owner);
    internal static ValueTask AdmitInteractionAsync(CancellationToken token)
        => ActiveInteraction.Value is { } interaction ? interaction.AdmitAsync(token) : ValueTask.CompletedTask;

    internal sealed class Interaction : IAsyncDisposable
    {
        private readonly IDisposable _scope;
        private readonly Interaction? _previous = ActiveInteraction.Value;
        private readonly Guid _owner;
        private IAsyncDisposable? _lease;
        private int _disposed;
        internal Interaction(Guid owner) { _owner = Owner ?? owner; _scope = EnterScope(_owner); ActiveInteraction.Value = this; Interlocked.Increment(ref _activeInteractions); }
        internal async ValueTask AdmitAsync(CancellationToken token)
        { _lease ??= await Gate.EnterAsync(_owner, token).ConfigureAwait(false); }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false); }
            finally { ActiveInteraction.Value = _previous; _scope.Dispose(); Interlocked.Decrement(ref _activeInteractions); }
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Guid? _previous = Ambient.Value;
        internal Scope(Guid owner) => Ambient.Value = owner;
        public void Dispose() => Ambient.Value = _previous;
    }
}
