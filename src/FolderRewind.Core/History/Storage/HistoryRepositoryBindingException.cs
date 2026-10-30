using System;

namespace FolderRewind.History.Storage;

/// <summary>Preserves the binding outcome across the application gateway.</summary>
internal sealed class HistoryRepositoryBindingException(HistoryRepositoryBindingStatus status, string diagnostic)
    : InvalidOperationException(diagnostic)
{
    public HistoryRepositoryBindingStatus Status { get; } = status;
}
