using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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

    [Theory]
    [InlineData("CON")]
    [InlineData("prn.txt")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9.zip")]
    [InlineData("WorldTwo.")]
    [InlineData("WorldTwo ")]
    public void PreviewRename_rejects_windows_reserved_or_trailing_leaf_names(string newLeafName)
    {
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "WorldOne");

        var preview = FolderRenameService.PreviewRename(folder, newLeafName);

        Assert.False(preview.IsValid);
        Assert.Equal(@"D:\Games\Saves\WorldOne", preview.OldPath);
        Assert.Equal(string.Empty, preview.NewPath);
    }

    [Fact]
    public void PreviewRename_counts_matching_paths_after_normalization()
    {
        var oldPath = @"D:\Games\Saves\WorldOne";
        var folder = CreateManagedFolder(oldPath, "WorldOne");
        var config = CreateBackupConfig("config-one", new[]
        {
            CreateManagedFolder(@"D:\Games\Saves\WorldOne\", "WorldOne"),
            CreateManagedFolder(@"D:\Games\Saves\OtherWorld", "OtherWorld")
        });

        SetCurrentConfig(CreateAppConfig(new[] { config }));
        SetHistoryItems(new[]
        {
            new HistoryItem { ConfigId = "config-one", FolderPath = @"D:\Games\Saves\WorldOne\", FolderName = "WorldOne" },
            new HistoryItem { ConfigId = "config-one", FolderPath = @"D:\Games\Saves\OtherWorld", FolderName = "OtherWorld" }
        });

        var preview = FolderRenameService.PreviewRename(folder, "WorldTwo");

        Assert.True(preview.IsValid);
        Assert.Equal(1, preview.AffectedConfigCount);
        Assert.Equal(1, preview.AffectedHistoryCount);
    }

    [Fact]
    public void PreviewRename_keeps_storage_folder_names_based_on_custom_display_name()
    {
        var folder = CreateManagedFolder(@"D:\Games\Saves\WorldOne", "My Favorite World");
        SetCurrentConfig(CreateAppConfig(Array.Empty<BackupConfig>()));
        SetHistoryItems(Array.Empty<HistoryItem>());

        var preview = FolderRenameService.PreviewRename(folder, "WorldTwo");

        Assert.True(preview.IsValid);
        Assert.Equal("My Favorite World", preview.OldStorageFolderName);
        Assert.Equal("My Favorite World", preview.NewStorageFolderName);
    }

    [Fact]
    public void PreviewRename_rejects_drive_root_path()
    {
        var folder = CreateManagedFolder(@"D:\", "Drive Root");

        var preview = FolderRenameService.PreviewRename(folder, "RenamedDrive");

        Assert.False(preview.IsValid);
        Assert.Equal(@"D:\", preview.OldPath);
        Assert.Equal(string.Empty, preview.OldLeafName);
        Assert.Equal(string.Empty, preview.NewPath);
    }

    [Fact]
    public void PreviewRename_rejects_share_root_path()
    {
        var folder = CreateManagedFolder(@"\\server\share\", "Share Root");

        var preview = FolderRenameService.PreviewRename(folder, "RenamedShare");

        Assert.False(preview.IsValid);
        Assert.Equal(@"\\server\share\", preview.OldPath);
        Assert.Equal(string.Empty, preview.OldLeafName);
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

    [Fact]
    public void ApplyReferenceUpdates_rewrites_paths_history_and_recent_selection()
    {
        string oldPath = @"D:\Games\Saves\WorldOne";
        string newPath = @"D:\Games\Saves\WorldTwo";

        var config = CreateBackupConfig("cfg-a", new[]
        {
            CreateManagedFolder(oldPath, "WorldOne")
        });
        config.Name = "Primary";
        config.Automation.Scope = AutomationScope.SingleFolder;
        config.Automation.TargetFolderPath = oldPath;

        var settings = CreateGlobalSettings();
        settings.LastManagerFolderPath = oldPath;
        settings.LastHistoryFolderPath = oldPath;

        var historyItems = new List<HistoryItem>
        {
            new()
            {
                ConfigId = "cfg-a",
                FolderPath = oldPath,
                FolderName = "WorldOne",
                FileName = "[Full][2026-07-07_12-00-00]WorldOne.7z"
            }
        };

        var preview = new FolderRenamePreview
        {
            OldPath = oldPath,
            NewPath = newPath,
            OldLeafName = "WorldOne",
            NewLeafName = "WorldTwo",
            OldStorageFolderName = "WorldOne",
            NewStorageFolderName = "WorldTwo"
        };

        FolderRenameService.ApplyReferenceUpdates(new[] { config }, settings, historyItems, preview);

        Assert.Equal(newPath, config.SourceFolders[0].Path);
        Assert.Equal("WorldTwo", config.SourceFolders[0].DisplayName);
        Assert.Equal(newPath, config.Automation.TargetFolderPath);
        Assert.Equal(newPath, settings.LastManagerFolderPath);
        Assert.Equal(newPath, settings.LastHistoryFolderPath);
        Assert.Equal(newPath, historyItems[0].FolderPath);
        Assert.Equal("WorldTwo", historyItems[0].FolderName);
    }

    [Fact]
    public void ExecuteMovePlan_rolls_back_source_rename_when_later_move_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "saves", "WorldOne");
        string renamed = Path.Combine(root, "saves", "WorldTwo");
        string backup = Path.Combine(root, "backup", "WorldOne");
        string backupRenamed = Path.Combine(root, "backup", "WorldTwo");
        string metadata = Path.Combine(root, "backup", "_metadata", "WorldOne");
        string metadataCollision = Path.Combine(root, "backup", "_metadata", "WorldTwo");

        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(backup);
            Directory.CreateDirectory(metadata);
            Directory.CreateDirectory(metadataCollision);

            var result = FolderRenameService.ExecuteMovePlan(
                new[]
                {
                    new FolderMoveOperation { SourcePath = source, DestinationPath = renamed, Description = "source" },
                    new FolderMoveOperation { SourcePath = backup, DestinationPath = backupRenamed, Description = "backup" },
                    new FolderMoveOperation { SourcePath = metadata, DestinationPath = metadataCollision, Description = "metadata" }
                });

            Assert.False(result.Success);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(renamed));
            Assert.True(Directory.Exists(backup));
            Assert.False(Directory.Exists(backupRenamed));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ManagedFolder CreateManagedFolder(string path, string displayName)
    {
        var folder = (ManagedFolder)RuntimeHelpers.GetUninitializedObject(typeof(ManagedFolder));
        folder.Path = path;
        folder.DisplayName = displayName;
        return folder;
    }

    private static BackupConfig CreateBackupConfig(string id, IEnumerable<ManagedFolder> sourceFolders)
    {
        var config = (BackupConfig)RuntimeHelpers.GetUninitializedObject(typeof(BackupConfig));
        config.Id = id;
        config.SourceFolders = new ObservableCollection<ManagedFolder>(sourceFolders);
        config.Automation = new AutomationSettings();
        return config;
    }

    private static AppConfig CreateAppConfig(IEnumerable<BackupConfig> backupConfigs)
    {
        var appConfig = (AppConfig)RuntimeHelpers.GetUninitializedObject(typeof(AppConfig));
        appConfig.BackupConfigs = new ObservableCollection<BackupConfig>(backupConfigs);
        return appConfig;
    }

    private static void SetCurrentConfig(AppConfig appConfig)
    {
        typeof(ConfigService)
            .GetProperty(nameof(ConfigService.CurrentConfig), BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, appConfig);
    }

    private static GlobalSettings CreateGlobalSettings()
    {
        return (GlobalSettings)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSettings));
    }

    private static void SetHistoryItems(IEnumerable<HistoryItem> historyItems)
    {
        typeof(HistoryService)
            .GetField("_allHistory", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new List<HistoryItem>(historyItems));
        typeof(HistoryService)
            .GetField("_initialized", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, true);
    }
}
