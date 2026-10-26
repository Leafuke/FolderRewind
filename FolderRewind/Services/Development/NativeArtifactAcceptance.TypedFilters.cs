#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FolderRewind.History.Domain;
using FolderRewind.History.Application;
using FolderRewind.History.LocalState;
using FolderRewind.Models;
using FolderRewind.Plugin.Abstractions;
using FolderRewind.Services.Plugins.V3;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    private static async Task<int> RunTypedFilterMergeAsync(BackupConfig config, string root)
    {
        var folder = config.SourceFolders.Single();
        var source = new SourceId(Guid.Parse(folder.Id));
        var region = Path.Combine(folder.Path, "region", "r.0.0.mca");
        Directory.CreateDirectory(Path.GetDirectoryName(region)!);
        config.Filters.Blacklist.Add(new("session.lock", PathFilterTarget.File));
        config.Filters.RestoreWhitelist.Add(new("cache", PathFilterTarget.Directory));
        await SaveAsync();
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        await CaptureAsync(1, 1);
        var first = (await history.Query.GetAllCheckpointsAsync()).Single(c => c.SourceId == source);
        var feature = await history.Branches.CreateFromCheckpointAsync(first.CheckpointId, "typed-source", sourceId: source);
        await CaptureAsync(2, 1);
        var ours = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).ActiveBranchUpdateId!.Value;
        await CheckoutAsync(feature.BranchUpdate.UpdateId);
        await CaptureAsync(1, 3);
        await CheckoutAsync(ours);
        var binding = PluginV3SemanticMerge.Options(config).Single(option => option.Binding.PolicyId == "conservative").Binding;
        var session = (await NativeHistoryApplicationService.StartMergeAsync(config, feature.BranchUpdate.BranchId, default, binding))!;
        if (session.State != MergeSessionState.Ready) throw new IOException("Native typed-filter merge is not Ready: " + session.State);
        if ((await history.Query.GetAllVersionsAsync()).Any(version => version.EffectiveSourceBoundary.SchemaVersion != 2))
            throw new IOException("New capture omitted typed boundary metadata.");
        var nativeCount = (await history.Query.GetAllRepresentationsAsync()).Count(representation => representation.Kind == RepresentationKind.PluginArtifact);
        if (nativeCount < 3) throw new IOException("Native captures were replaced by core fallback.");
        var review = await NativeHistoryApplicationService.PrepareMergeReviewAsync(config, session, _ => { }, default);
        var applied = await NativeHistoryApplicationService.ApplyMergeAsync(config, session, default, review);
        if (!applied.Succeeded) throw new IOException("Native semantic merge apply failed: " + applied.Diagnostic);
        RequireChunks(2, 3);
        // Leave another real merge task for the UI's re-analysis and review acceptance.
        await CaptureAsync(5, 3);
        ours = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).ActiveBranchUpdateId!.Value;
        var tip = (await history.Query.GetBranchTipsAsync(feature.BranchUpdate.BranchId)).Single();
        await CheckoutAsync(tip.UpdateId);
        await CaptureAsync(1, 4);
        await CheckoutAsync(ours);
        var pending = (await NativeHistoryApplicationService.StartMergeAsync(config, feature.BranchUpdate.BranchId, default, binding))!;
        if (pending.State != MergeSessionState.Ready) throw new IOException("UI acceptance merge is not Ready.");
        await SaveAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "typed-filters-result.json"), JsonSerializer.Serialize(new
        { Success = true, NativeCaptures = nativeCount, BoundaryVersion = 2, Applied = true, UiSession = pending.Id, Config = config.Id, Source = folder.Path }));
        return 0;

        async Task SaveAsync()
        {
            var saved = await ConfigService.UpdateAndSaveAsync(current =>
            {
                current.GlobalSettings.Plugins.EnabledIntent["com.folderrewind.minerewind"] = true;
                current.GlobalSettings.Plugins.AutoCheckUpdates = false;
            });
            if (!saved.Success) throw new IOException(saved.ErrorMessage);
        }
        async Task CaptureAsync(int left, int right)
        {
            await File.WriteAllBytesAsync(region, TypedFilterRegion(left, right));
            var captured = await BackupService.BackupConfigAsync(config);
            if (!captured.IsSuccessful || !captured.CreatedNewArchive) throw new IOException("Native typed-filter capture failed.");
        }
        async Task CheckoutAsync(BranchUpdateId update)
        {
            var checkout = await NativeHistoryApplicationService.CheckoutAsync(config, update);
            if (!checkout.Succeeded) throw new IOException("Typed-filter checkout failed: " + checkout.Diagnostic);
        }
        void RequireChunks(int left, int right)
        {
            var actual = File.ReadAllBytes(region); var expected = TypedFilterRegion(left, right);
            for (var slot = 0; slot < 2; slot++)
            {
                var offset = (BinaryPrimitives.ReadInt32BigEndian(actual.AsSpan(slot * 4)) >> 8) * 4096;
                var expectedOffset = (slot + 2) * 4096;
                var length = BinaryPrimitives.ReadInt32BigEndian(actual.AsSpan(offset));
                if (length != BinaryPrimitives.ReadInt32BigEndian(expected.AsSpan(expectedOffset))
                    || !actual.AsSpan(offset + 4, length).SequenceEqual(expected.AsSpan(expectedOffset + 4, length)))
                    throw new IOException("Merged chunk payload does not match the selected source.");
            }
        }
    }

    private static byte[] TypedFilterRegion(int left, int right)
    {
        var result = new byte[16384];
        for (var slot = 0; slot < 2; slot++)
        {
            using var nbt = new MemoryStream(); nbt.Write([10, 0, 0]);
            foreach (var (name, value) in new[] { ("DataVersion", 3700), ("xPos", slot), ("zPos", 0), ("value", slot == 0 ? left : right) })
            {
                var text = Encoding.UTF8.GetBytes(name); nbt.WriteByte(3); nbt.WriteByte(0); nbt.WriteByte((byte)text.Length); nbt.Write(text);
                var number = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, value); nbt.Write(number);
            }
            nbt.Write([9, 0, 8]); nbt.Write(Encoding.UTF8.GetBytes("entities")); nbt.Write([0, 0, 0, 0, 0, 0]);
            var offset = (slot + 2) * 4096; var payload = nbt.ToArray();
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(slot * 4), (slot + 2) << 8 | 1);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4096 + slot * 4), 100 + slot);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset), payload.Length + 1);
            result[offset + 4] = 3; payload.CopyTo(result, offset + 5);
        }
        return result;
    }
}
#endif
