using FolderRewind.Models;
using System;
using System.Threading;

namespace FolderRewind.Services;

internal sealed class NotificationOperationScope : IDisposable
{
    private static readonly AsyncLocal<NotificationOperationScope?> Ambient = new();
    private readonly NotificationOperationScope? _previous;
    public static NotificationOperationScope? Current => Ambient.Value;
    public NotificationTarget Target { get; set; }
    public bool IsInternal { get; }
    public bool ResultReported { get; set; }
    public string OperationId => Target.OperationId!;

    public NotificationOperationScope(NotificationTarget target, bool isInternal = false)
    {
        _previous = Ambient.Value;
        Target = target with { OperationId = target.OperationId ?? Guid.NewGuid().ToString("N") };
        IsInternal = isInternal;
        Ambient.Value = this;
    }

    public void Dispose() => Ambient.Value = _previous;
}
