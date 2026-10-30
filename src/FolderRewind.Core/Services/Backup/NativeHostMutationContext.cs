using System;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal static class NativeHostMutationContext
{
    private sealed record State(bool InsideCoordinatorCallback, bool IsSuppliedContinuation);
    private static readonly AsyncLocal<State?> Current = new();

    public static bool IsNestedMutationBlocked => Current.Value is
        { InsideCoordinatorCallback: true, IsSuppliedContinuation: false };

    public static void ThrowIfNestedMutation()
    {
        if (IsNestedMutationBlocked)
            throw new InvalidOperationException("NestedHostMutationNotAllowed");
    }

    public static IDisposable EnterCoordinatorCallback()
    {
        var previous = Current.Value;
        Current.Value = new State(true, false);
        return new Scope(previous);
    }

    public static async Task<T> RunSuppliedContinuationAsync<T>(Func<Task<T>> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var previous = Current.Value;
        Current.Value = new State(true, true);
        try { return await continuation().ConfigureAwait(false); }
        finally { Current.Value = previous; }
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        private State? _previous = previous;

        public void Dispose()
        {
            if (_previous is null && Current.Value is null) return;
            Current.Value = _previous;
            _previous = null;
        }
    }
}
