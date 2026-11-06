#if NATIVE_ARTIFACT_ACCEPTANCE
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolderRewind.History.Application;
using FolderRewind.History.Domain;
using FolderRewind.Models;

namespace FolderRewind.Services.Development;

internal static partial class NativeArtifactAcceptance
{
    // Called only by the isolated native-artifact acceptance entry point. All mutations
    // are confined to its fresh, marked workspace and its copied source.
    private static async Task<int> SeedMergeUiAsync(BackupConfig config, string root)
    {
        var folder = config.SourceFolders.Single();
        var source = new SourceId(Guid.Parse(folder.Id));
        var dimensionRoot = Path.Combine(folder.Path, "dimensions", "minecraft", "overworld");
        if (Directory.Exists(dimensionRoot) && Directory.Exists(Path.Combine(folder.Path, "region")))
            throw new InvalidDataException("Merge UI source has ambiguous overworld roots.");
        var region = Path.Combine(Directory.Exists(dimensionRoot) ? dimensionRoot : folder.Path, "region", "r.0.0.mca");
        Directory.CreateDirectory(Path.GetDirectoryName(region)!);
        var saved = await ConfigService.UpdateAndSaveAsync(current =>
        {
            current.GlobalSettings.Plugins.EnabledIntent["com.folderrewind.minerewind"] = true;
            current.GlobalSettings.Plugins.AutoCheckUpdates = false;
            current.GlobalSettings.EnableNotifications = false;
        });
        if (!saved.Success) throw new IOException(saved.ErrorMessage);
        var history = await NativeHistoryCoreGateway.EnsureReadyAsync(config);
        await CaptureAsync(1, 1);
        var first = (await history.Query.GetAllCheckpointsAsync()).Single(c => c.SourceId == source);
        var feature = await history.Branches.CreateFromCheckpointAsync(first.CheckpointId, "UI feature", sourceId: source);
        await CaptureAsync(2, 1);
        var ours = (await history.WorkspaceStore.LoadAsync()).Value!.GetSourceState(source).ActiveBranchUpdateId!.Value;
        await CheckoutAsync(feature.BranchUpdate.UpdateId);
        await CaptureAsync(1, 3);
        await CheckoutAsync(ours);
        await File.WriteAllTextAsync(Path.Combine(root, "merge-ui-seed.json"), JsonSerializer.Serialize(new
        {
            status = "READY", config = config.Id, folder = folder.Id,
            branch = feature.BranchUpdate.BranchId.ToString(), package = "com.folderrewind.minerewind"
        }));
        return 0;

        async Task CaptureAsync(int left, int right)
        {
            await File.WriteAllBytesAsync(region, TypedFilterRegion(left, right));
            var result = await BackupService.BackupConfigAsync(config);
            if (!result.IsSuccessful || !result.CreatedNewArchive) throw new IOException("Merge UI capture failed.");
        }
        async Task CheckoutAsync(BranchUpdateId update)
        {
            var result = await NativeHistoryApplicationService.CheckoutAsync(config, update);
            if (!result.Succeeded) throw new IOException("Merge UI checkout failed: " + result.Diagnostic);
        }
    }
}
#endif
