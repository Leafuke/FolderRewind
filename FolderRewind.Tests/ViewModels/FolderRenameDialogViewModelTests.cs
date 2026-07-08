using FolderRewind.Models;
using FolderRewind.ViewModels;
using System.Runtime.CompilerServices;
using Xunit;

namespace FolderRewind.Tests.ViewModels;

public class FolderRenameDialogViewModelTests
{
    [Fact]
    public void Load_populates_leaf_name_and_impact_summary()
    {
        var vm = new FolderRenameDialogViewModel();
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "WorldOne");
        var preview = new FolderRenamePreview
        {
            IsValid = true,
            OldPath = @"D:\Games\Saves\WorldOne",
            NewPath = @"D:\Games\Saves\WorldTwo",
            OldLeafName = "WorldOne",
            NewLeafName = "WorldTwo",
            AffectedConfigCount = 2,
            AffectedHistoryCount = 9
        };

        vm.Load(folder, preview);

        Assert.Equal("WorldTwo", vm.NewLeafName);
        Assert.Contains("2", vm.ImpactSummary);
        Assert.Contains("9", vm.ImpactSummary);
    }

    private static ManagedFolder CreateManagedFolder(string path, string displayName)
    {
        var folder = (ManagedFolder)RuntimeHelpers.GetUninitializedObject(typeof(ManagedFolder));
        folder.Path = path;
        folder.DisplayName = displayName;
        return folder;
    }
}
