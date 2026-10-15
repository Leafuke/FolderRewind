using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed record CloudConnectionDisplay(string Id, string Name, string Detail);
public sealed class CloudConnectionsViewModel : ViewModelBase
{
    public ObservableCollection<CloudConnectionDisplay> Connections { get; } = [];
    public ObservableCollection<string> Projects { get; } = [];
    private string[] _projectIds = [];
    private string? _selectedProfileId;
    private int _selectedProjectIndex = -1;
    public string? SelectedProfileId
    {
        get => _selectedProfileId;
        set { if (SetProperty(ref _selectedProfileId, value)) { OnPropertyChanged(nameof(HasSelectedProfile)); OnPropertyChanged(nameof(CanPromote)); OnPropertyChanged(nameof(SelectedConnectionText)); } }
    }
    public bool HasSelectedProfile => Connections.Any(c => c.Id == SelectedProfileId);
    public bool CanPromote => HasSelectedProfile && ConfigService.CurrentConfig.CloudConnections.Any(c => c.Id == SelectedProfileId && c.Scope != CloudConnectionScope.Shared);
    public string SelectedConnectionText => Connections.FirstOrDefault(c => c.Id == SelectedProfileId) is { } connection
        ? I18n.Format("CloudConnections_Selected", connection.Name) : I18n.GetString("CloudConnections_SelectFirst");
    public int SelectedProjectIndex { get => _selectedProjectIndex; set { if (SetProperty(ref _selectedProjectIndex, value)) OnPropertyChanged(nameof(HasProjectSelection)); } }
    public bool HasProjectSelection => SelectedProjectId is not null;
    public string? SelectedProjectId => SelectedProjectIndex >= 0 && SelectedProjectIndex < _projectIds.Length ? _projectIds[SelectedProjectIndex] : null;
    public void Refresh()
    {
        Connections.Clear(); Projects.Clear();
        var app = ConfigService.CurrentConfig;
        _projectIds = app.BackupConfigs.Select(p => p.Id).ToArray();
        foreach (var project in app.BackupConfigs) Projects.Add(project.Name);
        foreach (var profile in app.CloudConnections)
        {
            var direct = app.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == profile.Id).Select(p => p.Name);
            var storageUsers = app.CloudConnections.Where(p => CloudConnectionCatalog.SameCredentialStorage(p.CredentialReference, profile.CredentialReference)).Select(p => p.Id).ToHashSet();
            var count = app.BackupConfigs.Count(p => p.Cloud.ConnectionProfileId is { } id && storageUsers.Contains(id));
            var detail = I18n.Format("CloudConnections_Detail", I18n.GetString(profile.Scope == CloudConnectionScope.Shared ? "CloudConnections_Shared" : "CloudConnections_Project"),
                profile.RemoteName, string.Join(", ", direct), count);
            Connections.Add(new(profile.Id, profile.DisplayName, detail));
        }
        OnPropertyChanged(nameof(HasProjectSelection));
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanPromote));
        OnPropertyChanged(nameof(SelectedConnectionText));
    }
    public async Task DeleteAsync(CancellationToken token)
    {
        if (SelectedProfileId is null) return;
        await CloudConnectionCatalog.DeleteProfileAsync(SelectedProfileId, token); SelectedProfileId = null; Refresh();
    }
    public async Task PromoteAsync(CancellationToken token)
    {
        if (SelectedProfileId is null) return;
        var original = ConfigService.CurrentConfig.CloudConnections.Single(p => p.Id == SelectedProfileId);
        var replacement = ConfigSnapshotMapper.Copy(original);
        replacement.Scope = CloudConnectionScope.Shared; replacement.OwnerProjectId = null;
        await CloudConnectionCatalog.SaveProfileAsync(replacement, original.ProfileRevision, token); Refresh();
    }
}
