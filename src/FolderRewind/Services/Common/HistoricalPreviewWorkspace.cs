using FolderRewind.History.Domain;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Plugin.Runtime.Artifacts;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

/// <summary>A page-owned plaintext tree. Callers must drain all readers before disposing it.</summary>
internal sealed class HistoricalPreviewWorkspace(OwnedPluginTemporaryStorage storage, string directory,
    ConfigSnapshot origin, Guid folderId, SourceVersion version, bool isPartial) : IAsyncDisposable
{
    private int _disposed;
    internal string DirectoryPath { get; } = directory;
    internal ConfigSnapshot Origin { get; } = origin;
    internal Guid FolderId { get; } = folderId;
    internal SourceVersion Version { get; } = version;
    internal RepresentationId? RepresentationId { get; init; }
    internal bool IsPartial { get; } = isPartial;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await storage.DeleteAsync().ConfigureAwait(false);
    }
}

internal enum HistoricalPreviewStage { Assessing, Verifying, Materializing }

internal sealed class HistoricalPreviewUnavailableException(string message, bool requiresDownload = false) : IOException(message)
{
    internal bool RequiresDownload { get; } = requiresDownload;
}

