using System.Xml.Linq;

namespace FolderRewind.Tests;

[TestClass]
public sealed class AccessibilityMarkupTests
{
    private static readonly HashSet<string> InteractiveElementNames =
    [
        "Button",
        "CheckBox",
        "ComboBox",
        "DropDownButton",
        "Expander",
        "GridView",
        "HyperlinkButton",
        "ListView",
        "MenuFlyoutItem",
        "MenuFlyoutSubItem",
        "NavigationView",
        "NumberBox",
        "RadioButton",
        "SplitButton",
        "TextBox",
        "ToggleButton",
        "ToggleSwitch"
    ];

    private static readonly string[] AuditedXamlFiles =
    [
        "Views/HistoryPage.xaml",
        "Views/HomePage.xaml",
        "Views/FolderManagerPage.xaml",
        "Views/MiniWindow.xaml",
        "Views/Settings/AboutControl.xaml",
        "Views/Settings/AppearanceLayoutControl.xaml",
        "Views/Settings/CoreBehaviorControl.xaml",
        "Views/Settings/DataManagementControl.xaml",
        "Views/Settings/DiagnosticsControl.xaml",
        "Views/Settings/PluginsKnotLinkControl.xaml",
        "Views/Settings/PresetSettingsControl.xaml",
        "Views/Settings/RuntimeEnvControl.xaml"
    ];

    [TestMethod]
    public void AuditedViewsUseUniqueStaticAutomationIds()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        var ids = AuditedXamlFiles
            .Select(path => Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Attributes()
                .Where(attribute => attribute.Name.LocalName == "AutomationProperties.AutomationId")
                .Select(attribute => (path, id: attribute.Value)))
            .Where(item => !item.id.StartsWith("{x:Bind", StringComparison.Ordinal))
            .ToArray();

        var duplicates = ids
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.IsGreaterThanOrEqualTo(140, ids.Length, $"Expected the audited surfaces to expose stable selectors, found {ids.Length}.");
        Assert.HasCount(0, duplicates, $"Duplicate AutomationIds: {string.Join(", ", duplicates)}");
    }

    [TestMethod]
    public void AuditedInteractiveElementsExposeStableSelectors()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        var xamlNamespace = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var missingSelectors = AuditedXamlFiles
            .Select(path => Path.Combine(projectRoot, path.Replace('/', Path.DirectorySeparatorChar)))
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => InteractiveElementNames.Contains(element.Name.LocalName))
                .Where(element => element.Attributes().All(attribute => attribute.Name.LocalName != "AutomationProperties.AutomationId"))
                .Where(element => element.Attribute(xamlNamespace + "Name") is null)
                .Select(element => $"{Path.GetFileName(path)}:{element.Name.LocalName}"))
            .ToArray();

        Assert.HasCount(0, missingSelectors, $"Interactive elements without stable selectors: {string.Join(", ", missingSelectors)}");
    }

    [TestMethod]
    public void IconOnlyAndDangerousActionsHaveLocalizedAutomationMetadata()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "FolderRewind");
        var resourcePaths = new[]
        {
            Path.Combine(projectRoot, "Strings", "en-US", "Resources.resw"),
            Path.Combine(projectRoot, "Strings", "zh-CN", "Resources.resw")
        };
        var requiredKeys = new[]
        {
            "HomePage_QuickBackupButton.AutomationProperties.Name",
            "HomePage_NewConfigButton.AutomationProperties.Name",
            "HistoryPage_ToggleImportantButton.AutomationProperties.Name",
            "HistoryPage_ViewButton.AutomationProperties.Name",
            "HistoryPage_EditCommentButton.AutomationProperties.Name",
            "HistoryPage_CreateBranchButton.AutomationProperties.Name",
            "HistoryPage_RestoreButton.AutomationProperties.Name",
            "HistoryPage_RestoreButton.AutomationProperties.HelpText",
            "HistoryPage_DeleteButton.AutomationProperties.Name",
            "HistoryPage_DeleteButton.AutomationProperties.HelpText",
            "HistoryPage_BranchCheckoutButton.AutomationProperties.Name",
            "HistoryPage_BranchMoreActions.AutomationProperties.Name",
            "HistoryPage_OpenCloudSyncButton.AutomationProperties.Name",
            "HistoryPage_MaintenanceDropDown.AutomationProperties.Name",
            "HistoryPage_BranchDeleteItem.AutomationProperties.HelpText",
            "FolderManager_ConfigSettingsButton.AutomationProperties.Name",
            "FolderManager_AddFolderButton.AutomationProperties.Name",
            "FolderManager_BackupConfigButton.AutomationProperties.Name",
            "FolderManager_BackupSelectedButton.AutomationProperties.Name",
            "FolderManager_HistoryButton.AutomationProperties.Name",
            "FolderManager_ChangeCover.AutomationProperties.Name",
            "FolderManager_FavoriteToggle.AutomationProperties.Name",
            "FolderManager_MoreActions.AutomationProperties.Name",
            "FolderManager_RemoveFolder.AutomationProperties.HelpText",
            "SettingsPage_PluginUninstall.AutomationProperties.HelpText",
            "SettingsPage_PluginDeleteData.AutomationProperties.HelpText",
            "MiniWindow_PrimaryAction.AutomationProperties.Name",
            "MiniWindow_CommentBox.AutomationProperties.Name",
            "SettingsPage_StartupWidthInput.AutomationProperties.Name",
            "SettingsPage_StartupHeightInput.AutomationProperties.Name"
        };

        foreach (var resourcePath in resourcePaths)
        {
            var resources = XDocument.Load(resourcePath)
                .Root!
                .Elements("data")
                .ToDictionary(
                    element => (string)element.Attribute("name")!,
                    element => element.Element("value")?.Value ?? string.Empty,
                    StringComparer.Ordinal);
            foreach (var key in requiredKeys)
            {
                Assert.IsTrue(
                    resources.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value),
                    $"Missing localized accessibility metadata '{key}' in {resourcePath}.");
            }
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

        throw new DirectoryNotFoundException("Could not locate the FolderRewind repository root.");
    }
}
