using FolderRewind.History.Cloud;
using FolderRewind.History.Domain;
using FolderRewind.History.Storage;
using FolderRewind.Models;
using FolderRewind.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FolderRewind.ViewModels;

public sealed partial class CloudSetupViewModel
{
    private CloudConnectionProfile? _reauthenticationProfile;
    private string _reauthenticationSuffix = "";
    private Dictionary<string, string>? _reauthenticationBindings;
    private bool _allowEmptyReauthentication, _reauthenticationEmpty;
    public bool IsReauthenticating => _reauthenticationProfile is not null;
    public bool NeedsReauthentication
    {
        get
        {
            var profile = _editingProfile ?? ConfigService.CurrentConfig.CloudConnections.FirstOrDefault(p => p.Id == _selectedProfileId);
            return profile is not null && (profile.Access == CloudConnectionAccess.NeedsAuthentication || !File.Exists(profile.CredentialReference));
        }
    }
    public bool AllowEmptyReauthentication
    {
        get => _allowEmptyReauthentication;
        set { if (SetProperty(ref _allowEmptyReauthentication, value)) { _reauthenticationBindings = null; Invalidate(); } }
    }
    public string ReauthenticationSummary
    {
        get
        {
            if (_reauthenticationProfile is null) return "";
            var projects = ConfigService.CurrentConfig.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == _reauthenticationProfile.Id);
            return I18n.Format("CloudReauth_Summary", _reauthenticationProfile.DisplayName, string.Join(", ", projects.Select(p => p.Name)));
        }
    }
    public void BeginReauthentication()
    {
        var profile = _editingProfile ?? ConfigService.CurrentConfig.CloudConnections.FirstOrDefault(p => p.Id == _selectedProfileId);
        if (profile is null) throw new InvalidOperationException(I18n.GetString("CloudConnections_SelectFirst"));
        _reauthenticationProfile = CloudConnectionEditPolicy.Copy(profile);
        _saveSession.Reauthenticate(profile);
        _reauthenticationSuffix = _config?.Cloud.RemotePathSuffix
            ?? ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(p => p.Cloud.ConnectionProfileId == profile.Id)?.Cloud.RemotePathSuffix ?? "";
        _reauthenticationBindings = null;
        ConnectionName = profile.DisplayName; ShareConnection = profile.Scope == CloudConnectionScope.Shared;
        _owned?.Dispose(); _owned = null;
        ConfigPath = ""; RemoteRoot = ""; WorkingDirectory = "";
        if (!File.Exists(Executable)) Executable = ConfigService.CurrentConfig.GlobalSettings.RcloneExecutablePath;
        WizardStep = 0; RequestAutomaticUpload = RequestFirstUpload = false;
        Message = I18n.GetString("CloudReauth_Help");
        OnPropertyChanged(nameof(IsReauthenticating)); OnPropertyChanged(nameof(CanEditProfile)); OnPropertyChanged(nameof(ReauthenticationSummary));
    }
    public void CreateSeparateConnection()
    {
        _reauthenticationProfile = null; _reauthenticationBindings = null; _allowEmptyReauthentication = _reauthenticationEmpty = false;
        BeginNewConnection(); ConfigPath = ""; RemoteRoot = ""; WizardStep = 0;
        OnPropertyChanged(nameof(IsReauthenticating)); OnPropertyChanged(nameof(AllowEmptyReauthentication)); OnPropertyChanged(nameof(CanEditProfile));
    }
    private string RemoteWithPreservedSuffix(string remote) => remote + ":" + (IsReauthenticating ? _reauthenticationSuffix : "");
    private void ValidateReauthenticationBindings()
    {
        if (!IsReauthenticating) return;
        var current = ConfigService.CurrentConfig.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == _reauthenticationProfile!.Id).ToArray();
        if (_reauthenticationBindings is null || current.Length != _reauthenticationBindings.Count
            || current.Any(p => !_reauthenticationBindings.TryGetValue(p.Id, out var revision)
                || revision != p.ConfigRevision + "|" + p.Cloud.BindingRevision + "|" + p.Cloud.RemotePathSuffix))
            throw new InvalidOperationException(I18n.GetString("SettingsProject_Stale"));
    }
    public async Task ValidateReauthenticationTargetAsync(CancellationToken token)
    {
        if (!IsReauthenticating) return;
        if (ConfigService.CurrentConfig.BackupConfigs.Any(p => p.Cloud.ConnectionProfileId == _reauthenticationProfile!.Id)
            && CloudRemotePath.Split(RemoteRoot).Suffix != _reauthenticationSuffix)
            throw new CloudSetupInputException("CloudReauth_OriginalPathRequired");
        using var dependency = await OpenListRuntimeService.AcquireForConnectionAsync(DependsOnOpenList, token);
        var revision = _revision;
        var projects = ConfigService.CurrentConfig.BackupConfigs.Where(p => p.Cloud.ConnectionProfileId == _reauthenticationProfile!.Id)
            .Select(p => (p.Id, Revision: p.ConfigRevision + "|" + p.Cloud.BindingRevision + "|" + p.Cloud.RemotePathSuffix, p.Cloud.RemotePathSuffix)).ToArray();
        var original = _reauthenticationProfile!;
        using var current = CreateContext();
        var remote = CloudRemotePath.Split(RemoteRoot).Remote;
        var empty = false;
        foreach (var project in projects)
        {
            using var context = new RcloneExecutionContext(Executable, ConfigPath, WorkingDirectory, remote + ":" + project.RemotePathSuffix,
                ConfigService.RcloneTasksDirectory, CloudConnectionCatalog.IsManaged(ConfigPath));
            var expected = HistoryRepositoryDescriptor.Create(new(project.Id));
            var bytes = await new RcloneNativeHistoryTransport(context).ReadDescriptorAsync(expected.ConfigId, token);
            if (bytes is null)
            {
                if (!AllowEmptyReauthentication) throw new CloudSetupInputException("CloudReauth_RepositoryMissing");
                empty = true; continue;
            }
            if (HistoryRepositoryDescriptor.Parse(bytes).ConfigId != expected.ConfigId || !bytes.AsSpan().SequenceEqual(expected.ToCanonicalBytes()))
                throw new CloudSetupInputException("CloudReauth_RepositoryMismatch");
        }
        current.RequireUnchanged();
        if (revision != _revision || !ReferenceEquals(original, _reauthenticationProfile))
            throw new InvalidOperationException(I18n.GetString("CloudSetup_ConnectionChanged"));
        _reauthenticationBindings = projects.ToDictionary(p => p.Id, p => p.Revision);
        _reauthenticationEmpty = empty;
        ValidateReauthenticationBindings();
    }
    public async Task UpdateProjectHistoryAsync(CancellationToken token)
    {
        if (_config is null || _manageConnection) return;
        var revision = _revision;
        var signature = NativeHistoryConfigLease.Signature(_config);
        var result = await CloudSyncService.PullConfigurationHistoryAsync(_config, token);
        var current = ConfigService.CurrentConfig.BackupConfigs.FirstOrDefault(p => p.Id == _config.Id);
        if (_disposed || revision != _revision || current is null || NativeHistoryConfigLease.Signature(current) != signature) return;
        if (!result.Succeeded) throw new InvalidDataException(CloudCommandSecurity.Redact(result.Diagnostic));
        await LoadVersionsAsync(token);
        Message = I18n.Format("CloudHistory_Updated", result.DownloadedPacks);
        var attention = result.DivergentBranches.Length != 0 || result.BranchNameCollisions.Length != 0;
        if (attention) Message += "\n" + I18n.Format("CloudHistory_Conflicts", result.DivergentBranches.Length, result.BranchNameCollisions.Length);
        RecordCheck("cloud.history-updated", attention ? OnboardingCheckState.NeedsInput : OnboardingCheckState.Ready, Message);
    }
}
