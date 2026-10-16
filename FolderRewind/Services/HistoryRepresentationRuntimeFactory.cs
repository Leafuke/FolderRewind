using FolderRewind.History.Application;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;

namespace FolderRewind.Services;

/// <summary>Constructs consumers using the operation's frozen configuration and codec credentials.</summary>
internal static class HistoryRepresentationRuntimeFactory
{
    internal static RepresentationRuntime Create(BackupConfig config, HistoryRuntime history,
        IArchiveRepresentationBackend? archive = null)
    {
        archive ??= new SevenZipHistoryArchiveBackend(config);
        return new RepresentationRuntime([
            new CoreArchiveRepresentationHandler(archive),
            new SmartDeltaRepresentationHandler(archive),
            Plugins.V3.NativeArtifactHostService.Handler(config, history, archive as IArtifactEnvelopeCodec)
        ]);
    }
}
