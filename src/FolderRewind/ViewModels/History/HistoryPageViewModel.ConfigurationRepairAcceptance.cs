#if CONFIGURATION_REPAIR_ACCEPTANCE
using FolderRewind.Models;

namespace FolderRewind.ViewModels;

public sealed partial class HistoryPageViewModel
{
    internal bool IsConfigurationRepairAcceptanceActive => _isActive;

    internal bool IsConfigurationRepairAcceptanceReady(BackupConfig config, ManagedFolder folder)
        => _isActive && !IsLoading && ReferenceEquals(_currentConfig, config) && ReferenceEquals(_currentFolder, folder);

    // Fixture setup only. The command, planner, real dialogs, save path and notifications are unchanged.
    internal void ConfigureConfigurationRepairAcceptance(BackupConfig config, ManagedFolder folder, BranchViewItem branch)
    {
        Suspend();
        _currentConfig = config;
        _currentFolder = folder;
        _selectedBranch = branch;
        _isActive = true;
    }
}
#endif
