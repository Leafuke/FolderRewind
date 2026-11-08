using FolderRewind.Models;
using System;
using System.Threading;

namespace FolderRewind.Services;

internal sealed class NotificationOperationScope : IDisposable
{
    private static readonly AsyncLocal<NotificationOperationScope?> Ambient = new();
    private readonly NotificationOperationScope? _previous;
    public static NotificationOperationScope? Current => Ambient.Value;
    private sealed class NotificationState(NotificationTarget target)
    {
        internal NotificationTarget Target = target;
        internal bool ResultReported;
    }
    private readonly NotificationState _notification;
    public NotificationTarget Target { get => _notification.Target; set => _notification.Target = value; }
    public bool IsInternal { get; }
    public bool ResultReported { get => _notification.ResultReported; set => _notification.ResultReported = value; }
    public string OperationId => Target.OperationId!;
    public LogOperationContext Context { get; }
    private bool _disposed;
    private sealed class CommandSequence { internal int Value; }
    private readonly CommandSequence _commands;
    internal int NextCommandAttempt() => Interlocked.Increment(ref _commands.Value);

    public NotificationOperationScope(NotificationTarget target, bool isInternal = false, LogOperationContext? context = null)
    {
        _previous = Ambient.Value;
        _notification = new(target with { OperationId = target.OperationId ?? Guid.NewGuid().ToString("N") });
        IsInternal = isInternal;
        Context = context ?? new(Target.OperationId!, target.Kind.ToString());
        _commands = _previous?.OperationId == OperationId ? _previous._commands : new();
        Ambient.Value = this;
    }

    private NotificationOperationScope(NotificationOperationScope parent, LogOperationContext context)
        : this(parent.Target, parent.IsInternal, context) => _notification = parent._notification;

    public LogOperationContext Capture() => Context with { OperationId = OperationId,
        ConfigId = Context.ConfigId ?? Target.ConfigId, SourceId = Context.SourceId ?? Target.FolderId,
        RunId = Context.RunId ?? Target.RunId, JobId = Context.JobId ?? Target.JobId };

    internal static NotificationOperationScope? EnterChild(Func<LogOperationContext, LogOperationContext> enrich)
        => Current is { } parent ? new(parent, enrich(parent.Capture())) : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ambient.Value = _previous;
    }
}
