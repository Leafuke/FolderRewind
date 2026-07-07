using FolderRewind.Models;
using FolderRewind.Services;
using System.Runtime.CompilerServices;
using Xunit;

namespace FolderRewind.Tests.Services;

public class FolderRenameServiceTests
{
    [Fact]
    public void PreviewRename_rejects_invalid_leaf_name()
    {
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "WorldOne");

        var preview = FolderRenameService.PreviewRename(folder, "bad:name");

        Assert.False(preview.IsValid);
        Assert.Equal(@"D:\Games\Saves\WorldOne", preview.OldPath);
        Assert.Equal(string.Empty, preview.NewPath);
    }

    [Fact]
    public void ResolveUpdatedDisplayName_only_changes_default_display_name()
    {
        Assert.Equal(
            "WorldTwo",
            FolderRenameService.ResolveUpdatedDisplayName("WorldOne", "WorldOne", "WorldTwo"));

        Assert.Equal(
            "My Favorite World",
            FolderRenameService.ResolveUpdatedDisplayName("My Favorite World", "WorldOne", "WorldTwo"));
    }

    [Fact]
    public void ResolveUpdatedHistoryFolderName_only_changes_matching_storage_name()
    {
        Assert.Equal(
            "WorldTwo",
            FolderRenameService.ResolveUpdatedHistoryFolderName("WorldOne", "WorldOne", "WorldTwo"));

        Assert.Equal(
            "custom-archive-name",
            FolderRenameService.ResolveUpdatedHistoryFolderName("custom-archive-name", "WorldOne", "WorldTwo"));
    }

    private static ManagedFolder CreateManagedFolder(string path, string displayName)
    {
        var folder = (ManagedFolder)RuntimeHelpers.GetUninitializedObject(typeof(ManagedFolder));
        folder.Path = path;
        folder.DisplayName = displayName;
        return folder;
    }
}
