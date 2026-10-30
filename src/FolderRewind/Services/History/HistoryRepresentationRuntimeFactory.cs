using FolderRewind.History.Application;
using FolderRewind.History.Representation;
using FolderRewind.Models;
using FolderRewind.Plugin.Runtime.Artifacts;
using FolderRewind.History.Domain;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.Services;

internal sealed record HistoryRepresentationConsumerContext(BackupConfig Snapshot, HistoryRuntime History,
    IArchiveRepresentationBackend Archive, Func<NativeArtifactStore> ArtifactStore,
    Func<VersionRepresentation, CancellationToken, Task<bool>>? IsNativeRegistered = null);

/// <summary>Constructs consumers using the operation's frozen configuration and codec credentials.</summary>
internal static class HistoryRepresentationRuntimeFactory
{
    internal static RepresentationRuntime Create(BackupConfig config, HistoryRuntime history,
        IArchiveRepresentationBackend? archive = null)
    {
        var snapshot = ConfigSnapshotMapper.Copy(config);
        archive ??= new SevenZipHistoryArchiveBackend(snapshot, freezeInput: true);
        var codec = archive as IArtifactEnvelopeCodec;
        return Create(new(snapshot, history, archive, () => Plugins.V3.NativeArtifactHostService.Store(snapshot,
            codec ?? throw new InvalidOperationException("The consumer requires an explicit Native envelope codec."))));
    }

    internal static RepresentationRuntime Create(HistoryRepresentationConsumerContext context)
    {
        if (!context.History.ConfigId.Matches(context.Snapshot.Id)) throw new InvalidDataException("Representation consumer context belongs to another project.");
        return new RepresentationRuntime([
            new CoreArchiveRepresentationHandler(context.Archive),
            new SmartDeltaRepresentationHandler(context.Archive),
            Plugins.V3.NativeArtifactHostService.Handler(context.Snapshot, context.History,
                context.ArtifactStore, context.IsNativeRegistered,
                context.Archive is IArchiveRestorePreflightBackend preflight ? () => preflight.InputPolicyIdentity : null)
        ]);
    }
}
