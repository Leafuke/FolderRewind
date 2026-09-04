namespace FolderRewind.Tests;

[TestClass]
public sealed class HistoryPageMvvmArchitectureTests
{
    [TestMethod]
    public void HistoryPage_CodeBehind_DoesNotOrchestrateBusinessServicesOrDialogs()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "FolderRewind", "Views", "HistoryPage.xaml.cs"));

        var forbidden = new[]
        {
            "ContentDialog",
            "AppDialogService",
            "NotificationService",
            "ConfigService.Save",
            "Task.Run",
            "NativeHistoryApplicationService",
            "CloudSyncService",
            "EncryptionService"
        };
        foreach (var token in forbidden)
        {
            Assert.IsFalse(source.Contains(token, StringComparison.Ordinal), $"HistoryPage code-behind still contains {token}.");
        }
    }

    [TestMethod]
    public void HistoryPage_TopLevelOperations_AreBoundToViewModelCommands()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(repositoryRoot, "FolderRewind", "Views", "HistoryPage.xaml"));
        var requiredCommands = new[]
        {
            "CheckoutBranchCommand",
            "ReconcileBranchCommand",
            "RenameBranchCommand",
            "DeleteBranchCommand",
            "OpenCloudSyncCommand",
            "ManageSafetySnapshotsCommand",
            "ClearMissingCommand",
            "ScanRecoverCommand",
            "RetryCommand"
        };
        foreach (var command in requiredCommands)
        {
            StringAssert.Contains(xaml, $"ViewModel.{command}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FolderRewind.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
