using System;

namespace FolderRewind.Models;

public sealed class ConfigSaveResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public Exception? Exception { get; init; }
}

public sealed class HistorySaveResult
{
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public Exception? Exception { get; init; }
}
